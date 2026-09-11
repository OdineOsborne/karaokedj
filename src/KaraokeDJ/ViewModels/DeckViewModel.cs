using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Audio;
using KaraokeDJ.Cdg;
using KaraokeDJ.Models;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

public sealed partial class DeckViewModel : ObservableObject
{
    private CdgDecoder? _cdg;
    private DateTime _lastCdgRender = DateTime.MinValue;

    public DeckViewModel(Deck deck)
    {
        Deck = deck;
        CdgBitmap = new WriteableBitmap(CdgDecoder.Width, CdgDecoder.Height, 96, 96, PixelFormats.Bgra32, null);
        deck.TrackEnded += () => Application.Current?.Dispatcher.BeginInvoke(() => TrackEnded?.Invoke(this));
        deck.Seeked += () => Application.Current?.Dispatcher.BeginInvoke(() => Seeked?.Invoke(this));
    }

    public Deck Deck { get; }
    public string Name => Deck.Name;
    public WriteableBitmap CdgBitmap { get; }

    public event Action<DeckViewModel>? TrackEnded;
    public event Action<DeckViewModel>? Seeked;
    public event Action<DeckViewModel>? TrackLoaded;
    /// <summary>Il brano è stato effettivamente suonato (oltre 10 s).</summary>
    public event Action<DeckViewModel, Track>? Played;
    private bool _playedMarked;

    [ObservableProperty] private Track? _track;
    [ObservableProperty] private string _title = "— vuoto —";
    [ObservableProperty] private string _artist = "";
    [ObservableProperty] private string _singer = "";
    [ObservableProperty] private string _kindLabel = "";
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _hasTrack;
    [ObservableProperty] private bool _isKaraoke;
    [ObservableProperty] private bool _isCdg;
    [ObservableProperty] private bool _isVideo;
    [ObservableProperty] private double _positionSec;
    [ObservableProperty] private double _durationSec;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _positionLabel = "0:00";
    [ObservableProperty] private string _remainingLabel = "-0:00";
    [ObservableProperty] private bool _isEnding;
    [ObservableProperty] private int _keyShift;
    [ObservableProperty] private int _tempoPercent;
    [ObservableProperty] private double _volume = 1.0;
    [ObservableProperty] private string? _videoPath;
    [ObservableProperty] private string? _loadError;
    [ObservableProperty] private bool _keyLock = true;
    [ObservableProperty] private string _bpmLabel = "";
    [ObservableProperty] private string _keyDisplay = "";
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private byte[]? _waveform;
    [ObservableProperty] private double _introFraction;
    [ObservableProperty] private double _outroFraction = 1;
    [ObservableProperty] private string _introOutroLabel = "";
    [ObservableProperty] private bool _inOutro;
    [ObservableProperty] private double _levelL;
    [ObservableProperty] private double _levelR;

    // ---------------------------------------------------------------- effetti
    [ObservableProperty] private bool _fxVisible;
    [ObservableProperty] private bool _vocalRemove;
    [ObservableProperty] private double _vocalStrength = 1.0;
    [ObservableProperty] private double _filterValue;
    [ObservableProperty] private bool _filterOn = true;
    [ObservableProperty] private double _filterResonance = 0.15;
    partial void OnFilterOnChanged(bool v) => Deck.Fx.FilterOn = v;
    partial void OnFilterResonanceChanged(double v) => Deck.Fx.FilterResonance = (float)v;
    [ObservableProperty] private bool _echoOn;
    [ObservableProperty] private double _echoBeats = 0.5;   // frazione di battuta
    [ObservableProperty] private double _echoFeedback = 0.45;
    [ObservableProperty] private double _echoMix = 0.35;
    [ObservableProperty] private bool _reverbOn;
    [ObservableProperty] private double _reverbSize = 0.6;
    [ObservableProperty] private double _reverbMix = 0.25;
    [ObservableProperty] private bool _flangerOn;
    [ObservableProperty] private bool _echoOutRunning;

