using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ProTakipCallerBridge;

/// <summary>
/// NetGSM Bulut Santral TCP subscriber — mirrors the NegroPos anaForm.cs
/// <c>tcpBaglan</c>/<c>VeriGeldi</c> flow. One raw TCP socket to
/// <c>crmsntrl.netgsm.com.tr:9110</c>. On connect we send a login packet:
/// <code>{ command: "login", crm_id: "&lt;rnd&gt;", username, password }</code>
/// terminated with <c>\n\n</c>. The server then pushes JSON event objects
/// asynchronously. We react to:
///
///   - <c>scenario == "Inbound_call"</c>     → yeni dış arama
///   - <c>scenario == "InboundtoPBX"</c>     → dış hattan santrale yönlendirilen
///   - <c>context_name</c> contains "mesai"  → mesai-içi IVR
///
/// Whichever fires first, the <c>customer_num</c> field is extracted and
/// handed to the supplied callback (which posts it to /caller-id/ingest).
///
/// On socket drop we back off exponentially (10s → 30s → 60s → 120s → 300s
/// ceiling) and re-connect until <see cref="Stop"/> is called, so transient
/// NetGSM outages recover without user intervention. On auth rejection (wrong
/// username/password) we wait the ceiling delay since retrying every 10s would
/// just spam NetGSM with rejected logins until they IP-ban us — and the
/// credentials can only be fixed by the user on the web panel.
///
/// State transitions are published via <see cref="StateChanged"/> so the
/// status form can show "Bağlanıyor / Bağlı / Yanlış kullanıcı adı veya
/// parola / Bağlantı kesildi" — without UI feedback the secretary stares
/// at an amber "Bağlanıyor…" forever and never knows credentials were
/// wrong.
///
/// This class runs 100% inside the bridge on the secretary's PC — the backend
/// never opens its own socket, keeping server-side state at zero for all firms.
/// </summary>
public sealed class NetgsmTcpClient : IDisposable
{
    /// <summary>
    /// Reconnect delay schedule (seconds). Each consecutive failure picks the
    /// next bucket; final value (300s) is the ceiling. Resets to bucket #0
    /// the moment we receive a successful "login Successful" frame.
    /// </summary>
    private static readonly int[] ReconnectScheduleSeconds = { 10, 30, 60, 120, 300 };

    /// <summary>
    /// Delay used after an authentication rejection. Same as the ceiling — no
    /// point retrying every 10s when the credentials are wrong; user has to
    /// fix them on the web panel. Bridge keeps polling the backend
    /// <c>/caller-id/pbx-config</c> every 60s, so the moment Hakan updates
    /// credentials Program.cs tears this client down and starts a new one
    /// with the new password, which short-circuits this wait.
    /// </summary>
    private const int AuthRejectedRetryDelaySeconds = 300;

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly Func<string, Task> _onIncomingNumber;
    private readonly Action<string>? _log;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>
    /// Number of consecutive failed connection attempts. Resets to 0 on a
    /// successful login frame (NOT on TCP connect — NetGSM can accept the
    /// socket and then reject credentials, which is a failure for our
    /// purposes). Used to pick the next bucket in
    /// <see cref="ReconnectScheduleSeconds"/>.
    /// </summary>
    private int _consecutiveFailures;

    public string Version { get; }

    /// <summary>
    /// Raised every time the subscriber transitions between connection
    /// states. Always fires on the worker task — handlers must marshal to
    /// the UI thread themselves. <c>detail</c> is a human-readable hint
    /// (e.g. "Yanlış kullanıcı adı veya parola", "30 sn sonra yeniden
    /// bağlanılacak"); null if the state itself is self-explanatory.
    /// </summary>
    public event Action<NetgsmConnectionState, string?>? StateChanged;

