using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Audio;
using KaraokeDJ.Audio.Studio;
using KaraokeDJ.Models;
using KaraokeDJ.Models.Studio;
using KaraokeDJ.Services;
using KaraokeDJ.Services.Studio;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Lo Studio: timeline a più corsie per preparare mix, medley e remix da esportare in un file unico.
/// Ogni modifica passa da <see cref="Edit"/>: fotografia per annulla/ripeti, salvataggio automatico e
/// aggiornamento del motore se sta suonando.
/// </summary>
public sealed partial class StudioViewModel : ObservableObject
{
    public MainViewModel Main { get; }
    public StudioProject Project { get; private set; } = new();
    private StudioRenderer? _renderer;
    private readonly Stack<string> _undo = new(), _redo = new();
    private readonly Dictionary<string, string?> _pathCache = new();

    /// <summary>La timeline deve ridisegnarsi (progetto o selezione cambiati).</summary>
    public event Action? Changed;

    public static string Dir => Path.Combine(AppPaths.Root, "studio");

    public StudioViewModel(MainViewModel main)
    {
        Main = main;
        var last = Main.Settings.StudioLastProject;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
        {
            try { Project = StudioProject.Load(last); ProjectPath = last; } catch { Project = NewProject(); }
        }
        else Project = NewProject();
        OnMaster = !Main.Engine.CueRunning;
        RefreshSearch();
    }

    private static StudioProject NewProject()
    {
        var p = new StudioProject { Name = "Nuovo mix", Bpm = 124 };
        p.AddLane(StudioDj.LaneA, LaneKind.Music);
        p.AddLane(StudioDj.LaneB, LaneKind.Music);
        p.AddLane(StudioDj.LaneVoice, LaneKind.Voice).DucksMusic = true;
        p.AddLane(StudioDj.LaneFx, LaneKind.Fx);
        return p;
    }

    [ObservableProperty] private string? _projectPath;
    [ObservableProperty] private string _status = "Aggiungi i brani dalla colonna a destra, oppure usa l'assistente per un mix da DJ";
    public string Title => $"Studio · {Project.Name}" + (ProjectPath == null ? " (non salvato)" : "");

    // ------------------------------------------------------------ modifiche, annulla, salvataggio

    /// <summary>Esegue una modifica al progetto: annullabile, salvata, e subito udibile se sta suonando.</summary>
    public void Edit(string what, Action change)
    {
        _undo.Push(Project.ToJson());
        if (_undo.Count > 200) { var keep = _undo.Take(150).Reverse().ToList(); _undo.Clear(); foreach (var k in keep) _undo.Push(k); }
        _redo.Clear();
        change();
        AfterChange(what);
    }

    /// <summary>Fotografia presa all'inizio di un trascinamento: la modifica vera arriva a pezzi, l'annulla è uno.</summary>
    public void BeginDrag() => _undo.Push(Project.ToJson());
    public void DragMoved() { _renderer?.SetProject(Project.Clone()); Changed?.Invoke(); }
    public void EndDrag(string what) { _redo.Clear(); AfterChange(what); }