    // EQ (dB): -30 = kill
    [ObservableProperty] private double _eqLow;
    [ObservableProperty] private double _eqMid;
    [ObservableProperty] private double _eqHigh;
    partial void OnEqLowChanged(double v) => Deck.Fx.EqLow = (float)v;
    partial void OnEqMidChanged(double v) => Deck.Fx.EqMid = (float)v;
    partial void OnEqHighChanged(double v) => Deck.Fx.EqHigh = (float)v;
    [RelayCommand] private void EqReset() { EqLow = 0; EqMid = 0; EqHigh = 0; }
    [RelayCommand] private void KillLow() => EqLow = EqLow <= -29 ? 0 : -30;
    [RelayCommand] private void KillMid() => EqMid = EqMid <= -29 ? 0 : -30;
    [RelayCommand] private void KillHigh() => EqHigh = EqHigh <= -29 ? 0 : -30;

    // altri effetti
    [ObservableProperty] private bool _phaserOn;
    [ObservableProperty] private bool _crushOn;
    [ObservableProperty] private double _crushAmount = 0.5;
    [ObservableProperty] private bool _gateOn;
    [ObservableProperty] private double _gateBeats = 0.25;
    partial void OnPhaserOnChanged(bool v) => Deck.Fx.PhaserOn = v;
    partial void OnCrushOnChanged(bool v) => Deck.Fx.CrushOn = v;
    partial void OnCrushAmountChanged(double v) => Deck.Fx.CrushAmount = (float)v;
    partial void OnGateOnChanged(bool v) => Deck.Fx.GateOn = v;
    partial void OnGateBeatsChanged(double v) { UpdateEchoTime(); OnPropertyChanged(nameof(GateBeatsLabel)); }
    public string GateBeatsLabel => GateBeats switch { 0.125 => "1/8", 0.25 => "1/4", 0.5 => "1/2", 1 => "1/1", _ => GateBeats.ToString("0.###") };
    [RelayCommand] private void GateBeatsCycle() => GateBeats = GateBeats switch { 0.125 => 0.25, 0.25 => 0.5, 0.5 => 1, _ => 0.125 };
    [RelayCommand] private void Brake() => Deck.Brake(1.5);
    [RelayCommand] private void Backspin() => Deck.Backspin(0.8);

    // ---------------------------------------------------------------- voce AI (Demucs)
    [ObservableProperty] private bool _aiVocalOff;
    [ObservableProperty] private bool _hasInstrumental;
    [ObservableProperty] private bool _isSeparating;
    [ObservableProperty] private string _aiStatus = "";
    /// <summary>Impostato dal MainViewModel: prepara la versione senza voce (Demucs) e ritorna true se pronta.</summary>
    public Func<Track, Task<bool>>? PrepareStems { get; set; }

    partial void OnAiVocalOffChanged(bool value)
    {
        if (Track == null) return;
        try
        {
            if (value && Track.HasInstrumental) Deck.SwapSource(Track.InstrumentalPath!);
            else if (!value) Deck.SwapSource(LibraryService.PrepareForPlayback(Track).audioPath);
        }
        catch (Exception ex) { AiStatus = "Errore: " + ex.Message; }
    }

    /// <summary>VOCE AI: se la base senza voce esiste la attiva/disattiva, altrimenti la genera con Demucs e poi la attiva.</summary>
    [RelayCommand]
    private async Task ToggleAiVocalAsync()
    {
        if (Track == null) return;
        if (Track.HasInstrumental) { AiVocalOff = !AiVocalOff; return; }
        if (IsSeparating || PrepareStems == null) return;
        var track = Track;
        IsSeparating = true;
        try
        {
            bool ok = await PrepareStems(track);
            HasInstrumental = track.HasInstrumental;
            if (ok && Track == track) AiVocalOff = true;
        }
        finally { IsSeparating = false; }
    }

    // ---------------------------------------------------------------- loop
    [ObservableProperty] private bool _loopOn;
    [ObservableProperty] private string _loopLabel = "";
    [ObservableProperty] private double _loopStartFraction;
    [ObservableProperty] private double _loopEndFraction;
    private double _rollReturnSec = -1;

    private double BeatSec()
    {
        double bpm = Track?.Bpm > 0 ? Track.Bpm : 120;
        return 60.0 / bpm; // in secondi di brano (il tempo del deck non cambia la posizione nel brano)
    }

    /// <summary>Loop di N battute a partire dalla posizione corrente (o dall'inizio del loop se già attivo, per cambiarne la lunghezza).</summary>
    [RelayCommand]
    private void LoopBeats(string? beatsStr)
    {
        if (!HasTrack || !double.TryParse(beatsStr, System.Globalization.CultureInfo.InvariantCulture, out var beats)) return;
        double start = Deck.LoopOn ? Deck.LoopStart : Deck.PositionSec;
        double len = BeatSec() * beats;
        Deck.SetLoop(start, start + len);
        LoopOn = true;
        LoopLabel = beats < 1 ? $"LOOP 1/{(int)Math.Round(1 / beats)}" : $"LOOP {beats:0}";
        LoopStartFraction = DurationSec > 0 ? start / DurationSec : 0;
        LoopEndFraction = DurationSec > 0 ? (start + len) / DurationSec : 0;
    }

