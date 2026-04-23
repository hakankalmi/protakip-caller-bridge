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

        // Login packet — same shape NegroPos used. crm_id is a random token
        // that appears in every downstream event; NetGSM uses it to dedupe
        // parallel subscriptions. We don't care about the value, just need
        // one per session.
        var crmId = Guid.NewGuid().ToString("N").Substring(0, 16);
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

            accumulator.Append(Encoding.UTF8.GetString(buffer, 0, read));

            // Events are separated by "\n\n". Extract complete frames,
            // leave any partial tail for the next read.
            while (true)
            {
                var payload = accumulator.ToString();
                var idx = payload.IndexOf("\n\n", StringComparison.Ordinal);
                if (idx < 0) break;

                var frame = payload.Substring(0, idx).Trim();
                accumulator.Remove(0, idx + 2);

                if (frame.Length == 0) continue;
                await HandleFrameAsync(frame);
            }
        }
    }

    private async Task HandleFrameAsync(string json)
    {
        string? scenario = null;
        string? contextName = null;
        string? customerNum = null;

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
