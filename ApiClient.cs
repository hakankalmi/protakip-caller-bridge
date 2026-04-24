using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace ProTakipCallerBridge;

/// <summary>
/// Thin HTTP client — the bridge only ever hits two endpoints:
/// <c>/caller-id/claim</c> (pair once) and <c>/caller-id/ingest</c>
/// (every incoming call). Both return in milliseconds; no retries here,
/// the caller decides what to do on failure.
/// </summary>
public class ApiClient
{
    private readonly HttpClient _http;
    private readonly BridgeConfig _cfg;

    public ApiClient(BridgeConfig cfg)
    {
        _cfg = cfg;
        _http = new HttpClient
        {
            BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public async Task<ClaimResponse?> ClaimAsync(string pairCode, string? deviceSerial, CancellationToken ct = default)
    {
        var payload = new
        {
            pairCode,
            deviceSerial,
            deviceInfo = $"{Environment.MachineName} · win · bridge 1.0",
        };

        using var resp = await _http.PostAsJsonAsync("caller-id/claim", payload, ct);
        if (!resp.IsSuccessStatusCode) return null;

        return await resp.Content.ReadFromJsonAsync<ClaimResponse>(cancellationToken: ct);
    }

    /// <summary>
    /// Heartbeat — bumps the device's LastSeenAt on the server so the web
    /// panel shows us as "Bağlı" during idle periods. Fire every ~60s.
    /// Returns (success, diagnosticDetail). Detail is populated on
    /// failure so Program.cs can log WHY ping didn't succeed (401 auth,
    /// 404 route, DNS, etc.) instead of a useless "non-success".
    /// </summary>
    public async Task<(bool ok, string detail)> PingAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cfg.DeviceToken)) return (false, "no device token");
        var req = new HttpRequestMessage(HttpMethod.Post, "caller-id/ping");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, string.Empty);
            return (false, $"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    public async Task<bool> IngestAsync(
        string phoneNumber,
        string? line,
        string? deviceSerial,
        string? callAt,
        string? other,
        string? source = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cfg.DeviceToken)) return false;

        var req = new HttpRequestMessage(HttpMethod.Post, "caller-id/ingest")
        {
            Content = JsonContent.Create(new
            {
                phoneNumber,
                line,
                deviceSerial,
                callAt,
                other,
                source,
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);

        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Pulls NetGSM credentials for the paired company. Bridge uses the
    /// returned <c>version</c> string to decide whether to tear down and
    /// re-open its TCP socket — any change on the server side (enabled flag
    /// flipped, credentials updated, provider swapped) bumps the version.
    /// Returns null on auth/network failure; the bridge keeps its last known
    /// config in that case instead of killing an otherwise healthy socket.
    /// </summary>
    public async Task<PbxConfigResponse?> GetPbxConfigAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cfg.DeviceToken)) return null;
        var req = new HttpRequestMessage(HttpMethod.Get, "caller-id/pbx-config");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<PbxConfigResponse>(cancellationToken: ct);
        }
        catch
        {
            return null;
        }
    }
}

public class ClaimResponse
{
    public int DeviceId { get; set; }
    public string DeviceToken { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
}

public class PbxConfigResponse
{
    public bool Enabled { get; set; }
    public string? Provider { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Version { get; set; }
}