    /// <summary>Loop roll: loop momentaneo; alla fine il brano riprende da dove sarebbe arrivato.</summary>
    public void LoopRollStart(double beats)
    {
        if (!HasTrack) return;
        if (_rollReturnSec < 0) { _rollReturnSec = Deck.PositionSec; _rollStartedAt = DateTime.UtcNow; }
        double start = Deck.PositionSec;
        Deck.SetLoop(start, start + BeatSec() * beats);
        LoopOn = true; LoopLabel = $"ROLL {(beats < 1 ? "1/" + (int)Math.Round(1 / beats) : beats.ToString("0"))}";
        LoopStartFraction = DurationSec > 0 ? start / DurationSec : 0;
        LoopEndFraction = DurationSec > 0 ? (start + BeatSec() * beats) / DurationSec : 0;
    }
    private DateTime _rollStartedAt;

    public void LoopRollEnd()
    {
        if (_rollReturnSec < 0) return;
        double elapsed = (DateTime.UtcNow - _rollStartedAt).TotalSeconds * (1.0 + TempoPercent / 100.0);
        double target = Math.Min(DurationSec - 0.1, _rollReturnSec + elapsed);
        _rollReturnSec = -1;
        Deck.ClearLoop();
        Deck.Seek(target);
        LoopOn = false; LoopLabel = ""; LoopStartFraction = LoopEndFraction = 0;
    }

    [RelayCommand]
    private void LoopExit()
    {
        _rollReturnSec = -1;
        Deck.ClearLoop();
        LoopOn = false; LoopLabel = ""; LoopStartFraction = LoopEndFraction = 0;
    }

    [RelayCommand]
    private void LoopHalf()
    {
        if (!Deck.LoopOn) return;
        double len = (Deck.LoopEnd - Deck.LoopStart) / 2;
        if (len < BeatSec() / 16) return;
        Deck.SetLoop(Deck.LoopStart, Deck.LoopStart + len);
        LoopEndFraction = DurationSec > 0 ? (Deck.LoopStart + len) / DurationSec : 0;
        LoopLabel = LoopLabelFor(len);
    }

    [RelayCommand]
    private void LoopDouble()
    {
        if (!Deck.LoopOn) return;
        double len = (Deck.LoopEnd - Deck.LoopStart) * 2;
        Deck.SetLoop(Deck.LoopStart, Deck.LoopStart + len);
        LoopEndFraction = DurationSec > 0 ? (Deck.LoopStart + len) / DurationSec : 0;
        LoopLabel = LoopLabelFor(len);
    }

    private string LoopLabelFor(double lenSec)
    {
        double beats = lenSec / BeatSec();
        return beats < 0.99 ? $"LOOP 1/{(int)Math.Round(1 / beats)}" : $"LOOP {beats:0.#}";
    }

    public string EchoBeatsLabel => EchoBeats switch { 0.25 => "1/4", 0.5 => "1/2", 0.75 => "3/4", 1 => "1/1", 2 => "2/1", _ => EchoBeats.ToString("0.##") };
    public string FilterLabel => Math.Abs(FilterValue) < 0.01 ? "—" : FilterValue < 0 ? $"LP {(-FilterValue * 100):0}%" : $"HP {(FilterValue * 100):0}%";

    partial void OnVocalRemoveChanged(bool value) => Deck.Fx.VocalRemove = value;
    partial void OnVocalStrengthChanged(double value) => Deck.Fx.VocalStrength = (float)value;
    partial void OnFilterValueChanged(double value) { Deck.Fx.Filter = (float)value; OnPropertyChanged(nameof(FilterLabel)); }
    partial void OnEchoOnChanged(bool value) => Deck.Fx.EchoOn = value;
    partial void OnEchoBeatsChanged(double value) { UpdateEchoTime(); OnPropertyChanged(nameof(EchoBeatsLabel)); }
    partial void OnEchoFeedbackChanged(double value) => Deck.Fx.EchoFeedback = (float)value;
    partial void OnEchoMixChanged(double value) => Deck.Fx.EchoMix = (float)value;
    partial void OnReverbOnChanged(bool value) => Deck.Fx.ReverbOn = value;
    partial void OnReverbSizeChanged(double value) => Deck.Fx.ReverbSize = (float)value;
    partial void OnReverbMixChanged(double value) => Deck.Fx.ReverbMix = (float)value;
    partial void OnFlangerOnChanged(bool value) => Deck.Fx.FlangerOn = value;

