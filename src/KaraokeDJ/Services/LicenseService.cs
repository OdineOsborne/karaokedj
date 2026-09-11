using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KaraokeDJ.Services;

/// <summary>
/// Donationware legato alla macchina (stile Voicemeeter): l'app è sempre completa; la chiave, firmata con
/// ECDSA P-256 dalla chiave privata dell'autore, vale solo per l'ID macchina per cui è stata emessa.
/// </summary>
public static class LicenseService
{
    /// <summary>Chiave pubblica (SubjectPublicKeyInfo, base64). La privata sta solo dall'autore.</summary>
    public const string PublicKeyBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE8vOu6hQHzUJaxRLDOq/QzfDDmQQHD5EOKJnN0Xc5NKBX5kEstla7zVK0PjVji8zfX4OrOSlFDXO/pFslXG0yxA==";

    public sealed record LicenseInfo(string Name, string Machine, DateTime Issued, string? Note);

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

    /// <summary>Verifica una chiave (base64 di JSON {n,m,i,note,s}) per questa macchina. Ritorna null se non valida.</summary>
    public static LicenseInfo? Verify(string? code) => Verify(code, MachineId);

    public static LicenseInfo? Verify(string? code, string machineId)
    {
        if (string.IsNullOrWhiteSpace(code) || PublicKeyBase64.StartsWith("__")) return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(code.Trim().Replace("\n", "").Replace("\r", "").Replace(" ", "")));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string n = r.GetProperty("n").GetString() ?? "", m = r.GetProperty("m").GetString() ?? "", i = r.GetProperty("i").GetString() ?? "";
            string? note = r.TryGetProperty("note", out var no) ? no.GetString() : null;
            var sig = Convert.FromBase64String(r.GetProperty("s").GetString() ?? "");
            if (!string.Equals(m, machineId, StringComparison.OrdinalIgnoreCase)) return null;
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), out _);
            var payload = Encoding.UTF8.GetBytes(Payload(n, m, i, note));
            if (!ecdsa.VerifyData(payload, sig, HashAlgorithmName.SHA256)) return null;
            return new LicenseInfo(n, m, DateTime.TryParse(i, out var d) ? d : DateTime.MinValue, note);
        }
        catch { return null; }
    }

    public static string Payload(string name, string machine, string issued, string? note) => $"{name}\n{machine}\n{issued}\n{note ?? ""}";

    /// <summary>Usato dal tool dell'autore: firma e produce il codice licenza.</summary>
    public static string Issue(ECDsa privateKey, string name, string machine, string? note = null)
    {
        var issued = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var sig = privateKey.SignData(Encoding.UTF8.GetBytes(Payload(name, machine, issued, note)), HashAlgorithmName.SHA256);
        var json = JsonSerializer.Serialize(new { n = name, m = machine, i = issued, note, s = Convert.ToBase64String(sig) });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }
}
