using System.Security.Cryptography;
using KaraokeDJ.Services;

// Strumento dell'autore per le licenze Mixfonia (perpetua legata alla macchina + aggiornamenti annuali per account).
//   LicenseTool keygen <file-privato.pem>                                           → crea la coppia di chiavi e stampa la pubblica
//   LicenseTool issue <pem> <ID-MACCHINA> "<Nome>" <email-account> [anni=1] ["nota"] → chiave v2 (aggiornamenti fino a oggi+anni)
//   LicenseTool token <pem> <email-account> <fino-a AAAA-MM-GG>                     → token aggiornamenti per l'account
//   LicenseTool verify <codice> [ID-MACCHINA]                                       → verifica chiave o token con la chiave pubblica dell'app
//   LicenseTool machine                                                             → ID di questa macchina

if (args.Length == 0) { Console.WriteLine("Uso: keygen | issue | token | verify | machine"); return; }
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
        if (args.Length < 5) { Console.WriteLine("issue <pem> <ID-MACCHINA> <nome> <email> [anni] [nota]"); return; }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(args[1]));
        int years = args.Length > 5 && int.TryParse(args[5], out var y) ? y : 1;
        var until = DateTime.UtcNow.Date.AddYears(years);
        Console.WriteLine(LicenseService.Issue(ecdsa, args[3], args[2].Trim().ToUpperInvariant(), args[4], until, args.Length > 6 ? args[6] : null));
        break;
    }
    case "token":
    {
        if (args.Length < 4) { Console.WriteLine("token <pem> <email> <fino-a AAAA-MM-GG>"); return; }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(args[1]));
        Console.WriteLine(LicenseService.IssueToken(ecdsa, args[2], DateTime.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture)));
        break;
    }
    case "verify":
    {
        var tok = LicenseService.VerifyToken(args[1]);
        if (tok != null) { Console.WriteLine($"TOKEN VALIDO: account {tok.Account} · aggiornamenti fino al {tok.Until:yyyy-MM-dd} (emesso {tok.Issued:yyyy-MM-dd})"); break; }
        var machine = args.Length > 2 ? args[2].Trim().ToUpperInvariant() : LicenseService.MachineId;
        var info = LicenseService.Verify(args[1], machine);
        Console.WriteLine(info == null ? "NON VALIDA" : $"VALIDA: {info.Name} · {info.Machine} · account {(info.IsLegacy ? "(v1)" : info.Account)} · emessa {info.Issued:yyyy-MM-dd} · aggiornamenti fino al {info.UpdatesUntil:yyyy-MM-dd} {info.Note}");
        break;
    }
    case "machine":
        Console.WriteLine(LicenseService.MachineId);
        break;
}