    /// <summary>Tempo dell'eco agganciato ai BPM del brano (con il tempo corrente); 120 BPM se sconosciuti.</summary>
    private void UpdateEchoTime()
    {
        double bpm = Track?.Bpm > 0 ? Track.Bpm : 120;
        bpm *= 1.0 + TempoPercent / 100.0;
        Deck.Fx.EchoTimeSec = (float)Math.Clamp(60.0 / bpm * EchoBeats, 0.02, 1.9);
        Deck.Fx.GateTimeSec = (float)Math.Clamp(60.0 / bpm * GateBeats, 0.02, 2.0);
    }

    [RelayCommand] private void ToggleFx() => FxVisible = !FxVisible;
    [RelayCommand] private void FilterReset() => FilterValue = 0;
    [RelayCommand] private void EchoBeatsCycle() => EchoBeats = EchoBeats switch { 0.25 => 0.5, 0.5 => 0.75, 0.75 => 1, 1 => 2, _ => 0.25 };

    [RelayCommand]
    private void FxReset()
    {
        VocalRemove = false; FilterValue = 0; FilterOn = true; EchoOn = false; ReverbOn = false; FlangerOn = false;
        PhaserOn = false; CrushOn = false; GateOn = false; Deck.CancelSpin();
        Deck.Fx.Dry = 1f;
        EchoOutRunning = false;
    }

    /// <summary>ECHO OUT: taglia il brano lasciando ripetere la coda dell'eco, poi mette in pausa il deck.</summary>
    [RelayCommand]
    private async Task EchoOutAsync()
    {
        if (!HasTrack || EchoOutRunning) return;
        if (!IsPlaying) { Deck.Play(); }
        EchoOutRunning = true;
        var fx = Deck.Fx;
        bool prevEcho = EchoOn; double prevFb = EchoFeedback, prevMix = EchoMix;
        UpdateEchoTime();
        EchoFeedback = 0.72; EchoMix = 0.9; EchoOn = true;
        // ~80 ms di segnale diretto per "caricare" l'eco, poi si toglie il dry
        await Task.Delay(80);
        fx.Dry = 0f;
        // la coda dura alcune ripetizioni
        double tail = Math.Clamp(fx.EchoTimeSec * 7, 1.5, 6);
        await Task.Delay(TimeSpan.FromSeconds(tail));
        if (EchoOutRunning)
        {
            Deck.Pause();
            fx.Dry = 1f;
            EchoOn = prevEcho; EchoFeedback = prevFb; EchoMix = prevMix;
            fx.Reset();
        }
        EchoOutRunning = false;
    }

    /// <summary>Annulla un echo-out in corso (es. se il DJ ci ripensa).</summary>
    [RelayCommand]
    private void CancelEchoOut()
    {
        if (!EchoOutRunning) return;
        EchoOutRunning = false;
        Deck.Fx.Dry = 1f;
    }

    public string KeyLabel => KeyShift == 0 ? "0" : (KeyShift > 0 ? $"+{KeyShift}" : KeyShift.ToString());
    public string TempoLabel => TempoPercent == 0 ? "0%" : (TempoPercent > 0 ? $"+{TempoPercent}%" : $"{TempoPercent}%");
    public int CdgOffsetMs { get; set; }

    partial void OnKeyShiftChanged(int value)
    {
        Deck.KeyShift = value;
        OnPropertyChanged(nameof(KeyLabel));
        RefreshAnalysisLabels();
    }

    partial void OnTempoPercentChanged(int value)
    {
        Deck.Tempo = 1.0 + value / 100.0;
        OnPropertyChanged(nameof(TempoLabel));
        RefreshAnalysisLabels();
        UpdateEchoTime();
    }

    partial void OnKeyLockChanged(bool value)
    {
        Deck.KeyLock = value;
        RefreshAnalysisLabels();
    }

