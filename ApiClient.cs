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
    /// </summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cfg.DeviceToken)) return false;
        var req = new HttpRequestMessage(HttpMethod.Post, "caller-id/ping");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IngestAsync(string phoneNumber, string? line, string? deviceSerial, string? callAt, string? other, CancellationToken ct = default)
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
            }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.DeviceToken);

        using var resp = await _http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode;
    }
}

public class ClaimResponse
{
    public int DeviceId { get; set; }
    public string DeviceToken { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
}