    private void AfterChange(string what)
    {
        if (!string.IsNullOrEmpty(what)) Status = what;
        _renderer?.SetProject(Project.Clone());
        AutoSave();
        OnPropertyChanged(nameof(Title));
        RefreshInspector();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0) { Status = "Niente da annullare"; return; }
        _redo.Push(Project.ToJson());
        Project = StudioProject.FromJson(_undo.Pop());
        Reselect();
        AfterChange("Annullato");
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redo.Count == 0) { Status = "Niente da ripetere"; return; }
        _undo.Push(Project.ToJson());
        Project = StudioProject.FromJson(_redo.Pop());
        Reselect();
        AfterChange("Ripetuto");
    }

    private void Reselect()
    {
        if (SelectedClip != null) SelectedClip = Project.Clips.FirstOrDefault(c => c.Id == SelectedClip.Id);
    }

    private DateTime _lastAutoSave;
    private void AutoSave()
    {
        // senza nome si salva comunque, in una cartella dello Studio: un mix di un'ora non si perde per un crash
        try
        {
            ProjectPath ??= UniquePath(Project.Name);
            Project.Save(ProjectPath);
            Main.Settings.StudioLastProject = ProjectPath;
            _lastAutoSave = DateTime.UtcNow;
        }
        catch (Exception ex) { Status = "Salvataggio non riuscito: " + ex.Message; }
    }

    private static string UniquePath(string name)
    {
        var safe = string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)).Trim();
        if (safe.Length == 0) safe = "mix";
        var p = Path.Combine(Dir, safe + ".mixstudio");
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(Dir, $"{safe} ({i}).mixstudio");
        return p;
    }

    [RelayCommand]
    private void NewMix()
    {
        Stop();
        _undo.Push(Project.ToJson());
        Project = NewProject();
        ProjectPath = null;
        SelectedClip = null;
        AfterChange("Nuovo mix");
    }

    public void OpenFile(string path)
    {
        try
        {
            Stop();
            _undo.Push(Project.ToJson());
            Project = StudioProject.Load(path);
            ProjectPath = path;
            SelectedClip = null;
            AfterChange("Aperto: " + Path.GetFileName(path));
        }
        catch (Exception ex) { Status = "Non riesco ad aprire il file: " + ex.Message; }
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == Project.Name) return;
        Edit("Rinominato", () => Project.Name = name.Trim());
        // il file prende il nome nuovo (se era quello automatico nella cartella dello Studio)
        if (ProjectPath != null && Path.GetDirectoryName(ProjectPath) == Dir)
        {
            try
            {
                var np = UniquePath(Project.Name);
                File.Move(ProjectPath, np);
                ProjectPath = np;
                Main.Settings.StudioLastProject = np;
            }
            catch { }
        }
        OnPropertyChanged(nameof(Title));
    }

    // ------------------------------------------------------------ file dei brani

    public string? PathOf(StudioClip c)
    {
        if (c.TrackId == null) return c.FilePath;
        lock (_pathCache)
        {
            if (_pathCache.TryGetValue(c.TrackId, out var p)) return p;
            var t = TrackOf(c);
            p = t != null ? LibraryService.PrepareForPlayback(t).audioPath : null;
            _pathCache[c.TrackId] = p;
            return p;
        }
    }

    private Dictionary<string, Track>? _byId;
    public Track? TrackOf(StudioClip c)
    {
        if (c.TrackId == null) return null;
        if (_byId == null || _byId.Count != Main.Tracks.Count) _byId = Main.Tracks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
        return _byId.TryGetValue(c.TrackId, out var t) ? t : null;
    }

    // ------------------------------------------------------------ riproduzione

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSec;
    /// <summary>true = si sente in sala (master), false = in cuffia.</summary>
    [ObservableProperty] private bool _onMaster;
    private double _stopAt = -1;

    partial void OnOnMasterChanged(bool value)
    {
        if (!value && !Main.Engine.CueRunning) { Status = "La cuffia non è configurata (Impostazioni → Audio → Cuffia): lo Studio suona in sala"; OnMaster = true; return; }
        if (IsPlaying) Play(PositionSec);
    }

    public void Play(double fromSec, double stopAtSec = -1)
    {
        _renderer?.Stop();
        _renderer = new StudioRenderer(Project.Clone(), PathOf, sync: false);
        _renderer.Seek(Math.Max(0, fromSec));
        Main.Engine.StartStudio(_renderer, OnMaster);
        _stopAt = stopAtSec;
        IsPlaying = true;
        Status = OnMaster ? "▶ in sala" : "▶ in cuffia";
    }

    [RelayCommand]
    public void TogglePlay()
    {
        if (IsPlaying) Stop();
        else Play(PositionSec >= Project.EndSec - 0.5 ? 0 : PositionSec);
    }

    [RelayCommand]
    public void Stop()
    {
        Main.Engine.StopStudio();
        _renderer?.Stop();
        _renderer = null;
        IsPlaying = false;
    }

    public void Seek(double sec)
    {
        PositionSec = Math.Clamp(sec, 0, Math.Max(0, Project.EndSec + 5));
        if (IsPlaying) Play(PositionSec, _stopAt);
    }

    /// <summary>Dal timer della finestra: posizione di quello che si sente adesso (calcolato meno la scorta).</summary>
    public void Tick()
    {
        if (!IsPlaying || _renderer == null) return;
        if (!Main.Engine.StudioPlaying) { IsPlaying = false; return; }
        PositionSec = Math.Max(0, _renderer.PositionSec - Main.Engine.StudioBufferedSec);
        if (_renderer.MissedData) { _renderer.MissedData = false; Status = "Preparo il brano… (un attimo di silenzio al primo ascolto è normale)"; }
        if (_stopAt > 0 && PositionSec >= _stopAt) Stop();
        if (PositionSec > Project.EndSec + 4) Stop();
    }

    // ------------------------------------------------------------ selezione e ispettore

    [ObservableProperty] private StudioClip? _selectedClip;
    /// <summary>Parametro di automazione mostrato (e modificabile) sulla timeline.</summary>
    [ObservableProperty] private string _automationParam = "vol";
    public IReadOnlyList<AutoParams.Info> Params => AutoParams.All;

    partial void OnSelectedClipChanged(StudioClip? value) { RefreshInspector(); Changed?.Invoke(); }
    partial void OnAutomationParamChanged(string value) => Changed?.Invoke();

    public bool HasClip => SelectedClip != null;
    public string ClipTitle => SelectedClip?.Label ?? "";
    public string ClipInfo
    {
        get
        {
            var c = SelectedClip; if (c == null) return "";
            var t = TrackOf(c);
            string bpm = c.Bpm > 0 ? $"{c.Bpm:0.#} BPM → {c.EffectiveBpm:0.#}" : "BPM sconosciuti";
            return $"{Fmt(c.StartSec)}–{Fmt(c.EndSec)} · file {Fmt(c.InSec)}–{Fmt(c.OutSec)} · {bpm}" + (t?.KeyLabel is { Length: > 0 } k ? $" · {k}" : "");
        }
    }

    public static string Fmt(double s) { var ts = TimeSpan.FromSeconds(Math.Max(0, s)); return $"{(int)ts.TotalMinutes}:{ts.Seconds:00}"; }

    public double ClipTempoPct
    {
        get => SelectedClip == null ? 0 : Math.Round((SelectedClip.Tempo - 1) * 100, 2);
        set { var c = SelectedClip; if (c == null) return; Edit($"Tempo {value:+0.##;-0.##} %", () => { c.Warp = false; c.Tempo = Math.Clamp(1 + value / 100, 0.5, 1.5); }); }
    }
    public bool ClipWarp
    {
        get => SelectedClip?.Warp ?? false;
        set { var c = SelectedClip; if (c == null) return; Edit(value ? "A tempo col progetto" : "Tempo originale", () => { c.Warp = value; if (value) StudioDj.ApplyWarp(Project); else c.Tempo = 1; }); }
    }
    public int ClipKey
    {
        get => SelectedClip?.KeyShift ?? 0;
        set { var c = SelectedClip; if (c == null) return; Edit($"Tonalità {value:+0;-0;0}", () => c.KeyShift = Math.Clamp(value, -12, 12)); }
    }
    public double ClipGain
    {
        get => SelectedClip?.GainDb ?? 0;
        set { var c = SelectedClip; if (c == null) return; Edit($"Volume clip {value:+0.#;-0.#;0} dB", () => c.GainDb = Math.Clamp(value, -24, 12)); }
    }
    public double ClipFadeIn
    {
        get => SelectedClip?.FadeInSec ?? 0;
        set { var c = SelectedClip; if (c == null) return; Edit("Dissolvenza in entrata", () => c.FadeInSec = Math.Clamp(value, 0, 30)); }
    }
    public double ClipFadeOut
    {
        get => SelectedClip?.FadeOutSec ?? 0;
        set { var c = SelectedClip; if (c == null) return; Edit("Dissolvenza in uscita", () => c.FadeOutSec = Math.Clamp(value, 0, 30)); }
    }

    /// <summary>Le sezioni del brano (intro, strofa, ritornello…) per scegliere il pezzo con un clic.</summary>
    public IEnumerable<SectionPick> ClipSections
    {
        get
        {
            var c = SelectedClip; var t = c != null ? TrackOf(c) : null;
            if (t?.Sections is not { Count: > 1 } s) return Array.Empty<SectionPick>();
            return s.Select((x, i) => new SectionPick(x.Kind, x.Start, i + 1 < s.Count ? s[i + 1].Start : t.DurationSec));
        }
    }
    public sealed record SectionPick(string Kind, double From, double To) { public string Label => $"{Kind} {Fmt(From)}"; }

    [RelayCommand]
    private void UseSection(SectionPick? s)
    {
        var c = SelectedClip; if (c == null || s == null) return;
        Edit($"Solo «{s.Kind}» ({Fmt(s.From)}–{Fmt(s.To)})", () =>
        {
            c.InSec = StudioDj.SnapBeat(c, s.From, 4);
            c.OutSec = Math.Max(c.InSec + 1, StudioDj.SnapBeat(c, s.To, 4));
            c.AutoRange = false;
            c.Loops.RemoveAll(l => l.AtSec < c.InSec || l.AtSec >= c.OutSec);
        });
    }

    private void RefreshInspector()
    {
        foreach (var n in new[] { nameof(HasClip), nameof(ClipTitle), nameof(ClipInfo), nameof(ClipTempoPct), nameof(ClipWarp), nameof(ClipKey), nameof(ClipGain),
                     nameof(ClipFadeIn), nameof(ClipFadeOut), nameof(ClipSections), nameof(HasTransition), nameof(TransitionInfo), nameof(TransitionPreset), nameof(TransitionBeats),
                     nameof(ProjectBpm), nameof(Title), nameof(ClipLoopsLabel) })
            OnPropertyChanged(n);
    }

    // ------------------------------------------------------------ clip: aggiungere, togliere, tagliare

    /// <summary>Aggiunge un brano in fondo alla catena del DJ, sulla corsia alternata, con un passaggio automatico.</summary>
    [RelayCommand]
    public void AddToEnd(Track? t)
    {
        if (t == null || t.IsKaraoke && t.Kind != TrackKind.Audio) return;
        Edit($"Aggiunto: {t.Display}", () =>
        {
            var music = Project.Lanes.Where(l => l.Kind == LaneKind.Music).ToList();
            if (music.Count == 0) music.Add(Project.AddLane(StudioDj.LaneA, LaneKind.Music));
            var chain = Project.Clips.Where(c => music.Any(l => l.Id == c.LaneId)).OrderBy(c => c.StartSec).ToList();
            var last = chain.LastOrDefault();
            var lane = last == null ? music[0] : music.FirstOrDefault(l => l.Id != last.LaneId) ?? music[0];
            var c = StudioDj.ClipFor(t, lane.Id);
            if (Project.Bpm <= 0 && t.Bpm > 0) Project.Bpm = Math.Round(t.Bpm, 2);
            StudioDj.ApplyWarp(new StudioProject { Bpm = Project.Bpm, Clips = { c } });
            Project.Clips.Add(c);
            if (last == null) { c.StartSec = 0; }
            else
            {
                var (loudEnd, bassIn) = StudioDj.Levels(FineWaveform.Load(last.TrackId ?? ""), last.FileDurationSec);
                if (last.AutoRange == false && loudEnd > 0) { }
                c.AutoRange = true;
                c.EntrySec = bassIn > 0 ? bassIn : t.IntroEndSec;
                if (last.TrackId != null && last.OutSec >= last.FileDurationSec - 0.5 && loudEnd > last.FileDurationSec * 0.5)
                { last.AutoRange = true; last.ExitSec = loudEnd; last.LoudEndIsExit = true; }
                bool matched = last.Bpm > 0 && c.Bpm > 0 && Math.Abs(last.EffectiveBpm / c.EffectiveBpm - 1) < 0.015;
                StudioDj.Connect(Project, last, c, matched ? "blend" : "echo", null);
            }
            SelectedClip = c;
        });
    }

    /// <summary>Brano (o file) lasciato sulla timeline in un punto preciso: nessun passaggio automatico.</summary>
    public void AddAt(Track t, string laneId, double atSec)
    {
        Edit($"Aggiunto: {t.Display}", () =>
        {
            var c = StudioDj.ClipFor(t, laneId);
            if (Project.Bpm <= 0 && t.Bpm > 0) Project.Bpm = Math.Round(t.Bpm, 2);
            StudioDj.ApplyWarp(new StudioProject { Bpm = Project.Bpm, Clips = { c } });
            c.StartSec = Math.Max(0, atSec);
            Project.Clips.Add(c);
            SelectedClip = c;
        });
    }

    public void AddFileAt(string path, string laneId, double atSec)
    {
        Edit($"Aggiunto: {Path.GetFileName(path)}", () =>
        {
            double dur = 0;
            try { var (r, _) = SourceFactory.Open(path); using (r) dur = r.TotalTime.TotalSeconds; } catch { }
            if (dur <= 0) { Status = "File audio non leggibile"; return; }
            var c = new StudioClip { LaneId = laneId, FilePath = path, Label = Path.GetFileNameWithoutExtension(path), InSec = 0, OutSec = dur, FileDurationSec = dur, StartSec = Math.Max(0, atSec), Warp = false };
            Project.Clips.Add(c);
            SelectedClip = c;
        });
    }

    [RelayCommand]
    private void DeleteClip()
    {
        var c = SelectedClip; if (c == null) return;
        Edit($"Tolto: {c.Label}", () => { Project.Clips.Remove(c); SelectedClip = null; });
    }

    [RelayCommand]
    private void DuplicateClip()
    {
        var c = SelectedClip; if (c == null) return;
        Edit($"Duplicato: {c.Label}", () =>
        {
            var d = StudioProject.FromJson(new StudioProject { Clips = { c } }.ToJson()).Clips[0];
            d.Id = Guid.NewGuid().ToString("N")[..10];
            d.StartSec = c.EndSec;
            d.TransitionIn = null;
            Project.Clips.Add(d);
            SelectedClip = d;
        });
    }

    /// <summary>Taglia la clip selezionata sulla testina (sulla battuta più vicina): due clip indipendenti.</summary>
    [RelayCommand]
    private void SplitClip()
    {
        var c = SelectedClip; if (c == null) return;
        double local = PositionSec - c.StartSec;
        if (local <= 0.2 || local >= c.LengthSec - 0.2) { Status = "Metti la testina dentro la clip per tagliarla"; return; }
        Edit("Clip tagliata in due", () =>
        {
            double fileT = StudioDj.SnapBeat(c, c.FileTimeAt(local));
            var d = StudioProject.FromJson(new StudioProject { Clips = { c } }.ToJson()).Clips[0];
            d.Id = Guid.NewGuid().ToString("N")[..10];
            c.Loops.RemoveAll(l => l.AtSec >= fileT);
            d.Loops.RemoveAll(l => l.AtSec < fileT);
            double newLocal = (fileT - c.InSec) / c.Tempo + c.Loops.Sum(l => l.LengthSec * l.Repeats) / c.Tempo;
            c.OutSec = fileT; c.AutoRange = false; c.FadeOutSec = 0;
            d.InSec = fileT; d.StartSec = c.StartSec + newLocal; d.TransitionIn = null; d.AutoRange = false; d.FadeInSec = 0;
            // le automazioni restano ognuna dalla sua parte
            foreach (var a in d.Autos) { foreach (var p in a.Points) p.T -= newLocal; a.Points.RemoveAll(p => p.T < -0.01); }
            foreach (var a in c.Autos) a.Points.RemoveAll(p => p.T > newLocal + 0.01);
            Project.Clips.Add(d);
        });
    }

    // ------------------------------------------------------------ loop

    public string ClipLoopsLabel => SelectedClip is { Loops.Count: > 0 } c ? $"{c.Loops.Count} loop · +{Fmt(c.Loops.Sum(l => l.LengthSec * l.Repeats) / c.Tempo)}" : "nessun loop";

    /// <summary>Loop sulla testina: "1b×2" = una battuta ripetuta 2 volte; "r8" = roll di 1/8 di battito ×8 (sale al drop).</summary>
    [RelayCommand]
    private void AddLoop(string? kind)
    {
        var c = SelectedClip; if (c == null || kind == null) return;
        double local = PositionSec - c.StartSec;
        if (local < 0 || local >= c.LengthSec) { Status = "Metti la testina dentro la clip dove vuoi il loop"; return; }
        double beat = c.FileBeatSec;
        double at = StudioDj.SnapBeat(c, c.FileTimeAt(local), kind.StartsWith("r") ? 1 : 4);
        (double len, int rep, string name) = kind switch
        {
            "1b2" => (beat * 4, 2, "1 battuta ×2"),
            "1b4" => (beat * 4, 4, "1 battuta ×4"),
            "2b2" => (beat * 8, 2, "2 battute ×2"),
            "4b2" => (beat * 16, 2, "4 battute ×2"),
            "r2" => (beat / 2, 8, "roll 1/2 battito ×8"),
            "r4" => (beat / 4, 16, "roll 1/4 di battito ×16"),
            _ => (beat * 4, 2, "loop"),
        };
        if (at + len > c.OutSec) { Status = "Il loop uscirebbe dalla fine della clip"; return; }
        Edit($"Loop: {name}", () => { c.Loops.RemoveAll(l => Math.Abs(l.AtSec - at) < 0.01); c.Loops.Add(new StudioLoop { AtSec = at, LengthSec = len, Repeats = rep }); c.AutoRange = false; });
    }

    [RelayCommand]
    private void ClearLoops()
    {
        var c = SelectedClip; if (c == null || c.Loops.Count == 0) return;
        Edit("Loop tolti", () => c.Loops.Clear());
    }

    // ------------------------------------------------------------ effetti rapidi (scrivono automazioni)

    /// <summary>
    /// Effetto pronto sulla clip selezionata, dalla testina per N battiti (o su tutta la clip se la testina è fuori):
    /// scrive i punti di automazione, che poi si ritoccano a mano sulla timeline.
    /// </summary>
    [RelayCommand]
    private void QuickFx(string? fx)
    {
        var c = SelectedClip; if (c == null || fx == null) return;
        double beat = c.Bpm > 0 ? 60 / c.EffectiveBpm : 0.5;
        double t0 = PositionSec - c.StartSec;
        if (t0 < 0 || t0 >= c.LengthSec) t0 = Math.Max(0, c.LengthSec - 16 * beat);
        double t1 = Math.Min(c.LengthSec, t0 + 16 * beat);
        static AutoPoint P(double t, double v, bool step = false) => new(t, v, step);
        Edit(fx switch { "clear" => "Effetti tolti dalla clip", _ => "Effetto: " + fx }, () =>
        {
            switch (fx)
            {
                case "filtro su": c.Auto("filter").Replace(t0, t1, new[] { P(t0, 0), P(t1, 0.85) }); break;
                case "filtro giù": c.Auto("filter").Replace(t0, t1, new[] { P(t0, 0), P(t1, -0.85) }); break;
                case "filtro apre": c.Auto("filter").Replace(t0, t1, new[] { P(t0, -0.85), P(t1, 0) }); break;
                case "echo out":
                    c.Auto("echo").Replace(t0, t1, new[] { P(t0, 0.8, true) });
                    c.Auto("dry").Replace(t0, t1, new[] { P(t0, 1), P(Math.Min(t1, t0 + 2 * beat), 0) });
                    break;
                case "gate": c.Auto("gate").Replace(t0, t1, new[] { P(t0, 0), P(t0 + 0.01, 0.9, true), P(t1, 0.9), P(t1 + 0.01, 0, true) }); break;
                case "flanger": c.Auto("flanger").Replace(t0, t1, new[] { P(t0, 0), P(t0 + 4 * beat, 0.8), P(t1 - 2 * beat, 0.8), P(t1, 0) }); break;
                case "riverbero": c.Auto("reverb").Replace(t0, t1, new[] { P(t0, 0), P(t1, 0.9) }); break;
                case "senza bassi": c.Auto("low").Replace(t0, t1, new[] { P(t0, -26, true), P(t1, -26), P(t1 + 0.01, 0, true) }); break;
                case "build":
                    c.Auto("gate").Replace(t0, t1, new[] { P(t0, 0), P(t0 + (t1 - t0) * 0.3, 0.4), P(t1, 1) });
                    c.Auto("filter").Replace(t0, t1, new[] { P(t0, 0), P(t1, 0.6) });
                    break;
                case "clear": c.Autos.Clear(); break;
            }
            c.Autos.RemoveAll(a => a.Points.Count == 0);
        });
        AutomationParam = fx switch { "filtro su" or "filtro giù" or "filtro apre" or "build" => "filter", "echo out" => "echo", "gate" => "gate", "flanger" => "flanger", "riverbero" => "reverb", "senza bassi" => "low", _ => AutomationParam };
    }

    // ------------------------------------------------------------ passaggi

    /// <summary>La clip che entra: il passaggio selezionato è quello fra lei e la clip musicale prima.</summary>
    [ObservableProperty] private StudioClip? _transitionClip;
    partial void OnTransitionClipChanged(StudioClip? value) { RefreshInspector(); Changed?.Invoke(); }
    public bool HasTransition => TransitionClip != null && PrevInChain(TransitionClip) != null;
    public IReadOnlyList<StudioDj.Preset> Presets => StudioDj.Presets;
    public int[] BeatChoices { get; } = { 0, 4, 8, 16, 32, 64 };

    public StudioClip? PrevInChain(StudioClip c)
    {
        var music = Project.Lanes.Where(l => l.Kind == LaneKind.Music).Select(l => l.Id).ToHashSet();
        if (!music.Contains(c.LaneId)) return null;
        return Project.Clips.Where(x => x != c && music.Contains(x.LaneId) && x.StartSec < c.StartSec - 0.01).OrderByDescending(x => x.StartSec).FirstOrDefault();
    }

    public string TransitionInfo
    {
        get
        {
            var c = TransitionClip; var p = c != null ? PrevInChain(c) : null;
            if (c == null || p == null) return "";
            string bpm = p.Bpm > 0 && c.Bpm > 0 ? $"{p.EffectiveBpm:0.#} → {c.EffectiveBpm:0.#} BPM" : "BPM sconosciuti";
            return $"{p.Label}\n→ {c.Label}\n{bpm} · sovrapposti {Math.Max(0, p.EndSec - c.StartSec):0.0} s";
        }
    }

    public string TransitionPreset
    {
        get => TransitionClip?.TransitionIn ?? "blend";
        set => ApplyTransition(value, null);
    }

    public int TransitionBeats
    {
        get
        {
            var c = TransitionClip; var p = c != null ? PrevInChain(c) : null;
            if (c == null || p == null || p.Bpm <= 0) return 16;
            return (int)Math.Round(Math.Max(0, p.EndSec - c.StartSec) / (60 / p.EffectiveBpm));
        }
        set => ApplyTransition(TransitionPreset, value);
    }

    /// <summary>Cambia il passaggio: le clip dopo si spostano di conseguenza (come in un montaggio a catena).</summary>
    public void ApplyTransition(string preset, int? beats)
    {
        var c = TransitionClip; var p = c != null ? PrevInChain(c) : null;
        if (c == null || p == null) return;
        Edit($"Passaggio: {StudioDj.GetPreset(preset).Name}", () =>
        {
            double oldStart = c.StartSec, oldEnd = c.EndSec;
            StudioDj.Connect(Project, p, c, preset, beats);
            double delta = c.StartSec - oldStart;
            if (Math.Abs(delta) > 1e-6)
                foreach (var x in Project.Clips.Where(x => x != c && x != p && x.StartSec > oldStart + 0.01)) x.StartSec += delta;
        });
    }

    [RelayCommand]
    private void ListenTransition()
    {
        var c = TransitionClip; var p = c != null ? PrevInChain(c) : null;
        if (c == null || p == null) return;
        Play(Math.Max(0, c.StartSec - 8), Math.Max(p.EndSec, c.StartSec) + 8);
    }

    // ------------------------------------------------------------ progetto

    public double ProjectBpm
    {
        get => Math.Round(Project.Bpm, 2);
        set
        {
            if (value < 40 || value > 220 || Math.Abs(value - Project.Bpm) < 0.001) return;
            Edit($"Tempo del progetto {value:0.##} BPM", () =>
            {
                Project.Bpm = value;
                StudioDj.ApplyWarp(Project);
                StudioDj.Relayout(Project);
            });
        }
    }

    // ------------------------------------------------------------ libreria e assistente

    [ObservableProperty] private string _searchText = "";
    public ObservableCollection<Track> Results { get; } = new();
    /// <summary>Scaletta per l'assistente: i brani, nell'ordine in cui si vuole il mix.</summary>
    public ObservableCollection<Track> Setlist { get; } = new();
    [ObservableProperty] private bool _buildSnippets;
    [ObservableProperty] private double _snippetSec = 45;
    [ObservableProperty] private string _buildTransition = "auto";
    public IEnumerable<KeyValuePair<string, string>> BuildTransitions =>
        new[] { new KeyValuePair<string, string>("auto", "Automatico (variato)") }.Concat(StudioDj.Presets.Select(p => new KeyValuePair<string, string>(p.Id, p.Name)));

    partial void OnSearchTextChanged(string value) => RefreshSearch();

    private void RefreshSearch()
    {
        Results.Clear();
        var q = SearchText.Trim();
        IEnumerable<Track> src = Main.Tracks.Where(t => t.Kind == TrackKind.Audio);
        if (q.Length >= 2)
            src = src.Where(t => t.Display.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Genre.Contains(q, StringComparison.OrdinalIgnoreCase));
        else src = src.Where(t => t.PlayedThisSession || t.PlayCount > 0).OrderByDescending(t => t.LastPlayedUtc ?? DateTime.MinValue);
        foreach (var t in src.Take(80)) Results.Add(t);
    }

    [RelayCommand] private void SetlistAdd(Track? t) { if (t != null && !Setlist.Contains(t)) Setlist.Add(t); }
    [RelayCommand] private void SetlistRemove(Track? t) { if (t != null) Setlist.Remove(t); }
    [RelayCommand] private void SetlistUp(Track? t) { if (t == null) return; int i = Setlist.IndexOf(t); if (i > 0) Setlist.Move(i, i - 1); }
    [RelayCommand] private void SetlistDown(Track? t) { if (t == null) return; int i = Setlist.IndexOf(t); if (i >= 0 && i < Setlist.Count - 1) Setlist.Move(i, i + 1); }
    [RelayCommand] private void SetlistFromQueue() { foreach (var q in Main.Queue) if (!q.Track.IsKaraoke && !Setlist.Contains(q.Track)) Setlist.Add(q.Track); }
    [RelayCommand] private void SetlistClear() => Setlist.Clear();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _busyProgress;

    /// <summary>L'assistente: costruisce il mix dalla scaletta. Le clip musicali di prima vengono sostituite.</summary>
    [RelayCommand]
    private async Task BuildMix()
    {
        if (Setlist.Count < 2) { Status = "Metti almeno due brani nella scaletta dell'assistente"; return; }
        IsBusy = true; Status = "L'assistente prepara il mix (forme d'onda dei brani)…";
        try
        {
            var tracks = Setlist.ToList();
            var opt = new StudioDj.BuildOptions { Snippets = BuildSnippets, SnippetSec = SnippetSec, Transition = BuildTransition, Bpm = 0 };
            var built = await Task.Run(() => StudioDj.Build(tracks, opt, Project.Name,
                t => FineWaveform.GetOrComputeAsync(t.Id, LibraryService.PrepareForPlayback(t).audioPath, CancellationToken.None).GetAwaiter().GetResult()));
            Stop();
            Edit($"Mix costruito: {tracks.Count} brani, {Fmt(built.EndSec)}", () =>
            {
                // voce ed effetti già messi restano; le corsie musicali si rifanno
                var keepLanes = Project.Lanes.Where(l => l.Kind != LaneKind.Music).ToList();
                var keepClips = Project.Clips.Where(c => keepLanes.Any(l => l.Id == c.LaneId)).ToList();
                var voiceLanes = built.Lanes.Where(l => l.Kind != LaneKind.Music).ToList();
                built.Lanes.RemoveAll(l => l.Kind != LaneKind.Music);
                built.Lanes.AddRange(keepLanes.Count > 0 ? keepLanes : voiceLanes);
                built.Clips.AddRange(keepClips);
                built.Name = Project.Name;
                Project = built;
                SelectedClip = null; TransitionClip = null;
            });
            PositionSec = 0;
        }
        catch (Exception ex) { Status = "L'assistente non ce l'ha fatta: " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------ esportazione

    public async Task ExportAsync(string wavPath, bool mp3)
    {
        Stop();
        IsBusy = true; BusyProgress = 0; Status = "Esporto…";
        var prog = new Progress<double>(v => { BusyProgress = v; Status = $"Esporto… {v * 100:0} %"; });
        try
        {
            var snap = Project.Clone();
            var outPath = await Task.Run(() => StudioExport.Export(snap, PathOf, wavPath, mp3, prog, CancellationToken.None));
            Project.LastExportPath = outPath;
            AutoSave();
            Status = $"Esportato: {outPath} (con la scaletta accanto)";
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{outPath}\""); } catch { }
        }
        catch (Exception ex) { Status = "Esportazione non riuscita: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
