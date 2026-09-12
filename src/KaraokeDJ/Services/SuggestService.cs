using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

/// <summary>Brano consigliato che non è in libreria: si può scaricare e mettere in coda.</summary>
public sealed class ExternalSuggestion
{
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";
    /// <summary>Testo di ricerca per il download (YouTube via yt-dlp).</summary>
    public string Query => $"{Artist} {Title}".Trim();
}

/// <summary>Suggerimenti "fuori libreria" con Claude: brani che un DJ metterebbe dopo quello in corso, esclusi quelli già in libreria.</summary>
public static class SuggestService
{
    public const string Model = "claude-opus-5";

    public static async Task<List<ExternalSuggestion>> SuggestAsync(Track current, string mode, IReadOnlyCollection<Track> library, IReadOnlyList<string> rejected, string apiKey, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        // campione della libreria per far capire il repertorio e per evitare doppioni
        var known = library
            .Where(t => !t.IsKaraoke)
            .OrderByDescending(t => t.PlayCount)
            .ThenBy(_ => Random.Shared.Next())
            .Take(120)
            .Select(t => t.Display)
            .ToList();
        var coherence = mode switch
        {
            "decade" => $"stessa decade ({(string.IsNullOrEmpty(current.Decade) ? "sconosciuta: deducila dal brano" : current.Decade)})",
            "genre" => $"stesso genere ({(string.IsNullOrEmpty(current.Genre) ? "sconosciuto: deducilo dal brano" : current.Genre)})",
            _ => "mix libero ma coerente per energia e pubblico",
        };
        var prompt = $$"""
            Sei un DJ da feste e serate karaoke in Italia. Sta suonando: "{{current.Title}}" di "{{current.Artist}}"
            (BPM {{(current.Bpm > 0 ? current.Bpm.ToString("0") : "?")}}, tonalità {{(string.IsNullOrEmpty(current.Key) ? "?" : current.Key)}}, anno {{(current.Year > 0 ? current.Year.ToString() : "?")}}, genere {{(string.IsNullOrEmpty(current.Genre) ? "?" : current.Genre)}}).
            Proponi 8 brani da mettere DOPO, che facciano ballare/cantare lo stesso pubblico: coerenza richiesta: {{coherence}}.
            Devono essere brani molto conosciuti ed esistenti davvero (artista e titolo esatti, niente invenzioni).
            NON proporre brani presenti in questa lista (già in libreria):
            {{string.Join("\n", known.Select(k => "- " + k))}}
            E NON proporre questi, già scartati dal DJ perché fuori target:
            {{string.Join("\n", rejected.TakeLast(60).Select(k => "- " + k))}}
            Rispondi SOLO con un array JSON: [{"artist":"...","title":"...","reason":"max 8 parole sul perché"}]
            """;

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = Model,
            MaxTokens = 1500,
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancellationToken: ct);

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        return Parse(text, library);
    }

    /// <summary>Estrae l'array JSON dalla risposta e scarta i brani che risultano già in libreria.</summary>
    public static List<ExternalSuggestion> Parse(string text, IReadOnlyCollection<Track> library)
    {
        var m = Regex.Match(text, @"\[[\s\S]*\]");
        if (!m.Success) return new();
        var list = new List<ExternalSuggestion>();
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var s = new ExternalSuggestion
                {
                    Artist = el.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "",
                    Title = el.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Reason = el.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "",
                };
                if (s.Title.Length == 0) continue;
                var na = SearchUtil.NormalizeForCompare(s.Artist);
                var nt = SearchUtil.NormalizeForCompare(s.Title);
                bool inLib = library.Any(x => SearchUtil.NormalizeForCompare(x.Title) == nt && (na.Length == 0 || SearchUtil.NormalizeForCompare(x.Artist).Contains(na) || na.Contains(SearchUtil.NormalizeForCompare(x.Artist))));
                if (!inLib) list.Add(s);
            }
        }
        catch { }
        return list;
    }
}

/// <summary>Assegna genere (e decade se manca) ai brani senza tag, a lotti, con Claude.</summary>
public static class GenreClassifier
{
    public static readonly string[] Genres =
    {
        "Pop", "Pop italiano", "Rock", "Dance", "House", "Techno", "Hip-hop", "Reggaeton", "Latino", "Liscio", "Disco", "Funk", "R&B", "Soul",
        "Jazz", "Blues", "Metal", "Punk", "Indie", "Cantautori", "Napoletana", "Folk", "Country", "Reggae", "Ska", "Classica", "Colonna sonora",
        "Natalizio", "Bambini", "Revival anni 60", "Revival anni 70", "Revival anni 80", "Revival anni 90", "Anni 2000", "Trap", "EDM", "Ambient",
    };

    /// <summary>Ritorna per ogni id: (genere, anno stimato o 0).</summary>
    public static async Task<Dictionary<string, (string genre, int year)>> ClassifyAsync(IReadOnlyList<Track> batch, string apiKey, CancellationToken ct)
    {
        var client = new AnthropicClient { ApiKey = apiKey };
        var lines = batch.Select((t, i) => $"{i}|{t.Artist}|{t.Title}");
        var prompt = $$"""
            Per ogni brano (formato "n|artista|titolo") indica il genere più adatto per un DJ di feste, scelto SOLO da questa lista (uno o due generi separati da "; " se il brano sta bene in entrambi):
            {{string.Join(", ", Genres)}}
            e l'anno di uscita se lo conosci (altrimenti 0). Brani:
            {{string.Join("\n", lines)}}
            Rispondi SOLO con un array JSON: [{"n":0,"genre":"Pop italiano; Dance","year":1985}, ...] con una voce per ogni n.
            """;
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = SuggestService.Model,
            MaxTokens = 4000,
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancellationToken: ct);
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text)).Trim();
        var result = new Dictionary<string, (string, int)>();
        var m = Regex.Match(text, @"\[[\s\S]*\]");
        if (!m.Success) return result;
        try
        {
            using var doc = JsonDocument.Parse(m.Value);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("n", out var nEl) || !nEl.TryGetInt32(out var n) || n < 0 || n >= batch.Count) continue;
                var g = el.TryGetProperty("genre", out var gEl) ? gEl.GetString() ?? "" : "";
                int y = el.TryGetProperty("year", out var yEl) && yEl.TryGetInt32(out var yy) ? yy : 0;
                if (g.Length > 0) result[batch[n].Id] = (g, y is > 1900 and < 2100 ? y : 0);
            }
        }
        catch { }
        return result;
    }
}
