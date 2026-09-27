using KaraokeDJ.Models;
using KaraokeDJ.Models.Studio;

namespace KaraokeDJ.Services.Studio;

/// <summary>
/// L'assistente del mix da DJ: dispone i brani su due corsie alternate (come i deck A e B), sceglie i punti di
/// attacco e scrive i passaggi come automazioni. Non è una scatola chiusa: il risultato sono clip ed effetti
/// normali, che nello Studio si spostano e si ritoccano a mano.
/// </summary>
public static class StudioDj
{
    public sealed record Preset(string Id, string Name, int DefaultBeats, bool NeedsBeatMatch, string Hint);

    /// <summary>I passaggi pronti. La durata è in battiti del brano in uscita.</summary>
    public static readonly Preset[] Presets =
    {
        new("blend", "Blend lungo", 32, true, "i due brani insieme, i bassi si scambiano piano"),
        new("bass", "Scambio bassi", 16, true, "volumi incrociati, i bassi passano secchi a metà"),
        new("filter", "Filtro", 16, true, "l'uscita sale in passa-alto, l'entrata si apre dal passa-basso"),
        new("echo", "Echo out", 4, false, "l'uscita finisce in eco, l'entrata parte secca sul battere"),
        new("build", "Build-up (gate + filtro)", 16, false, "l'uscita si stringe a tempo e sale, poi stacco sul drop"),
        new("reverb", "Riverbero (wash)", 8, false, "l'uscita si scioglie nel riverbero"),
        new("fade", "Dissolvenza", 8, false, "incrocio di volume semplice, va bene anche fuori tempo"),
        new("cut", "Taglio sull'1", 0, false, "stacco netto, niente sovrapposizione"),
    };

    public static Preset GetPreset(string id) => Presets.FirstOrDefault(p => p.Id == id) ?? Presets[0];

    public const string LaneA = "Deck A", LaneB = "Deck B", LaneVoice = "Voce", LaneFx = "Effetti";

    /// <summary>Clip per un brano della libreria (tutto il brano; i tagli li decide chi la usa).</summary>
    public static StudioClip ClipFor(Track t, string laneId) => new()
    {
        LaneId = laneId,
        TrackId = t.Id,
        Label = t.Display,
        InSec = 0,
        OutSec = t.DurationSec,
        Bpm = t.Bpm,
        BeatAnchorSec = t.Beats is { Length: > 0 } ? t.Beats[0] : Math.Max(0, t.BeatOffsetSec),
        AutoGainDb = double.NaN,
        FileDurationSec = t.DurationSec,
    };

    /// <summary>Un tempo sul battere più vicino della griglia del brano (secondi del file).</summary>
    public static double SnapBeat(StudioClip c, double fileSec, int beats = 1)
    {
        if (c.Bpm <= 0) return fileSec;
        double step = c.FileBeatSec * beats;
        return c.BeatAnchorSec + Math.Round((fileSec - c.BeatAnchorSec) / step) * step;
    }

    /// <summary>Clip "a tempo": velocità per arrivare al tempo del progetto (×1, ×2 o ×½, al massimo ±12 %).</summary>
    public static void ApplyWarp(StudioProject p)
    {
        foreach (var c in p.Clips)
        {
            if (!c.Warp) continue;
            if (c.Bpm <= 0 || p.Bpm <= 0) { c.Tempo = 1; continue; }
            double best = 1, bestD = double.MaxValue;
            foreach (var m in new[] { 1.0, 2.0, 0.5 })
            {
                double r = p.Bpm / (c.Bpm * m);
                if (Math.Abs(r - 1) < bestD) { bestD = Math.Abs(r - 1); best = r; }
            }
            c.Tempo = bestD <= 0.12 ? Math.Round(best, 4) : 1;
        }
    }

    public sealed class BuildOptions
    {
        /// <summary>true = solo un pezzo di ogni brano (il ritornello), false = brani interi.</summary>
        public bool Snippets { get; set; }
        public double SnippetSec { get; set; } = 45;
        /// <summary>"auto" o l'id di un preset.</summary>
        public string Transition { get; set; } = "auto";
        /// <summary>BPM del progetto: 0 = quelli del primo brano.</summary>
        public double Bpm { get; set; }
    }

