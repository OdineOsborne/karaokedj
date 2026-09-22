using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaraokeDJ.Services;

/// <summary>
/// Licenza Mixfonia: perpetua e legata alla macchina, con aggiornamenti inclusi fino a una data (1 anno dall'acquisto,
/// rinnovabile). Un account (email) può avere fino a 3 macchine e un solo abbonamento agli aggiornamenti:
/// il rinnovo produce un "token aggiornamenti" firmato per l'account, valido su tutte le sue macchine.
/// Tutto è firmato ECDSA P-256 dalla chiave privata dell'autore (che sta nel cloud e nella cartella privata).
/// Formati (base64 di JSON):
///   chiave v1 (donationware): {n,m,i,note,s}            payload "n\nm\ni\nnote"
///   chiave v2:                {n,m,i,note,a,u,s}        payload "n\nm\ni\nnote\na\nu"
///   token aggiornamenti:      {t:"upd",a,u,i,s}         payload "upd\na\nu\ni"
/// </summary>
public static class LicenseService
{
    /// <summary>Chiave pubblica (SubjectPublicKeyInfo, base64). La privata sta solo dall'autore.</summary>
    public const string PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8vOu6hQHzUJaxRLDOq/QzfDDmQQHD5EOKJnN0Xc5NKBX5kEstla7zVK0PjVji8zfX4OrOSlFDXO/pFslXG0yxA==";

    public const int MaxSeats = 3;

    /// <summary>Account = email (minuscola). UpdatesUntil = ultimo giorno in cui le release pubblicate sono installabili.</summary>
    public sealed record LicenseInfo(string Name, string Machine, DateTime Issued, string? Note, string Account, DateTime UpdatesUntil)
    {
        public bool IsLegacy => Account.Length == 0;
    }

    public sealed record UpdatesToken(string Account, DateTime Until, DateTime Issued);

    private static string? _machineId;

    /// <summary>ID stabile della macchina: hash di MachineGuid + serial del volume di sistema, formato VOXA-XXXX-XXXX-XXXX.</summary>
    public static string MachineId
    {
        get
        {
            if (_machineId != null) return _machineId;
            string guid = "";
            try
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                guid = k?.GetValue("MachineGuid")?.ToString() ?? "";
            }
            catch { }
            string serial = "";
            try
            {
                var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                serial = new DriveInfo(root).VolumeLabel + "|" + root;
                // serial di volume vero tramite kernel32
                if (GetVolumeInformation(root, null, 0, out uint sn, out _, out _, null, 0)) serial = sn.ToString("X8");
            }
            catch { }
            var raw = SHA256.HashData(Encoding.UTF8.GetBytes(guid + "|" + serial + "|VOXA"));
            var s = Base32(raw)[..12];
            _machineId = $"VOXA-{s[..4]}-{s[4..8]}-{s[8..12]}";
            return _machineId;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string root, StringBuilder? name, int nameSize, out uint serial, out uint maxLen, out uint flags, StringBuilder? fs, int fsSize);

    private static string Base32(byte[] data)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder();
        int bits = 0, value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b; bits += 8;
            while (bits >= 5) { sb.Append(alphabet[(value >> (bits - 5)) & 31]); bits -= 5; }
        }
        return sb.ToString();
    }

    public static string NormalizeAccount(string? email) => (email ?? "").Trim().ToLowerInvariant();

    private static JsonElement? Decode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || PublicKeyBase64.StartsWith("__")) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "")));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch { return null; }
    }

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";

    private static bool CheckSig(string payload, string sigB64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(payload), Convert.FromBase64String(sigB64), HashAlgorithmName.SHA256);
        }
        catch { return false; }
    }

    private static DateTime ParseDay(string s) => DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d.Date : DateTime.MinValue;

    /// <summary>Verifica una chiave (v1 o v2) per questa macchina. Ritorna null se non valida.</summary>
    public static LicenseInfo? Verify(string? code) => Verify(code, MachineId);

    public static LicenseInfo? Verify(string? code, string machineId)
    {
        var r0 = Decode(code);
        if (r0 == null) return null;
        var r = r0.Value;
        if (Str(r, "t").Length > 0) return null; // è un token, non una chiave
        string n = Str(r, "n"), m = Str(r, "m"), i = Str(r, "i"), a = NormalizeAccount(Str(r, "a")), u = Str(r, "u");
        string? note = r.TryGetProperty("note", out var no) && no.ValueKind == JsonValueKind.String ? no.GetString() : null;
        if (!string.Equals(m, machineId, StringComparison.OrdinalIgnoreCase)) return null;
        bool v2 = r.TryGetProperty("a", out _);
        var payload = v2 ? Payload(n, m, i, note, a, u) : PayloadV1(n, m, i, note);
        if (!CheckSig(payload, Str(r, "s"))) return null;
        var issued = ParseDay(i);
        // v1 (donationware): trattata come acquisto con 1 anno di aggiornamenti dalla data di emissione
        var until = v2 ? ParseDay(u) : issued.AddYears(1);
        return new LicenseInfo(n, m, issued, note, a, until);
    }

    /// <summary>Verifica un token aggiornamenti (rinnovo annuale dell'account). Ritorna null se non valido.</summary>
    public static UpdatesToken? VerifyToken(string? code)
    {
        var r0 = Decode(code);
        if (r0 == null) return null;
        var r = r0.Value;
        if (Str(r, "t") != "upd") return null;
        string a = NormalizeAccount(Str(r, "a")), u = Str(r, "u"), i = Str(r, "i");
        if (!CheckSig(TokenPayload(a, u, i), Str(r, "s"))) return null;
        return new UpdatesToken(a, ParseDay(u), ParseDay(i));
    }

    public static string PayloadV1(string name, string machine, string issued, string? note) => $"{name}\n{machine}\n{issued}\n{note ?? ""}";
    public static string Payload(string name, string machine, string issued, string? note, string account, string until) => $"{name}\n{machine}\n{issued}\n{note ?? ""}\n{account}\n{until}";
    public static string TokenPayload(string account, string until, string issued) => $"upd\n{account}\n{until}\n{issued}";

    /// <summary>Usato dal tool dell'autore: firma e produce la chiave v2.</summary>
    public static string Issue(ECDsa privateKey, string name, string machine, string account, DateTime updatesUntil, string? note = null)
    {
        var issued = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var a = NormalizeAccount(account); var u = updatesUntil.ToString("yyyy-MM-dd");
        var sig = privateKey.SignData(Encoding.UTF8.GetBytes(Payload(name, machine, issued, note, a, u)), HashAlgorithmName.SHA256);
        var json = JsonSerializer.Serialize(new { n = name, m = machine, i = issued, note, a, u, s = Convert.ToBase64String(sig) });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Usato dal tool dell'autore: token aggiornamenti per un account.</summary>
    public static string IssueToken(ECDsa privateKey, string account, DateTime until)
    {
        var issued = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var a = NormalizeAccount(account); var u = until.ToString("yyyy-MM-dd");
        var sig = privateKey.SignData(Encoding.UTF8.GetBytes(TokenPayload(a, u, issued)), HashAlgorithmName.SHA256);
        var json = JsonSerializer.Serialize(new { t = "upd", a, u, i = issued, s = Convert.ToBase64String(sig) });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
