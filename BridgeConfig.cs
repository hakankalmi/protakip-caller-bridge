using System.Text.Json;

namespace ProTakipCallerBridge;

/// <summary>
/// Persistent bridge configuration. Stored as JSON under
/// <c>%APPDATA%\ProTakipCallerBridge\config.json</c> — roams with the user
/// profile so pairing survives machine reinstalls (Windows restores AppData
/// from OneDrive if the user signed in with a Microsoft account).
///
/// Once a device is paired, <see cref="DeviceToken"/> is what the bridge
/// sends on every ingest as <c>Authorization: Bearer ...</c>. No JWT
/// handling needed on the bridge side — the token is opaque.
/// </summary>
public class BridgeConfig
{
    public string ApiBaseUrl { get; set; } = "https://api.protakip.com/api";
    public string HubBaseUrl { get; set; } = "https://api.protakip.com";

    public string? DeviceToken { get; set; }
    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public int? DeviceId { get; set; }
    public DateTime? PairedAt { get; set; }

    // ── Persistence ──────────────────────────────────────────────────

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProTakipCallerBridge");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "config.json");
        }
    }

    public static BridgeConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var cfg = JsonSerializer.Deserialize<BridgeConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch
        {
            // Corrupted file — fall through to a fresh default.
        }
        return new BridgeConfig();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        File.WriteAllText(FilePath, json);
    }

    public bool IsPaired => !string.IsNullOrEmpty(DeviceToken);

    public void Clear()
    {
        DeviceToken = null;
        CompanyId = null;
        CompanyName = null;
        DeviceId = null;
        PairedAt = null;
        Save();
    }
}