    /// <summary>
    /// Dove un brano è "pieno" (dalla forma d'onda fine, 50 colonne al secondo: picco e bassi): l'ultimo secondo
    /// ancora forte prima della sfumatura finale, e la prima battuta in cui entrano i bassi.
    /// </summary>
    public static (double loudEnd, double bassIn) Levels(byte[]? fine, double dur)
    {
        if (fine == null || fine.Length < 200) return (-1, -1);
        int cols = fine.Length / 2, cps = Audio.FineWaveform.ColumnsPerSec;
        // media mobile di 1 s del picco
        var lvl = new double[cols]; double acc = 0;
        for (int i = 0; i < cols; i++) { acc += fine[i * 2]; if (i >= cps) acc -= fine[(i - cps) * 2]; lvl[i] = acc / Math.Min(i + 1, cps); }
        var sorted = lvl.Where(v => v > 2).OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return (-1, -1);
        double median = sorted[sorted.Length / 2];
        int last = cols - 1;
        while (last > cols / 2 && lvl[last] < median * 0.6) last--;
        // bassi: media di 2 s, prima volta sopra metà del massimo
        var bass = new double[cols]; acc = 0; int w = cps * 2;
        for (int i = 0; i < cols; i++) { acc += fine[i * 2 + 1]; if (i >= w) acc -= fine[(i - w) * 2 + 1]; bass[i] = acc / Math.Min(i + 1, w); }
        double bmax = bass.Max();
        int first = 0;
        while (first < cols / 2 && bass[first] < bmax * 0.5) first++;
        double bassIn = first >= cols / 2 ? -1 : Math.Max(0, first - w) / (double)cps;
        return (last / (double)cps, bassIn);
    }

    /// <summary>Costruisce il mix da una lista di brani, nell'ordine dato.</summary>
    /// <param name="fineOf">forma d'onda fine del brano (per trovare dove è pieno); null = si usano intro/finale dell'analisi</param>
    public static StudioProject Build(IList<Track> tracks, BuildOptions o, string name, Func<Track, byte[]?>? fineOf = null)
    {
        var p = new StudioProject { Name = name };
        var a = p.AddLane(LaneA, LaneKind.Music);
        var b = p.AddLane(LaneB, LaneKind.Music);
        p.AddLane(LaneVoice, LaneKind.Voice).DucksMusic = true;
        p.AddLane(LaneFx, LaneKind.Fx);
        var first = tracks.FirstOrDefault(t => t.Bpm > 0);
        p.Bpm = o.Bpm > 0 ? o.Bpm : first != null ? Math.Round(first.Bpm, 2) : 120;

        StudioClip? prev = null;
        int i = 0, auto = 0;
        foreach (var t in tracks)
        {
            var c = ClipFor(t, (i++ % 2 == 0 ? a : b).Id);
            p.Clips.Add(c);
            ApplyWarpOne(p, c);
            ChooseRange(c, t, o, isFirst: prev == null);
            if (!o.Snippets && fineOf != null)
            {
                var (loudEnd, bassIn) = Levels(fineOf(t), t.DurationSec);
                if (loudEnd > t.DurationSec * 0.5) c.ExitSec = loudEnd;       // qui il passaggio deve essere già finito
                c.LoudEndIsExit = loudEnd > t.DurationSec * 0.5;
                if (prev != null && bassIn >= 0) c.EntrySec = bassIn;
            }
            if (prev == null) { c.StartSec = 0; prev = c; continue; }
            string preset = o.Transition != "auto" ? o.Transition : AutoPreset(prev, c, auto++, o.Snippets);
            Connect(p, prev, c, preset, null);
            prev = c;
        }
        return p;
    }

    private static void ApplyWarpOne(StudioProject p, StudioClip c)
    {
        var tmp = new StudioProject { Bpm = p.Bpm, Clips = { c } };
        ApplyWarp(tmp);
    }

    /// <summary>
    /// Pezzo usato e punti del passaggio: brano intero (si entra sul drop dopo l'intro, si esce dove comincia il
    /// finale) o il ritornello più forte. I tagli veri li fa <see cref="Connect"/>, che conosce la durata del passaggio.
    /// </summary>
    private static void ChooseRange(StudioClip c, Track t, BuildOptions o, bool isFirst)
    {
        double dur = t.DurationSec > 0 ? t.DurationSec : 240;
        c.AutoRange = true;
        if (!o.Snippets)
        {
            c.EntrySec = isFirst ? 0 : IntroEnd(t);
            double outro = t.OutroStartSec > dur * 0.6 ? t.OutroStartSec : t.FinalSectionStart(dur);
            c.ExitSec = outro > 0 ? outro : Math.Max(dur * 0.8, dur - 30);
            c.InSec = 0; c.OutSec = dur;
            return;
        }
        // spezzone: il ritornello (se l'analisi l'ha trovato), altrimenti il primo pezzo "pieno" dopo l'intro
        double start = -1;
        if (t.Sections is { Count: > 1 } s && t.SectionsScore >= 1.0)
        {
            var chorus = s.FirstOrDefault(x => x.Kind == "ritornello" && x.Start > 10) ?? s.FirstOrDefault(x => x.Kind == "pieno" && x.Start > 10);
            if (chorus != null) start = chorus.Start;
        }
        if (start < 0) start = Math.Max(IntroEnd(t), dur * 0.3);
        c.EntrySec = SnapBeat(c, start, 4);
        c.ExitSec = Math.Min(dur - 2, SnapBeat(c, c.EntrySec + o.SnippetSec * c.Tempo, 4));
        c.InSec = Math.Max(0, c.EntrySec - 16 * c.FileBeatSec);
        c.OutSec = dur;
    }