    /// <summary>BPM e tonalità effettivi, tenendo conto di tempo e trasposizione.</summary>
    public void RefreshAnalysisLabels()
    {
        var t = Track;
        if (t == null) { BpmLabel = ""; KeyDisplay = ""; Waveform = null; IntroOutroLabel = ""; return; }
        Waveform = WaveformStore.Load(t.Id);
        double dur = Math.Max(1, DurationSec > 0 ? DurationSec : t.DurationSec);
        IntroFraction = t.IntroEndSec > 0 ? Math.Clamp(t.IntroEndSec / dur, 0, 1) : 0;
        OutroFraction = t.OutroStartSec > 0 ? Math.Clamp(t.OutroStartSec / dur, 0, 1) : 1;
        IntroOutroLabel = t.Analyzed || t.CuesManual
            ? (t.IntroEndSec > 0 ? "Intro " + TimeSpan.FromSeconds(t.IntroEndSec).ToString(@"m\:ss") : "Nessuna intro")
              + (t.OutroStartSec > 0 && t.OutroStartSec < dur - 0.5 ? " · Uscita da " + TimeSpan.FromSeconds(t.OutroStartSec).ToString(@"m\:ss") : "")
            : "";
        UpdateEchoTime();
        double factor = 1.0 + TempoPercent / 100.0;
        BpmLabel = t.Bpm > 0 ? (t.Bpm * factor).ToString("0.0") + " BPM" : "";
        if (string.IsNullOrEmpty(t.Key)) { KeyDisplay = ""; return; }
        int semis = KeyShift + (KeyLock ? 0 : (int)Math.Round(12 * Math.Log2(factor)));
        var k = AudioAnalyzer.Transpose(t.Key, semis);
        var cam = AudioAnalyzer.CamelotOfKey(k);
        KeyDisplay = k + (cam.Length > 0 ? $" · {cam}" : "") + (semis != 0 ? $"  (orig. {t.Key})" : "");
    }

    partial void OnVolumeChanged(double value) => Deck.Volume = (float)value;

    public void Load(Track track, string singer = "", int keyShift = 0)
    {
        LoadError = null;
        try
        {
            var (audioPath, cdgPath) = LibraryService.PrepareForPlayback(track);
            Deck.Load(track, audioPath);
            _cdg = null;
            if (cdgPath != null && File.Exists(cdgPath))
            {
                _cdg = new CdgDecoder(File.ReadAllBytes(cdgPath));
                _cdg.RenderAt(0);
                PushCdgFrame();
            }
            Track = track;
            Title = track.Title;
            Artist = track.Artist;
            Singer = singer;
            KindLabel = track.KindLabel;
            HasTrack = true;
            IsKaraoke = track.IsKaraoke;
            IsCdg = _cdg != null;
            IsVideo = track.IsVideo;
            VideoPath = track.IsVideo ? track.FilePath : null;
            DurationSec = Deck.DurationSec;
            KeyShift = keyShift;
            IsEnding = false;
            RefreshAnalysisLabels();
            UpdateEchoTime();
            EchoOutRunning = false;
            Deck.Fx.Dry = 1f;
            _playedMarked = false;
            _aiVocalOff = false; OnPropertyChanged(nameof(AiVocalOff));
            HasInstrumental = track.HasInstrumental;
            LoopExit();
            Tick();
            TrackLoaded?.Invoke(this);
        }
        catch (Exception ex)
        {
            LoadError = "Impossibile aprire: " + ex.Message;
            Eject();
            throw;
        }
    }

    [RelayCommand]
    public void Eject()
    {
        Deck.Eject();
        _cdg = null;
        Track = null;
        Title = "— vuoto —";
        Artist = "";
        Singer = "";
        KindLabel = "";
        HasTrack = false;
        IsKaraoke = IsCdg = IsVideo = false;
        VideoPath = null;
        DurationSec = 0;
        IsEnding = false;
        BpmLabel = "";
        KeyDisplay = "";
        Tick();
        TrackLoaded?.Invoke(this);
    }

    [RelayCommand] public void TogglePlay() => Deck.TogglePlay();
    [RelayCommand] public void Play() => Deck.Play();
    [RelayCommand] public void Pause() => Deck.Pause();
    [RelayCommand] public void Stop() => Deck.Stop();
    [RelayCommand] public void KeyUp() => KeyShift = Math.Min(12, KeyShift + 1);
    [RelayCommand] public void KeyDown() => KeyShift = Math.Max(-12, KeyShift - 1);
    [RelayCommand] public void KeyReset() => KeyShift = 0;
    [RelayCommand] public void TempoReset() => TempoPercent = 0;
    [RelayCommand] public void Back10() => Deck.Seek(Deck.PositionSec - 10);
    [RelayCommand] public void Forward10() => Deck.Seek(Deck.PositionSec + 10);

