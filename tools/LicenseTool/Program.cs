using System.Security.Cryptography;
using KaraokeDJ.Services;

// Strumento dell'autore per le licenze VOXA (donationware legato alla macchina).
//   LicenseTool keygen <file-privato.pem>            → crea la coppia di chiavi e stampa la chiave pubblica da incorporare
//   LicenseTool issue <file-privato.pem> <ID-MACCHINA> "<Nome donatore>" ["nota"]   → stampa il codice licenza
//   LicenseTool verify <codice> <ID-MACCHINA>        → verifica con la chiave pubblica incorporata nell'app
//   LicenseTool machine                              → ID di questa macchina

if (args.Length == 0) { Console.WriteLine("Uso: keygen | issue | verify | machine"); return; }
switch (args[0])
{
    case "keygen":
    {
        var path = args.Length > 1 ? args[1] : "voxa-license-private.pem";
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(path, ecdsa.ExportECPrivateKeyPem());
        Console.WriteLine("Chiave privata salvata in: " + Path.GetFullPath(path) + "  (NON condividerla, NON metterla nel repo)");
        Console.WriteLine("PUBLIC_KEY_BASE64=" + Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        break;
    }
    case "issue":
    {
        if (args.Length < 4) { Console.WriteLine("issue <pem> <ID-MACCHINA> <nome> [nota]"); return; }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(args[1]));
        var code = LicenseService.Issue(ecdsa, args[3], args[2].Trim().ToUpperInvariant(), args.Length > 4 ? args[4] : null);
        Console.WriteLine(code);
        break;
    }
    case "verify":
    {
        var info = LicenseService.Verify(args[1], args[2].Trim().ToUpperInvariant());
        Console.WriteLine(info == null ? "NON VALIDA" : $"VALIDA: {info.Name} · {info.Machine} · {info.Issued:yyyy-MM-dd} {info.Note}");
        break;
    }
    case "machine":
        Console.WriteLine(LicenseService.MachineId);
        break;
}
