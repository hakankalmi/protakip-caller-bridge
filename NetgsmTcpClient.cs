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
/// On socket drop or auth failure we wait 10 seconds and re-connect until
/// <see cref="Stop"/> is called, so transient NetGSM outages recover without
/// user intervention.
///
/// This class runs 100% inside the bridge on the secretary's PC — the backend
/// never opens its own socket, keeping server-side state at zero for all firms.
/// </summary>
public sealed class NetgsmTcpClient : IDisposable
{
    private const int ReconnectDelaySeconds = 10;

    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _password;
    private readonly Func<string, Task> _onIncomingNumber;
    private readonly Action<string>? _log;

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public string Version { get; }

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
            try
            {
                await ConnectOnceAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log($"Netgsm loop error: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;

            // Back off before reconnecting so a misconfigured credential
            // doesn't hammer NetGSM with rejected login attempts.
            try { await Task.Delay(TimeSpan.FromSeconds(ReconnectDelaySeconds), ct); }
            catch (OperationCanceledException) { return; }
        }
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
        // event akmıyor. Random.Next int32 üretir, NetGSM bunu dedup için
        // session tag olarak saklıyor.
        var crmId = new Random().Next(100_000, int.MaxValue).ToString();
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
            return;
        }
        if (json.IndexOf("Yanlis kullanici", StringComparison.OrdinalIgnoreCase) >= 0 ||
            json.IndexOf("wrong username", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Log($"Netgsm login REJECTED: {json}");
            return;
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

    private void Log(string message)
    {
        _log?.Invoke(message);
    }
}
