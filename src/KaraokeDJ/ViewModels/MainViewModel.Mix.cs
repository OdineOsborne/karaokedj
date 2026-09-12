using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Motore di mix "da DJ": tracce sovrapposte, beat allineati sulla griglia (partenza sul battere, aggancio di fase continuo),
/// tempo che scivola dal brano in uscita a quello in arrivo, tecniche di passaggio diverse (blend lungo, scambio bassi,
/// filtro, echo-out, taglio sull'1, brake) scelte in automatico o a mano. Punti di ingresso/uscita ricavati dall'energia dei bassi.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Piano di un passaggio fra due deck.</summary>
    public sealed class MixPlan
    {
        public DeckViewModel Out = null!, In = null!;
        public string Technique = "blend";      // blend | bass | filter | echo | cut | brake | fade
        public int Bars;                        // durata del passaggio in battute (del brano in uscita, al tempo di mix)
        public double LenSec;                   // durata in secondi di uscita
        public double OutStartSec;              // posizione (file) del brano in uscita dove parte il passaggio (un battere)
        public double InStartSec;               // posizione (file) del brano in arrivo allineata a OutStartSec (un battere)
        public double InAudibleAt;              // frazione del passaggio in cui il brano in arrivo diventa udibile (0 = subito)
        public double OutBeat, InBeat;          // durata di un battito (secondi di file)
        public double OutAnchor, InAnchor;      // ancora della griglia (secondi di file)
        public bool BeatMatch;                  // tempo agganciato e fase bloccata
        public double InTempo0, InTempo1;       // tempo del brano in arrivo: agganciato → naturale
        public double OutTempo0, OutTempo1;     // tempo del brano in uscita: naturale → BPM del brano in arrivo
        public double Dir;                      // +1 verso B, −1 verso A
        public string Why = "";
        // stato di esecuzione
        public double T;                        // secondi di uscita trascorsi dall'inizio del passaggio (può essere negativo: attesa del battere)
        public bool Started, EchoFired, BrakeFired, Done;
        public double PhaseCorr;                // correzione di tempo per l'aggancio di fase (fattore additivo)
    }

    private MixPlan? _mix;
    public bool IsMixing => _mix is { Done: false };
    [ObservableProperty] private string _mixStatus = "";

    private static readonly string[] AutoTechniques = { "blend", "bass", "filter", "bass", "blend", "echo" };

    // ------------------------------------------------------------------ analisi: punti di mix dai bassi

    /// <summary>Energia media dei bassi per battuta, allineata alla griglia (indice = battuta dall'ancora).</summary>
    private static double[] BarEnergy(byte[] fine, double anchor, double beat, double duration)
    {
        double bar = beat * 4;
        int bars = (int)((duration - anchor) / bar);
        var e = new double[Math.Max(0, bars)];
        int cps = FineWaveform.ColumnsPerSec, cols = fine.Length / 2;
        for (int k = 0; k < e.Length; k++)
        {
            int c0 = (int)((anchor + k * bar) * cps), c1 = (int)((anchor + (k + 1) * bar) * cps);
            double s = 0; int n = 0;
            for (int c = Math.Max(0, c0); c < Math.Min(cols, c1); c++) { s += fine[c * 2 + 1]; n++; }
            e[k] = n > 0 ? s / n : 0;
        }
        return e;
    }

    /// <summary>Punto migliore per far entrare il brano: il battere dove i bassi "partono" (fine intro / primo drop).</summary>
    private static double FindMixIn(Track t, byte[]? fine, double anchor, double beat)
    {
        if (fine == null || t.Bpm <= 0) return t.IntroEndSec > 1 ? t.IntroEndSec : Math.Max(0, anchor);
        var e = BarEnergy(fine, anchor, beat, t.DurationSec);
        if (e.Length < 8) return Math.Max(0, anchor);
        double max = e.Max(); if (max <= 0) return Math.Max(0, anchor);
        for (int k = 0; k + 3 < e.Length && k < e.Length / 2; k++)
            if ((e[k] + e[k + 1] + e[k + 2] + e[k + 3]) / 4 >= 0.55 * max) return anchor + k * beat * 4;
        return t.IntroEndSec > 1 ? SnapToBar(t.IntroEndSec, anchor, beat) : Math.Max(0, anchor);
    }

    /// <summary>Punto migliore per iniziare a far uscire il brano: il battere dove i bassi "calano" (inizio uscita), lasciando almeno <paramref name="minBarsLeft"/> battute.</summary>
    private static double FindMixOut(Track t, byte[]? fine, double anchor, double beat, int minBarsLeft)
    {
        double bar = beat * 4;
        double latest = t.DurationSec - minBarsLeft * bar - 0.5;
        if (fine == null || t.Bpm <= 0)
            return SnapToBar(t.OutroStartSec > 0 && t.OutroStartSec < latest ? t.OutroStartSec : latest, anchor, beat);
        var e = BarEnergy(fine, anchor, beat, t.DurationSec);
        if (e.Length < 8) return SnapToBar(latest, anchor, beat);
        double max = e.Max(); if (max <= 0) return SnapToBar(latest, anchor, beat);
        // dall'ultima battuta forte in poi: l'uscita comincia lì
        int lastStrong = -1;
        for (int k = e.Length - 1; k >= e.Length / 2; k--) if (e[k] >= 0.55 * max) { lastStrong = k; break; }
        double candidate = lastStrong >= 0 ? anchor + (lastStrong + 1) * bar : latest;
        // preferiamo un punto su una frase (multiplo di 4 battute) e non troppo presto
        candidate = Math.Min(candidate, latest);
        candidate = Math.Max(candidate, t.DurationSec * 0.5);
        return SnapToBar(candidate, anchor, beat);
    }

    private static double SnapToBar(double sec, double anchor, double beat)
    {
        double bar = beat * 4;
        double k = Math.Round((sec - anchor) / bar);
        return Math.Max(0, anchor + k * bar);
    }

    private static double NextBar(double sec, double anchor, double beat)
    {
        double bar = beat * 4;
        double k = Math.Ceiling((sec - anchor) / bar - 1e-6);
        return anchor + k * bar;
    }

    /// <summary>Prepara griglia (forma d'onda fine + fase) per un brano, in background. Ritorna la forma d'onda fine se disponibile.</summary>
    private async Task<byte[]?> EnsureGridAsync(Track t)
    {
        if (t.IsKaraoke || t.Bpm <= 0) return null;
        byte[]? fine;
        try
        {
            var (audioPath, _) = LibraryService.PrepareForPlayback(t);
            fine = await FineWaveform.GetOrComputeAsync(t.Id, audioPath, CancellationToken.None);
        }
        catch { return null; }
        if (fine != null && t.BeatOffsetSec < 0 && !t.BeatManual)
        {
            double off = BeatGrid.EstimateOffset(fine, t.Bpm);
            if (off >= 0) { t.BeatOffsetSec = Math.Round(off, 3); Library.Save(t); }
        }
        return fine;
    }

    // ------------------------------------------------------------------ pianificazione

    /// <summary>Sceglie tecnica, durata e punti di mix per il passaggio da <paramref name="outgoing"/> a <paramref name="incoming"/>.</summary>
    private async Task<MixPlan> PlanMixAsync(DeckViewModel outgoing, DeckViewModel incoming, bool startNow)
    {
        var to = outgoing.Track!; var ti = incoming.Track!;
        var plan = new MixPlan { Out = outgoing, In = incoming, Dir = incoming == DeckB ? 1 : -1 };
        bool karaoke = to.IsKaraoke || ti.IsKaraoke;
        double outBpmNative = to.Bpm, inBpmNative = ti.Bpm;

        // BPM del brano in arrivo riportati vicino a quelli in uscita (×1, ×2, ×½)
        double inNative = inBpmNative;
        if (inBpmNative > 0 && outBpmNative > 0)
            foreach (var mult in new[] { 2.0, 0.5 }) if (Math.Abs(inBpmNative * mult - outBpmNative) < Math.Abs(inNative - outBpmNative)) inNative = inBpmNative * mult;
        double bpmDiffPct = (inNative > 0 && outBpmNative > 0) ? Math.Abs(inNative / outBpmNative - 1) * 100 : 100;
        bool canBeatMatch = !karaoke && BpmMatch && !BpmLock && inNative > 0 && outBpmNative > 0 && bpmDiffPct <= 12;

        // tecnica
        string style = TransitionStyle;
        string tech;
        if (karaoke || style == "fade") tech = "fade";
        else if (!canBeatMatch) tech = Random.Shared.Next(2) == 0 ? "echo" : "brake";
        else if (style is "blend" or "bass" or "filter" or "echo" or "cut" or "brake") tech = style;
        else if (style == "glide") tech = "blend";
        else
        {
            // auto: rap/hip-hop → taglio sull'1; generi vicini → blend/bassi/filtro; altrimenti echo
            bool rap = ti.Genres.Concat(to.Genres).Any(g => g.Contains("hip", StringComparison.OrdinalIgnoreCase) || g.Contains("rap", StringComparison.OrdinalIgnoreCase) || g.Contains("trap", StringComparison.OrdinalIgnoreCase));
            tech = rap ? (Random.Shared.Next(3) == 0 ? "echo" : "cut") : AutoTechniques[Random.Shared.Next(AutoTechniques.Length)];
        }
        plan.Technique = tech;
        plan.Bars = tech switch { "blend" => 16, "bass" => 16, "filter" => 8, "echo" => 4, "cut" => 2, "brake" => 2, _ => 0 };
        plan.InAudibleAt = tech switch { "echo" => 0.75, "cut" => 0.5, "brake" => 0.5, _ => 0 };
        plan.BeatMatch = canBeatMatch && tech != "fade";

        if (tech == "fade" || outBpmNative <= 0)
        {
            plan.LenSec = Math.Max(1, CrossfadeSeconds);
            double outStart = to.OutroStartSec > 0 && to.OutroStartSec < to.DurationSec - plan.LenSec ? to.OutroStartSec : to.DurationSec - plan.LenSec - 0.5;
            plan.OutStartSec = startNow ? outgoing.Deck.PositionSec : Math.Max(outgoing.Deck.PositionSec, outStart);
            plan.InStartSec = ti.IntroEndSec > 2 && !ti.IsKaraoke ? Math.Max(0, ti.IntroEndSec - 1) : 0;
            plan.Why = "dissolvenza semplice";
            return plan;
        }

        // griglie
        var fineOut = await EnsureGridAsync(to);
        var fineIn = await EnsureGridAsync(ti);
        double outTempoNow = outgoing.Deck.Tempo;
        plan.OutBeat = 60.0 / outBpmNative;
        plan.OutAnchor = to.BeatOffsetSec >= 0 ? to.BeatOffsetSec : 0;
        plan.InBeat = 60.0 / inBpmNative;
        plan.InAnchor = ti.BeatOffsetSec >= 0 ? ti.BeatOffsetSec : 0;
        // battute in arrivo: se il brano in arrivo è a ×2/×½ la sua "battuta" dura il doppio/metà in confronto
        double inBarScale = inNative / inBpmNative; // 2 → il brano in arrivo è lento (½): una battuta di mix = mezza sua battuta
        double barOutFile = plan.OutBeat * 4;
        plan.LenSec = plan.Bars * barOutFile / outTempoNow;

        // punto di uscita: sul battere, dove calano i bassi (o almeno "Bars" battute prima della fine)
        double outStartFile = FindMixOut(to, fineOut, plan.OutAnchor, plan.OutBeat, plan.Bars + 1);
        double now = outgoing.Deck.PositionSec;
        if (startNow || outStartFile < now + 0.2) outStartFile = NextBar(now + 0.15 * outTempoNow, plan.OutAnchor, plan.OutBeat);
        plan.OutStartSec = outStartFile;

        // punto di ingresso: il drop del brano in arrivo deve cadere a InAudibleAt del passaggio
        double dropIn = FindMixIn(ti, fineIn, plan.InAnchor, plan.InBeat);
        double barInFile = plan.InBeat * 4 / inBarScale;                     // una battuta "di mix" in secondi di file del brano in arrivo
        double barsBeforeDrop = plan.Bars * plan.InAudibleAt;                 // battute di mix dall'inizio del passaggio al drop
        if (plan.InAudibleAt == 0)
        {
            // tecniche che entrano subito: il drop arriva alla fine del passaggio (l'intro suona sotto l'uscita)
            barsBeforeDrop = plan.Bars;
        }
        double inStart = dropIn - barsBeforeDrop * barInFile;
        if (inStart < 0) { inStart = SnapToBar(0, plan.InAnchor, plan.InBeat / inBarScale); if (inStart < 0) inStart = 0; }
        plan.InStartSec = inStart;

        // tempo: in arrivo agganciato ai BPM effettivi dell'uscita, poi scivola al suo tempo naturale
        double outBpmEff = outBpmNative * outTempoNow;
        plan.InTempo0 = plan.BeatMatch ? Math.Clamp(outBpmEff / inNative, 0.75, 1.25) : 1.0;
        plan.InTempo1 = 1.0;
        plan.OutTempo0 = outTempoNow;
        plan.OutTempo1 = plan.BeatMatch ? Math.Clamp(outTempoNow * inNative / outBpmEff, 0.75, 1.25) : outTempoNow;
        plan.Why = $"{TechniqueLabel(tech)} · {plan.Bars} battute · {outBpmEff:0} → {inNative:0} BPM" + (plan.BeatMatch ? " · beat agganciati" : "");
        return plan;
    }

    private static string TechniqueLabel(string t) => t switch
    {
        "blend" => "blend lungo", "bass" => "scambio bassi", "filter" => "filtro", "echo" => "echo-out", "cut" => "taglio sull'1", "brake" => "brake", _ => "dissolvenza",
    };

    // ------------------------------------------------------------------ esecuzione

    /// <summary>Avvia il passaggio pianificato: il brano in arrivo parte subito (muto) in modo che il suo battere coincida con quello in uscita.</summary>
    private void StartMix(MixPlan p)
    {
        _mix = p;
        _crossfadeTarget = null;
        var inDeck = p.In.Deck; var outDeck = p.Out.Deck;
        // tempi iniziali
        inDeck.Tempo = p.InTempo0;
        p.In.TempoPercent = (int)Math.Round((p.InTempo0 - 1) * 100);
        // il brano in arrivo parte ora: quando il brano in uscita raggiunge OutStartSec, quello in arrivo deve essere a InStartSec
        double waitOut = Math.Max(0, (p.OutStartSec - outDeck.PositionSec) / Math.Max(0.5, outDeck.Tempo)); // secondi di uscita
        double inPos = p.InStartSec - waitOut * inDeck.Tempo;
        if (inPos < 0) { inPos = 0; }
        inDeck.Seek(inPos);
        if (p.Technique == "bass") p.In.EqLow = -14;
        if (p.Technique == "filter") p.In.FilterValue = -0.85;
        if (p.InAudibleAt > 0) p.In.GainDb = -24; // entra "sotto zero", poi torna al livello
        Crossfader = -p.Dir;                        // tutto sull'uscita
        inDeck.Play();
        p.T = -waitOut;
        p.Started = true;
        _lastOutgoingTrack = p.Out.Track;
        MixStatus = $"Mix: {p.Why} · parte tra {waitOut:0.0} s";
        StatusText = MixStatus;
    }

    /// <summary>Chiamato dal timer: fa avanzare il passaggio (crossfader, EQ, filtro, echo, tempo, aggancio di fase).</summary>
    private void TickMix(double dt)
    {
        var p = _mix;
        if (p == null || p.Done) return;
        if (!p.Out.IsPlaying && p.T < 0) { AbortMix("il brano in uscita si è fermato"); return; }
        if (!p.In.IsPlaying) { AbortMix("il brano in arrivo si è fermato"); return; }
        p.T += dt;
        if (p.T < 0) { PhaseLock(p); return; } // attesa del battere: teniamo comunque la fase

        double prog = Math.Clamp(p.T / p.LenSec, 0, 1);
        double s = prog * prog * (3 - 2 * prog);

        // tempo: scivola fra i due brani (mantenendo l'aggancio: entrambi cambiano insieme)
        if (p.BeatMatch)
        {
            double tIn = p.InTempo0 + (p.InTempo1 - p.InTempo0) * s;
            double tOut = p.OutTempo0 + (p.OutTempo1 - p.OutTempo0) * s;
            p.Out.Deck.Tempo = tOut;
            p.In.Deck.Tempo = tIn + p.PhaseCorr;
            PhaseLock(p);
        }

        // livello del brano in arrivo per le tecniche "a scatto"
        if (p.InAudibleAt > 0)
        {
            double g = prog < p.InAudibleAt ? -24 : 0;
            if (Math.Abs(p.In.GainDb - g) > 0.01) p.In.GainDb = g;
        }

        switch (p.Technique)
        {
            case "blend":
                Crossfader = -p.Dir + 2 * p.Dir * s;
                p.In.EqLow = -10 * (1 - Math.Clamp(prog / 0.6, 0, 1));
                p.Out.EqLow = -10 * Math.Clamp((prog - 0.4) / 0.6, 0, 1);
                break;
            case "bass":
                Crossfader = -p.Dir + 2 * p.Dir * s;
                // scambio dei bassi secco a metà (sul battere più vicino ci pensa il progresso a battute)
                p.In.EqLow = prog < 0.5 ? -14 : 0;
                p.Out.EqLow = prog < 0.5 ? 0 : -14;
                break;
            case "filter":
                Crossfader = -p.Dir + 2 * p.Dir * s;
                p.Out.FilterValue = 0.9 * Math.Clamp(prog / 0.9, 0, 1);            // high-pass che sale
                p.In.FilterValue = -0.85 * (1 - Math.Clamp(prog / 0.8, 0, 1));    // low-pass che si apre
                break;
            case "echo":
                if (prog < p.InAudibleAt) Crossfader = -p.Dir;
                else Crossfader = -p.Dir + 2 * p.Dir * Math.Clamp((prog - p.InAudibleAt) / 0.08, 0, 1);
                if (!p.EchoFired && prog >= p.InAudibleAt - 0.02) { p.EchoFired = true; p.Out.EchoOutCommand.Execute(null); }
                break;
            case "cut":
                Crossfader = prog < p.InAudibleAt ? -p.Dir : p.Dir;
                break;
            case "brake":
                if (!p.BrakeFired && prog >= p.InAudibleAt - 0.12) { p.BrakeFired = true; p.Out.BrakeCommand.Execute(null); }
                Crossfader = prog < p.InAudibleAt ? -p.Dir : p.Dir;
                break;
            default: // fade
                Crossfader = -p.Dir + 2 * p.Dir * s;
                break;
        }

        if (prog >= 1) FinishMix(p);
    }

    /// <summary>Aggancio di fase: misura lo scarto fra i battiti dei due deck e corregge il tempo del brano in arrivo (come il nudge sul jog).</summary>
    private void PhaseLock(MixPlan p)
    {
        if (!p.BeatMatch) return;
        double outBeatOut = p.OutBeat / Math.Max(0.5, p.Out.Deck.Tempo);          // durata del battito in secondi di uscita
        double phOut = ((p.Out.Deck.PositionSec - p.OutAnchor) / p.OutBeat) % 1.0; if (phOut < 0) phOut += 1;
        double phIn = ((p.In.Deck.PositionSec - p.InAnchor) / p.InBeat) % 1.0; if (phIn < 0) phIn += 1;
        // ×2/×½: confrontiamo sul battito più corto, la fase mod 1 basta
        double err = phIn - phOut; if (err > 0.5) err -= 1; if (err < -0.5) err += 1;   // in battiti, + = in arrivo avanti
        if (Math.Abs(err) < 0.01) { p.PhaseCorr *= 0.8; return; }
        // correzione proporzionale: 0,25 battiti di scarto → ±2 % di tempo; piccolo e continuo, niente salti udibili
        p.PhaseCorr = Math.Clamp(-err * 0.08, -0.025, 0.025);
        if (p.T < 0) p.In.Deck.Tempo = p.InTempo0 + p.PhaseCorr;
        _ = outBeatOut;
    }

    private void FinishMix(MixPlan p)
    {
        p.Done = true;
        _mix = null;
        Crossfader = p.Dir;
        var o = p.Out; var i = p.In;
        if (o.IsPlaying) o.Deck.Pause();
        if (o.HasTrack) o.Deck.Seek(0);
        if (_autoMixTriggeredFor == o) _autoMixTriggeredFor = null;
        o.EqLow = 0; o.FilterValue = 0; o.GainDb = 0; o.Deck.Fx.Reset(); o.TempoPercent = 0;
        if (o.EchoOutRunning) o.CancelEchoOutCommand.Execute(null);
        i.EqLow = 0; i.FilterValue = 0; i.GainDb = 0;
        i.Deck.Tempo = p.InTempo1; i.TempoPercent = (int)Math.Round((p.InTempo1 - 1) * 100);
        MixStatus = "";
        StatusText = $"Passaggio completato ({TechniqueLabel(p.Technique)})";
    }

    private void AbortMix(string why)
    {
        var p = _mix; _mix = null;
        if (p == null) return;
        p.Done = true;
        p.In.EqLow = 0; p.In.FilterValue = 0; p.In.GainDb = 0;
        p.Out.EqLow = 0; p.Out.FilterValue = 0; p.Out.GainDb = 0;
        MixStatus = "";
        StatusText = "Passaggio interrotto: " + why;
    }

    /// <summary>Passaggio "da DJ" verso il deck indicato, se possibile; altrimenti dissolvenza classica.</summary>
    private async Task MixToAsync(DeckViewModel outgoing, DeckViewModel incoming, bool startNow)
    {
        if (outgoing.Track == null || incoming.Track == null) { StartCrossfade(incoming == DeckB ? 1 : -1); return; }
        if (!outgoing.IsPlaying) { Crossfader = incoming == DeckB ? 1 : -1; incoming.Deck.Play(); return; }
        var plan = await PlanMixAsync(outgoing, incoming, startNow);
        StartMix(plan);
    }

    /// <summary>Prepara in anticipo la griglia del prossimo in coda (forma d'onda fine + fase), così al momento del mix è tutto pronto.</summary>
    private string? _prefetchedId;
    private async void PrefetchNextGrid()
    {
        var next = Queue.FirstOrDefault()?.Track;
        if (next == null || next.Id == _prefetchedId) return;
        _prefetchedId = next.Id;
        try { await EnsureGridAsync(next); } catch { }
    }
}
