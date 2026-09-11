using System.Text;
using Anthropic;
using Anthropic.Models.Messages;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>
/// Trasforma i messaggi degli invitati in un testo di canzone (strofe + ritornello) pronto per Suno.
/// Con chiave API usa Claude; senza, monta un testo dai messaggi così come sono.
/// </summary>
public static class LyricsService
{
    public const string Model = "claude-opus-5";

    public static async Task<(string title, string lyrics)> GenerateAsync(Celebration c, string apiKey, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var who = c.Pronoun switch { "lei" => "una donna", "loro" => "più persone (una coppia o un gruppo)", _ => "un uomo" };
        var msgs = new StringBuilder();
        foreach (var m in c.Messages)
            msgs.AppendLine($"- {(string.IsNullOrWhiteSpace(m.From) ? "anonimo" : m.From)}: {m.Text}");

        var prompt = $"""
            Scrivi il testo di una canzone in italiano per la festa di {c.Occasion} di {c.Name} ({who}).
            Gli invitati hanno lasciato questi messaggi su di {(c.Pronoun == "loro" ? "loro" : c.Pronoun)}:
            {msgs}
            Regole:
            - Usa davvero i contenuti dei messaggi (nomi, aneddoti, soprannomi, difetti simpatici); se un messaggio è firmato, puoi citare chi lo dice.
            - Struttura: [Verse 1], [Chorus], [Verse 2], [Chorus], [Bridge], [Chorus finale]. Metti i tag tra parentesi quadre in inglese su righe separate (formato Suno).
            - Rime semplici, ritmo cantabile, tono affettuoso e divertente, mai offensivo. Ritornello con il nome di {c.Name} ripetuto.
            - Massimo 24 righe di testo oltre ai tag. Niente introduzioni o commenti.
            Rispondi SOLO con: prima riga "TITOLO: <titolo breve>", poi una riga vuota, poi il testo.
            """;

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 4000,
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancellationToken: ct);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        return SplitTitle(text, c);
    }

    /// <summary>Fallback senza AI: i messaggi diventano strofe, il ritornello è fisso.</summary>
    public static (string title, string lyrics) BuildFromMessages(Celebration c)
    {
        var sb = new StringBuilder();
        var chorus = $"[Chorus]\n{c.Name}, {c.Name}, questa sera è tutta per te\n{c.Name}, {c.Name}, siamo tutti qui con te\n";
        int i = 0;
        foreach (var chunk in c.Messages.Chunk(3))
        {
            sb.AppendLine($"[Verse {++i}]");
            foreach (var m in chunk)
            {
                sb.AppendLine(m.Text.Trim());
                if (!string.IsNullOrWhiteSpace(m.From)) sb.AppendLine($"(così dice {m.From})");
            }
            sb.AppendLine();
            sb.AppendLine(chorus);
        }
        if (i == 0) sb.AppendLine(chorus);
        sb.AppendLine("[Outro]");
        sb.AppendLine($"Auguri {c.Name}, da tutti noi!");
        return ($"Per {c.Name}", sb.ToString().Trim());
    }

    private static (string title, string lyrics) SplitTitle(string text, Celebration c)
    {
        var lines = text.Split('\n');
        if (lines.Length > 0 && lines[0].StartsWith("TITOLO:", StringComparison.OrdinalIgnoreCase))
        {
            var title = lines[0]["TITOLO:".Length..].Trim();
            var body = string.Join('\n', lines.Skip(1)).Trim();
            return (title.Length > 0 ? title : $"Per {c.Name}", body);
        }
        return ($"Per {c.Name}", text);
    }

    /// <summary>Testo da mettere in clipboard per Suno (modalità Custom): stile + testo.</summary>
    public static string ClipboardPayload(Celebration c) =>
        $"{c.Lyrics.Trim()}";
}