    public void SeekFraction(double f)
    {
        if (!HasTrack) return;
        Deck.Seek(Math.Clamp(f, 0, 1) * DurationSec);
        Tick();
    }

    /// <summary>Imposta la fine dell'intro alla frazione indicata della forma d'onda (o alla posizione attuale).</summary>
    public void SetIntroAt(double? fraction)
    {
        if (Track == null || DurationSec <= 0) return;
        double sec = fraction is double f ? f * DurationSec : Deck.PositionSec;
        Track.IntroEndSec = Math.Round(Math.Clamp(sec, 0, Math.Max(0, (Track.OutroStartSec > 0 ? Track.OutroStartSec : DurationSec) - 1)), 1);
        Track.CuesManual = true;
        RefreshAnalysisLabels();
        CuesChanged?.Invoke(this);
    }

    public void SetOutroAt(double? fraction)
    {
        if (Track == null || DurationSec <= 0) return;
        double sec = fraction is double f ? f * DurationSec : Deck.PositionSec;
        Track.OutroStartSec = Math.Round(Math.Clamp(sec, Track.IntroEndSec + 1, DurationSec), 1);
        Track.CuesManual = true;
        RefreshAnalysisLabels();
        CuesChanged?.Invoke(this);
    }

    public void ClearCues()
    {
        if (Track == null) return;
        Track.IntroEndSec = 0; Track.OutroStartSec = 0; Track.CuesManual = false;
        RefreshAnalysisLabels();
        CuesChanged?.Invoke(this);
    }

    /// <summary>Intro/uscita modificate a mano: il MainViewModel salva la libreria.</summary>
    public event Action<DeckViewModel>? CuesChanged;

    /// <summary>Aggiornamento periodico dal timer UI.</summary>
    public void Tick()
    {
        IsPlaying = Deck.IsPlaying;
        PositionSec = Deck.PositionSec;
        // VU: sale subito, scende con decadimento
        double tl = Views.LevelMeter.ToScale(Deck.PeakL), tr = Views.LevelMeter.ToScale(Deck.PeakR);
        LevelL = tl > LevelL ? tl : Math.Max(0, LevelL - 0.06);
        LevelR = tr > LevelR ? tr : Math.Max(0, LevelR - 0.06);
        if (!_playedMarked && IsPlaying && PositionSec > 10 && Track != null) { _playedMarked = true; Played?.Invoke(this, Track); }
        var dur = Deck.DurationSec;
        if (Math.Abs(dur - DurationSec) > 0.01) DurationSec = dur;
        Progress = dur > 0 ? PositionSec / dur : 0;
        PositionLabel = TimeSpan.FromSeconds(PositionSec).ToString(@"m\:ss");
        RemainingLabel = "-" + TimeSpan.FromSeconds(Math.Max(0, dur - PositionSec)).ToString(@"m\:ss");
        double outroStart = Track?.OutroStartSec > 0 && Track.OutroStartSec < dur - 0.5 ? Track.OutroStartSec : dur - 20;
        InOutro = HasTrack && dur > 0 && PositionSec >= outroStart;
        IsEnding = HasTrack && dur > 0 && IsPlaying && (dur - PositionSec < 20 || InOutro);

        if (_cdg != null && (IsPlaying || _cdg.FrameVersion == 0))
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCdgRender).TotalMilliseconds >= 33)
            {
                _lastCdgRender = now;
                if (_cdg.RenderAt(PositionSec + CdgOffsetMs / 1000.0))
                    PushCdgFrame();
            }
        }
    }

    public void ForceCdgRefresh()
    {
        if (_cdg == null) return;
        _cdg.RenderAt(PositionSec + CdgOffsetMs / 1000.0);
        PushCdgFrame();
    }

    private void PushCdgFrame()
    {
        if (_cdg == null) return;
        CdgBitmap.WritePixels(new Int32Rect(0, 0, CdgDecoder.Width, CdgDecoder.Height), _cdg.Frame, CdgDecoder.Width * 4, 0);
    }
}