    public NetgsmTcpClient(
        string host,
        int port,
        string username,
        string password,
        string version,
        Func<string, Task> onIncomingNumber,
        Action<string>? log = null)
    {
        _host = host;
        _port = port;
        _username = username;
        _password = password;
        Version = version;
        _onIncomingNumber = onIncomingNumber;
        _log = log;
    }

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RaiseState(NetgsmConnectionState.Connecting, null);

            bool authRejected = false;
            string? errorDetail = null;

            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (AuthRejectedException ex)
            {
                authRejected = true;
                errorDetail = ex.Message;
            }
            catch (Exception ex)
            {
                errorDetail = ex.Message;
                Log($"Netgsm loop error: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            _consecutiveFailures++;

            int delaySeconds;
            if (authRejected)
            {
                // Wrong credentials — UI shows "Yanlış kullanıcı adı veya
                // parola", stop hammering NetGSM. Bridge reconfigures itself
                // when Hakan updates credentials on the web panel
                // (ReconcileNetgsmAsync sees a version bump → tears us down
                // and starts a new client → this wait is interrupted by
                // CancellationToken).
                delaySeconds = AuthRejectedRetryDelaySeconds;
                RaiseState(NetgsmConnectionState.AuthRejected, errorDetail);
            }
            else
            {
                delaySeconds = NextReconnectDelaySeconds();
                RaiseState(NetgsmConnectionState.Disconnected,
                    $"{delaySeconds} sn sonra yeniden bağlanılacak");
            }

            Log($"Netgsm reconnecting in {delaySeconds}s (consecutive failures: {_consecutiveFailures}, authRejected: {authRejected})");

            try { await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Picks the reconnect delay based on how many failures have piled up.
    /// Past the end of <see cref="ReconnectScheduleSeconds"/> we stay at the
    /// ceiling (300s) until login succeeds, at which point the counter is
    /// reset to zero by <see cref="HandleFrameAsync"/>.
    /// </summary>
    private int NextReconnectDelaySeconds()
    {
        // _consecutiveFailures has already been incremented for this attempt,
        // so the FIRST failure (= 1) maps to bucket 0 (= 10s).
        var idx = Math.Min(_consecutiveFailures - 1, ReconnectScheduleSeconds.Length - 1);
        return ReconnectScheduleSeconds[Math.Max(0, idx)];
    }

    /// <summary>
    /// Thrown by <see cref="HandleFrameAsync"/> when NetGSM rejects the login
    /// packet — bubbles up through <see cref="ConnectOnceAsync"/> to
    /// <see cref="RunAsync"/>, which then uses the longer ceiling delay
    /// instead of the normal backoff schedule.
    /// </summary>
    private sealed class AuthRejectedException : Exception
    {
        public AuthRejectedException(string detail) : base(detail) { }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        Log($"Netgsm dialing {_host}:{_port}");
        await client.ConnectAsync(_host, _port, ct);
        Log("Netgsm connected");

        using var stream = client.GetStream();

        // Login packet — NegroPos paritesi. crm_id INTEGER string olmalı
        // (rnd.Next().ToString()). Guid/hex verince NetGSM login'i sessizce
        // reject ediyor; cevap/hata frame'i dahi atmıyor, socket açık kalıp
        // event akmıyor. Random.Shared (.NET 6+) thread-safe ve seeded —
        // birden çok bridge aynı saniyede başlatılırsa session collision
        // riskini düşürür (eski `new Random()` time-seeded'idi).
        var crmId = Random.Shared.Next(100_000, int.MaxValue).ToString();
        var loginPayload = JsonSerializer.Serialize(new
        {
            command = "login",
            crm_id = crmId,
            username = _username,
            password = _password,
        });
        var loginBytes = Encoding.UTF8.GetBytes(loginPayload + "\n\n");
        await stream.WriteAsync(loginBytes, ct);
        Log("Netgsm login sent");

        var buffer = new byte[8192];
        var accumulator = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log($"Netgsm read error: {ex.Message}");
                return;
            }

            if (read == 0)
            {
                Log("Netgsm socket closed by server");
                return;
            }

            // NegroPos paritesi: NetGSM bazı mesajları düz string olarak
            // ('login Successful', 'Yanlis kullanici adi veya sifre')
            // bazılarını JSON olarak \n\n ile / bazılarını sadece \n ile
            // gönderiyor. Her incoming chunk'ı tek bir mesaj olarak işlemek
            // + trailing newline'ları strip etmek en güvenilir yaklaşım.
            // Accumulator'ı tamamen bırakıyoruz, her read bir "mesaj".
            var chunk = Encoding.UTF8.GetString(buffer, 0, read);
            accumulator.Append(chunk);

            // Mesaj(lar)ı \n karakterleriyle böl ve her parçayı ayrı handle et.
            // Birden çok frame aynı chunk içinde gelirse hepsi parse edilir.
            // Partial JSON (açılmış { ama kapanmamış) uçları bir sonraki
            // read'e bırakılır.
            while (true)
            {
                var payload = accumulator.ToString();
                if (payload.Length == 0) break;

                int end = FindFrameEnd(payload);
                if (end < 0)
                {
                    // Kapanmamış JSON — sonraki chunk ile birleşmesini bekle
                    break;
                }

                var frame = payload.Substring(0, end).Trim('\n', '\r', ' ', '\t');
                accumulator.Remove(0, end);

                if (frame.Length == 0) continue;

                var preview = frame.Length > 500 ? frame.Substring(0, 500) + "…" : frame;
                Log($"Netgsm frame: {preview}");

                await HandleFrameAsync(frame);
            }
        }
    }

    /// <summary>
    /// Accumulator'daki ilk tam frame'in bitiş index'ini döner (exclusive).
    /// Üç format destekli:
    ///   1. JSON ({...}) — brace balance ile bitiş yakalanır, nested OK.
    ///   2. \n\n separator ile biten blok.
    ///   3. Tek \n ile biten düz text ("login Successful", "Yanlis ...").
    /// Hiçbiri yoksa -1; caller sonraki read'i bekler.
    /// </summary>
    private static int FindFrameEnd(string buf)
    {
        int i = 0;
        while (i < buf.Length && (buf[i] == '\n' || buf[i] == '\r' || buf[i] == ' ' || buf[i] == '\t')) i++;
        if (i >= buf.Length) return buf.Length; // sadece whitespace

        if (buf[i] == '{')
        {
            int depth = 0;
            bool inStr = false;
            for (int j = i; j < buf.Length; j++)
            {
                char c = buf[j];
                if (inStr)
                {
                    if (c == '\\') { j++; continue; }
                    if (c == '"') inStr = false;
                }
                else
                {
                    if (c == '"') inStr = true;
                    else if (c == '{') depth++;
                    else if (c == '}')
                    {
                        depth--;
                        if (depth == 0) return j + 1;
                    }
                }
            }
            return -1; // JSON kapanmadı
        }

        // Plain-text satır — ilk \n'de kes
        int nl = buf.IndexOf('\n', i);
        if (nl < 0) return -1;
        return nl + 1;
    }

    private async Task HandleFrameAsync(string json)
    {
        string? scenario = null;
        string? contextName = null;
        string? customerNum = null;

        // Plain-text NetGSM response'ları — JSON değil, direkt log'la.
        if (json.IndexOf("login Successful", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log("Netgsm login OK");
            _consecutiveFailures = 0;
            RaiseState(NetgsmConnectionState.Connected, null);
            return;
        }
        if (json.IndexOf("Yanlis kullanici", StringComparison.OrdinalIgnoreCase) >= 0 ||
            json.IndexOf("wrong username", StringComparison.OrdinalIgnoreCase) >= 0 ||
            json.IndexOf("invalid password", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log($"Netgsm login REJECTED: {json}");
            // Throw → unwind to RunAsync, which uses AuthRejectedRetryDelaySeconds
            // and raises NetgsmConnectionState.AuthRejected on the UI side.
            throw new AuthRejectedException("Yanlış kullanıcı adı veya parola");
        }
        // JSON değilse ve tanıdık bir plain-text de değilse — log at, parse atla.
        if (json.Length == 0 || json[0] != '{')
        {
            Log($"Netgsm unrecognized frame (non-JSON): {json}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            // The login-success JSON variant — server sometimes wraps the
            // string in {"status":"Success","message":"login Successful"}.
            // Check both ways; without this the JSON variant slips past the
            // plain-text check above and Connected state never fires.
            if (doc.RootElement.TryGetProperty("status", out var statusProp) &&
                statusProp.ValueKind == JsonValueKind.String &&
                string.Equals(statusProp.GetString(), "Success", StringComparison.OrdinalIgnoreCase) &&
                doc.RootElement.TryGetProperty("message", out var msgProp) &&
                msgProp.ValueKind == JsonValueKind.String &&
                (msgProp.GetString()?.IndexOf("login", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0)
            {
                Log("Netgsm login OK (JSON variant)");
                _consecutiveFailures = 0;
                RaiseState(NetgsmConnectionState.Connected, null);
                return;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "scenario":
                        scenario = prop.Value.GetString();
                        break;
                    case "context_name":
                        contextName = prop.Value.GetString();
                        break;
                    case "customer_num":
                        customerNum = prop.Value.GetString();
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Netgsm bad JSON frame: {ex.Message}");
            return;
        }

        // Only ring the bridge when NetGSM actually says "incoming call".
        // Other scenarios (agent state, keep-alive, etc.) flow through
        // the same socket and should be ignored so we don't fire ghost
        // popups on the secretary's screen.
        bool isIncoming =
            scenario == "Inbound_call" ||
            scenario == "InboundtoPBX" ||
            (contextName?.Contains("mesai", StringComparison.OrdinalIgnoreCase) ?? false);

        // Temporary — arama yapıldığında neden match etmediğimizi
        // anlayabilmek için her frame'in özetini log'la. Sadece scenario
        // veya customer_num göründüğünde (keep-alive gürültüsü olmadan).
        if (!string.IsNullOrEmpty(scenario) || !string.IsNullOrEmpty(customerNum))
        {
            Log($"Netgsm parse: scenario='{scenario}' context='{contextName}' num='{customerNum}' isIncoming={isIncoming}");
        }

        if (!isIncoming) return;
        if (string.IsNullOrWhiteSpace(customerNum)) return;

        Log($"Netgsm ring: {customerNum} (scenario={scenario})");
        try
        {
            await _onIncomingNumber(customerNum!);
        }
        catch (Exception ex)
        {
            Log($"Netgsm ingest callback threw: {ex.Message}");
        }
    }

    private void RaiseState(NetgsmConnectionState state, string? detail)
    {
        try
        {
            StateChanged?.Invoke(state, detail);
        }
        catch (Exception ex)
        {
            // A buggy subscriber must not bring down the reconnect loop.
            Log($"StateChanged handler threw: {ex.Message}");
        }
    }

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}

/// <summary>
/// Connection states published via <see cref="NetgsmTcpClient.StateChanged"/>.
/// Mapped 1:1 to the <see cref="NetgsmState"/> the status form understands —
/// kept as a separate enum so the client doesn't depend on WinForms types.
/// </summary>
public enum NetgsmConnectionState
{
    /// <summary>TCP connect + login packet sent, waiting for response.</summary>
    Connecting,
    /// <summary>"login Successful" frame received — events should start flowing.</summary>
    Connected,
    /// <summary>NetGSM rejected the login. Manual fix required (credentials).</summary>
    AuthRejected,
    /// <summary>Socket dropped or unrecoverable error; reconnect timer running.</summary>
    Disconnected,
}
