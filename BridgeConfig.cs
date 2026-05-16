using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProTakipCallerBridge;

/// <summary>
/// Persistent bridge configuration. Stored as JSON under
/// <c>%APPDATA%\ProTakipCallerBridge\config.json</c> — roams with the user
/// profile so pairing survives machine reinstalls (Windows restores AppData
/// from OneDrive if the user signed in with a Microsoft account).
///
/// <para><b>Token protection</b>: <see cref="DeviceToken"/> is held in memory
/// as plaintext for easy use (set Bearer header on every request), but
/// serialized to disk via Windows DPAPI (<c>CurrentUser</c> scope). The
/// encrypted blob can only be decrypted by the same Windows user on the same
/// machine; if config.json is exfiltrated, the attacker cannot replay the
/// token from a different PC. <see cref="DeviceTokenForSerialization"/>
/// transparently encrypts on get and decrypts on set, so callers never
/// touch the raw cipher.</para>
///
/// <para><b>Backup file</b>: every successful pair also writes
/// <c>config.backup.json</c> alongside the main file. If the primary config
/// is ever lost or corrupted (#1 Repair Clear bug, hard power-off mid-write,
/// disk error), <see cref="LoadBackup"/> can be consulted by Program.cs to
/// self-heal — validate the backup token via <c>/caller-id/ping</c> and, if
/// the server still recognises it, restore it as the primary config.</para>
///
/// <para><b>Atomic writes</b>: <see cref="Save"/> writes to <c>config.json.tmp</c>
/// first and atomically renames over the target. A power loss / process kill
/// mid-write leaves either the old file (rename hasn't happened) or the new
/// file (rename completed), never a half-written JSON that would fail to
/// parse and reset pair state on next launch.</para>
/// </summary>
public class BridgeConfig
{
    public string ApiBaseUrl { get; set; } = "https://api.protakip.com/api";
    public string HubBaseUrl { get; set; } = "https://api.protakip.com";

    /// <summary>
    /// Bearer token used on every <c>/caller-id/*</c> request. Plaintext
    /// in memory; encrypted with DPAPI when serialized — see
    /// <see cref="DeviceTokenForSerialization"/>.
    /// </summary>
    [JsonIgnore]
    public string? DeviceToken { get; set; }

    /// <summary>
    /// Serialization shim — JSON property name stays "DeviceToken" so old
    /// plaintext config files keep loading. On get we DPAPI-encrypt the
    /// in-memory plaintext; on set we attempt decrypt and fall back to
    /// treating the value as plaintext (migration path from older versions
    /// that wrote raw tokens).
    /// </summary>
    [JsonPropertyName("DeviceToken")]
    public string? DeviceTokenForSerialization
    {
        get => ProtectToken(DeviceToken);
        set => DeviceToken = UnprotectToken(value);
    }

    public Guid? CompanyId { get; set; }
    public string? CompanyName { get; set; }
    public int? DeviceId { get; set; }
    public DateTime? PairedAt { get; set; }

    // ── Paths ────────────────────────────────────────────────────────

    private static string Dir
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProTakipCallerBridge");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static string FilePath => Path.Combine(Dir, "config.json");
    private static string BackupFilePath => Path.Combine(Dir, "config.backup.json");

    // ── Load / Save ──────────────────────────────────────────────────

    public static BridgeConfig Load()
    {
        return LoadFrom(FilePath) ?? new BridgeConfig();
    }

    /// <summary>
    /// Loads the backup written alongside every successful pair. Returns
    /// null if the file doesn't exist or fails to parse. Caller (Program.cs)
    /// is expected to validate the token via <c>/caller-id/ping</c> before
    /// promoting the backup to the primary config — the backup might be
    /// stale (server-side pair removed) and blindly restoring would trick
    /// the bridge into looking paired while every ingest 401s.
    /// </summary>
    public static BridgeConfig? LoadBackup()
    {
        return LoadFrom(BackupFilePath);
    }

    private static BridgeConfig? LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<BridgeConfig>(json);
        }
        catch
        {
            // Corrupted file — caller falls through to default / backup.
            // We don't auto-rename the bad file here because Program.cs
            // might want to inspect it for diagnostics first.
            return null;
        }
    }

    /// <summary>
    /// Persists the current state to <c>config.json</c> and, when paired,
    /// also to <c>config.backup.json</c>. Atomic — partial-write of either
    /// file is impossible because we write to a <c>.tmp</c> sibling and
    /// rename over the target.
    /// </summary>
    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        AtomicWriteAllText(FilePath, json);

        // Backup only mirrors a meaningful pair state. Writing an unpaired
        // backup would defeat the whole purpose — Repair-and-cancel cycles
        // would overwrite the recoverable backup with a useless empty one.
        if (IsPaired)
        {
            try { AtomicWriteAllText(BackupFilePath, json); }
            catch { /* backup is best-effort; primary save already succeeded */ }
        }
    }

    private static void AtomicWriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        // File.Move with overwrite is atomic on NTFS (single MoveFileEx call).
        // A process kill between WriteAllText and Move leaves the OLD file
        // intact — never a half-written one.
        File.Move(tmp, path, overwrite: true);
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

    /// <summary>
    /// Overwrites in-memory state with the values from another instance
    /// and persists. Used by the self-heal path when we restore from
    /// <c>config.backup.json</c>: <c>cfg.AdoptFrom(backup); cfg.Save();</c>
    /// </summary>
    public void AdoptFrom(BridgeConfig other)
    {
        DeviceToken = other.DeviceToken;
        CompanyId = other.CompanyId;
        CompanyName = other.CompanyName;
        DeviceId = other.DeviceId;
        PairedAt = other.PairedAt;
        Save();
    }

    // ── DPAPI token protection ──────────────────────────────────────

    private static string? ProtectToken(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var cipher = ProtectedData.Protect(bytes, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            // DPAPI failure shouldn't lock the user out — fall back to
            // plaintext so the bridge still works. Logged ops can spot
            // the unencrypted token on disk during incident review.
            return plain;
        }
    }

    private static string? UnprotectToken(string? cipherOrPlain)
    {
        if (string.IsNullOrEmpty(cipherOrPlain)) return null;
        try
        {
            // DPAPI ciphertext is Base64. Old plaintext tokens are NOT valid
            // Base64 in the general case (they include '-', '_', '+' from
            // the backend's URL-safe encoding), so FromBase64String throws
            // → we catch and treat the value as already-plaintext. Migration
            // is automatic: next Save() rewrites it as a proper cipher.
            var cipher = Convert.FromBase64String(cipherOrPlain);
            var plainBytes = ProtectedData.Unprotect(cipher, optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            // Not Base64, or decrypted with a different user's key (config
            // copied between machines / user profile reset). Return as-is —
            // if it's plaintext from an older bridge it'll work; if it's
            // an undecryptable cipher the next request will 401 and the
            // self-heal flow will surface a pair prompt.
            return cipherOrPlain;
        }
    }
}