    private static double IntroEnd(Track t) => t.IntroEndSec > 1 ? t.IntroEndSec : 0;

    private static string AutoPreset(StudioClip outC, StudioClip inC, int n, bool snippets)
    {
        bool matched = outC.Bpm > 0 && inC.Bpm > 0 && Math.Abs(outC.EffectiveBpm / inC.EffectiveBpm - 1) < 0.015
            || outC.Bpm > 0 && inC.Bpm > 0 && (Math.Abs(outC.EffectiveBpm / (inC.EffectiveBpm * 2) - 1) < 0.015 || Math.Abs(outC.EffectiveBpm * 2 / inC.EffectiveBpm - 1) < 0.015);
        if (!matched) return n % 3 == 2 ? "reverb" : "echo";
        var cycle = snippets ? new[] { "bass", "filter", "echo", "bass", "build" } : new[] { "blend", "bass", "filter", "blend", "echo" };
        return cycle[n % cycle.Length];
    }

    /// <summary>
    /// Mette <paramref name="inC"/> dopo <paramref name="outC"/> con il passaggio scelto: posizione (sovrapposti
    /// di N battiti, sul battere) e automazioni dei due. Si può richiamare per cambiare passaggio o durata.
    /// </summary>
    public static void Connect(StudioProject p, StudioClip outC, StudioClip inC, string presetId, int? beats)
    {
        var pr = GetPreset(presetId);
        int nb = beats ?? pr.DefaultBeats;
        double beatOut = outC.Bpm > 0 ? 60.0 / outC.EffectiveBpm : 0.5;
        double ov = Math.Min(nb * beatOut, Math.Min(outC.LengthSec, inC.LengthSec) * 0.8);
        // tagli scelti dall'assistente: l'uscita comincia a lasciare dove parte il suo finale, e il brano nuovo entra
        // in modo che il suo drop arrivi alla fine del passaggio (niente code sfumate sopra intro vuote)
        if (outC.AutoRange && outC.ExitSec > outC.InSec)
        {
            // ExitSec è l'inizio del finale (il passaggio parte lì) o, dalla forma d'onda, l'ultimo punto pieno
            // (il passaggio deve finire lì)
            double end = outC.LoudEndIsExit ? outC.ExitSec : SnapBeat(outC, outC.ExitSec, 4) + ov * outC.Tempo;
            outC.OutSec = outC.FileDurationSec > 0 ? Math.Min(outC.FileDurationSec, end) : end;
        }
        if (inC.AutoRange && inC.EntrySec > 0)
            inC.InSec = Math.Max(0, SnapBeat(inC, inC.EntrySec, 4) - ov * inC.Tempo);
        if (inC.AutoRange && inC.EntrySec > 0 && inC.InSec <= 0 && inC.Bpm > 0)
            inC.InSec = Math.Max(0, inC.BeatAnchorSec);   // intro più corta del passaggio: si parte dal primo battere
        // la fine dell'uscita cade su una battuta del brano in uscita: tagliamo lì
        if (outC.Bpm > 0 && ov > 0)
        {
            double bar = outC.FileBeatSec * 4;
            double fileEnd = outC.BeatAnchorSec + Math.Floor((outC.OutSec - outC.BeatAnchorSec) / bar + 1e-6) * bar;
            if (fileEnd > outC.InSec + ov * outC.Tempo) outC.OutSec = fileEnd;
        }
        inC.StartSec = Math.Max(0, outC.EndSec - ov);
        inC.TransitionIn = pr.Id;
        inC.TransitionInSec = ov;
        WriteTransition(outC, inC, pr.Id, ov, beatOut);
    }

    /// <summary>Le automazioni del passaggio (sostituiscono quelle che c'erano nella zona sovrapposta).</summary>
    private static void WriteTransition(StudioClip o, StudioClip n, string preset, double ov, double beat)
    {
        double e = o.LengthSec, s = e - ov;
        string[] all = AutoParams.All.Select(x => x.Id).ToArray();
        foreach (var id in all) { o.Auto(id).Replace(s, e + 60, Array.Empty<AutoPoint>()); n.Auto(id).Replace(-1, ov, Array.Empty<AutoPoint>()); }
        void O(string id, params AutoPoint[] pts) => o.Auto(id).Replace(s, e + 60, pts);
        void N(string id, params AutoPoint[] pts) => n.Auto(id).Replace(-1, ov, pts);
        static AutoPoint P(double t, double v, bool step = false) => new(t, v, step);

        switch (preset)
        {
            case "blend":
                O("vol", P(s, 1), P(s + ov * 0.6, 0.85), P(e, 0));
                O("low", P(s, 0), P(s + ov * 0.45, 0), P(s + ov * 0.55, -26));
                N("vol", P(0, 0), P(ov * 0.4, 0.85), P(ov, 1));
                N("low", P(0, -26), P(ov * 0.45, -26), P(ov * 0.55, 0));
                break;
            case "bass":
                O("vol", P(s, 1), P(s + ov * 0.5, 0.9), P(e, 0));
                O("low", P(s, 0), P(s + ov * 0.5, -26, true));
                N("vol", P(0, 0.2), P(ov * 0.5, 0.9), P(ov, 1));
                N("low", P(0, -26), P(ov * 0.5, 0, true));
                break;
            case "filter":
                O("filter", P(s, 0), P(e, 0.85));
                O("vol", P(s, 1), P(s + ov * 0.7, 0.8), P(e, 0));
                N("filter", P(0, -0.85), P(ov * 0.85, 0));
                N("vol", P(0, 0.3), P(ov * 0.5, 0.9), P(ov, 1));
                break;
            case "echo":
                // l'eco parte un battito prima della fine; il segnale diretto se ne va, restano le code
                O("echo", P(s, 0), P(Math.Max(s, e - beat), 0.8, true));
                O("dry", P(Math.Max(s, e - beat), 1), P(e, 0));
                N("vol", P(0, 0), P(ov, 1, true));
                break;
            case "build":
                O("gate", P(s, 0), P(s + ov * 0.25, 0.4), P(e, 1));
                O("filter", P(s, 0), P(e, 0.6));
                O("vol", P(s, 1), P(e - 0.01, 1), P(e, 0, true));
                N("vol", P(0, 0), P(ov, 1, true));
                break;
            case "reverb":
                O("reverb", P(s, 0), P(s + ov * 0.5, 1));
                O("vol", P(s, 1), P(e, 0));
                N("vol", P(0, 0), P(ov * 0.5, 0.3), P(ov, 1));
                break;
            case "fade":
                O("vol", P(s, 1), P(e, 0));
                N("vol", P(0, 0), P(ov, 1));
                break;
            case "cut":
            default:
                break;
        }
        // punti vuoti tolti: una corsia senza punti vale il neutro
        o.Autos.RemoveAll(a => a.Points.Count == 0);
        n.Autos.RemoveAll(a => a.Points.Count == 0);
    }

    /// <summary>
    /// Dopo aver accorciato o allungato una clip della catena del DJ: quelle dopo si spostano (come in un
    /// montaggio "a catena") e i passaggi si riscrivono alla nuova posizione.
    /// </summary>
    public static void Relayout(StudioProject p)
    {
        var lanes = p.Lanes.Where(l => l.Kind == LaneKind.Music).Select(l => l.Id).ToHashSet();
        var chain = p.Clips.Where(c => lanes.Contains(c.LaneId)).OrderBy(c => c.StartSec).ToList();
        for (int i = 1; i < chain.Count; i++)
        {
            var c = chain[i];
            if (c.TransitionIn == null) continue;
            int beats = chain[i - 1].Bpm > 0 ? (int)Math.Round(c.TransitionInSec / (60.0 / chain[i - 1].EffectiveBpm)) : GetPreset(c.TransitionIn).DefaultBeats;
            Connect(p, chain[i - 1], c, c.TransitionIn, beats);
        }
    }

    /// <summary>La scaletta con i minutaggi (per l'esportazione e per chi deve sapere cosa c'è dentro).</summary>
    public static string Setlist(StudioProject p)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(p.Name);
        foreach (var c in p.Clips.Where(c => p.Lane(c.LaneId)?.Kind == LaneKind.Music).OrderBy(c => c.StartSec))
        {
            var ts = TimeSpan.FromSeconds(c.StartSec);
            string tr = c.TransitionIn != null ? $"   ← {GetPreset(c.TransitionIn).Name.ToLowerInvariant()}" : "";
            sb.AppendLine($"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}  {c.Label}{tr}");
        }
        var end = TimeSpan.FromSeconds(p.EndSec);
        sb.AppendLine($"Durata: {(int)end.TotalMinutes:00}:{end.Seconds:00}");
        return sb.ToString();
    }
}
