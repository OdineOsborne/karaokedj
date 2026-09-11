using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Audio;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

public sealed partial class StepViewModel : ObservableObject
{
    private readonly RhythmTrack _track;
    private readonly int _index;
    public StepViewModel(RhythmTrack track, int index) { _track = track; _index = index; _on = track.Pattern[index]; }
    [ObservableProperty] private bool _on;
    [ObservableProperty] private bool _current;
    /// <summary>Primo step di ogni quarto: evidenziato in griglia.</summary>
    public bool IsBeat => _index % 4 == 0;
    partial void OnOnChanged(bool value) => _track.Pattern[_index] = value;
}

public sealed partial class RhythmTrackViewModel : ObservableObject
{
    public RhythmTrack Track { get; }
    private readonly RhythmViewModel _owner;
    public RhythmTrackViewModel(RhythmTrack t, RhythmViewModel owner)
    {
        Track = t; _owner = owner;
        _name = t.Name; _sound = t.Sound; _volume = t.Volume; _muted = t.Muted;
        for (int i = 0; i < RhythmTrack.Steps; i++) Steps.Add(new StepViewModel(t, i));
    }
    public ObservableCollection<StepViewModel> Steps { get; } = new();
    [ObservableProperty] private string _name;
    [ObservableProperty] private DrumSound _sound;
    [ObservableProperty] private float _volume;
    [ObservableProperty] private bool _muted;
    public string SampleLabel => Track.Sound == DrumSound.Sample
        ? (string.IsNullOrEmpty(Track.SamplePath) ? "(nessun campione)" : Path.GetFileName(Track.SamplePath)) : "";
    public static IReadOnlyList<DrumSound> Sounds { get; } = Enum.GetValues<DrumSound>();

    partial void OnNameChanged(string value) => Track.Name = value;
    partial void OnVolumeChanged(float value) => Track.Volume = value;
    partial void OnMutedChanged(bool value) => Track.Muted = value;
    partial void OnSoundChanged(DrumSound value)
    {
        Track.Sound = value;
        if (value == DrumSound.Sample && string.IsNullOrEmpty(Track.SamplePath)) PickSample();
        else _owner.Engine.Render(Track);
        OnPropertyChanged(nameof(SampleLabel));
    }

    [RelayCommand] private void Preview() => _owner.Engine.Trigger(Track);
    [RelayCommand] private void Clear() { foreach (var s in Steps) s.On = false; }
    [RelayCommand] private void Remove() => _owner.RemoveTrack(this);

    [RelayCommand]
    private void PickSample()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Campione per la traccia",
            Filter = "Audio|*.wav;*.mp3;*.flac;*.ogg;*.m4a;*.aac;*.wma|Tutti i file|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        Track.SamplePath = dlg.FileName;
        if (Sound != DrumSound.Sample) Sound = DrumSound.Sample; else _owner.Engine.Render(Track);
        if (Name.StartsWith("Traccia") || Name.Length == 0) Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        OnPropertyChanged(nameof(SampleLabel));
    }

    internal void RefreshCurrent(int step, bool running)
    {
        for (int i = 0; i < Steps.Count; i++) Steps[i].Current = running && i == step;
    }
}

/// <summary>Sequencer ritmico: tracce, pattern, preset, tap tempo, aggancio ai BPM della serata.</summary>
public sealed partial class RhythmViewModel : ObservableObject
{
    private sealed class SavedTrack
    {
        public string Name { get; set; } = "";
        public DrumSound Sound { get; set; }
        public string? SamplePath { get; set; }
        public bool[] Pattern { get; set; } = new bool[RhythmTrack.Steps];
        public float Volume { get; set; } = 0.8f;
        public bool Muted { get; set; }
    }
    private sealed class SavedState
    {
        public List<SavedTrack> Tracks { get; set; } = new();
        public float Volume { get; set; } = 0.8f;
        public float Swing { get; set; }
        public bool FollowDecks { get; set; } = true;
        public double ManualBpm { get; set; } = 120;
    }

    public RhythmEngine Engine { get; }
    private readonly Func<double> _setBpm;     // BPM correnti della serata (deck che suona / blocco BPM), 0 se ignoti
    private readonly DispatcherTimer _ui;
    private readonly List<DateTime> _taps = new();

    public ObservableCollection<RhythmTrackViewModel> Tracks { get; } = new();
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private float _volume = 0.8f;
    [ObservableProperty] private float _swing;
    /// <summary>true: segue i BPM del deck in riproduzione (o il blocco BPM); false: BPM manuale/tap.</summary>
    [ObservableProperty] private bool _followDecks = true;
    [ObservableProperty] private double _manualBpm = 120;
    [ObservableProperty] private string _bpmLabel = "120";
    [ObservableProperty] private string _tapLabel = "TAP";

    public RhythmViewModel(RhythmEngine engine, Func<double> setBpm)
    {
        Engine = engine; _setBpm = setBpm;
        Load();
        if (Tracks.Count == 0) ApplyPreset("house");
        _ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _ui.Tick += (_, _) => Tick();
        _ui.Start();
    }

    partial void OnVolumeChanged(float value) => Engine.Volume = value;
    partial void OnSwingChanged(float value) => Engine.Swing = value;
    partial void OnManualBpmChanged(double value) { if (!FollowDecks) Engine.Bpm = value; }
    partial void OnFollowDecksChanged(bool value) { if (!value) Engine.Bpm = ManualBpm; }

    private void Tick()
    {
        if (FollowDecks)
        {
            double b = _setBpm();
            if (b > 0) Engine.Bpm = b;
        }
        BpmLabel = Engine.Bpm.ToString("0.0") + (FollowDecks ? " (segue i deck)" : "");
        int step = Engine.CurrentStep; bool run = Engine.IsRunning;
        foreach (var t in Tracks) t.RefreshCurrent(step, run);
        IsRunning = run;
        if (_taps.Count > 0 && (DateTime.UtcNow - _taps[^1]).TotalSeconds > 2) { _taps.Clear(); TapLabel = "TAP"; }
    }

    [RelayCommand] private void TogglePlay() { if (Engine.IsRunning) Engine.Stop(); else Engine.Start(); }
    [RelayCommand] private void Resync() { if (!Engine.IsRunning) Engine.Start(); else Engine.Resync(); }

    /// <summary>Tap tempo: media degli ultimi intervalli (fino a 8 tap).</summary>
    [RelayCommand]
    public void Tap()
    {
        var now = DateTime.UtcNow;
        if (_taps.Count > 0 && (now - _taps[^1]).TotalSeconds > 2) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 8) _taps.RemoveAt(0);
        if (_taps.Count < 2) { TapLabel = "TAP ●"; return; }
        double avg = (_taps[^1] - _taps[0]).TotalSeconds / (_taps.Count - 1);
        double bpm = Math.Round(60.0 / avg, 1);
        FollowDecks = false;
        ManualBpm = bpm;
        TapLabel = $"TAP {bpm:0.0}";
    }

    [RelayCommand] private void AddTrack()
    {
        var t = Engine.AddTrack($"Traccia {Tracks.Count + 1}", DrumSound.Kick);
        Tracks.Add(new RhythmTrackViewModel(t, this));
    }

    internal void RemoveTrack(RhythmTrackViewModel vm) { Engine.RemoveTrack(vm.Track); Tracks.Remove(vm); }

    [RelayCommand] private void ClearAll() { foreach (var t in Tracks) foreach (var s in t.Steps) s.On = false; }

    // ---------------------------------------------------------------- preset

    [RelayCommand]
    public void ApplyPreset(string name)
    {
        var defs = Presets.TryGetValue(name, out var d) ? d : Presets["house"];
        foreach (var t in Tracks.ToList()) RemoveTrack(t);
        foreach (var (label, sound, pat, vol) in defs)
        {
            var t = Engine.AddTrack(label, sound);
            t.Volume = vol;
            for (int i = 0; i < RhythmTrack.Steps && i < pat.Length; i++) t.Pattern[i] = pat[i] == 'x' || pat[i] == 'X';
            Tracks.Add(new RhythmTrackViewModel(t, this));
        }
    }

    public static IReadOnlyList<(string key, string label)> PresetList { get; } = new[]
    {
        ("house", "House"), ("disco", "Disco"), ("hiphop", "Hip-hop"), ("rock", "Rock"),
        ("reggaeton", "Reggaeton"), ("latin", "Latin / Clave"), ("techno", "Techno"), ("empty", "Vuoto"),
    };

    // pattern: 16 caratteri, 'x' = colpo. Ordine: |1e&a|2e&a|3e&a|4e&a|
    private static readonly Dictionary<string, (string, DrumSound, string, float)[]> Presets = new()
    {
        ["house"] = new[]
        {
            ("Kick", DrumSound.Kick, "x...x...x...x...", 0.9f),
            ("Clap", DrumSound.Clap, "....x.......x...", 0.7f),
            ("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.x.", 0.45f),
            ("Open hat", DrumSound.HatOpen, "..x...x...x...x.", 0.4f),
        },
        ["disco"] = new[]
        {
            ("Kick", DrumSound.Kick, "x...x...x...x...", 0.9f),
            ("Snare", DrumSound.Snare, "....x.......x...", 0.7f),
            ("Open hat", DrumSound.HatOpen, "..x...x...x...x.", 0.45f),
            ("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.x.", 0.35f),
        },
        ["hiphop"] = new[]
        {
            ("Kick", DrumSound.Kick, "x.....x.x.....x.", 0.9f),
            ("Snare", DrumSound.Snare, "....x.......x...", 0.75f),
            ("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.xx", 0.4f),
        },
        ["rock"] = new[]
        {
            ("Kick", DrumSound.Kick, "x.....x.x.......", 0.9f),
            ("Snare", DrumSound.Snare, "....x.......x...", 0.8f),
            ("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.x.", 0.45f),
        },
        ["reggaeton"] = new[]
        {
            ("Kick", DrumSound.Kick, "x...x...x...x...", 0.9f),
            ("Snare", DrumSound.Snare, "...x..x....x..x.", 0.75f),
            ("Hat", DrumSound.HatClosed, "x.x.x.x.x.x.x.x.", 0.35f),
        },
        ["latin"] = new[]
        {
            ("Kick", DrumSound.Kick, "x.......x.......", 0.8f),
            ("Clave", DrumSound.Rim, "x..x..x...x.x...", 0.7f),
            ("Cowbell", DrumSound.Cowbell, "x...x...x...x...", 0.4f),
            ("Shaker", DrumSound.Shaker, "x.x.x.x.x.x.x.x.", 0.4f),
            ("Tom", DrumSound.TomLow, "......x.......x.", 0.5f),
        },
        ["techno"] = new[]
        {
            ("Kick", DrumSound.Kick, "x...x...x...x...", 1.0f),
            ("Clap", DrumSound.Clap, "....x.......x...", 0.5f),
            ("Hat", DrumSound.HatClosed, "..x...x...x...x.", 0.5f),
            ("Rim", DrumSound.Rim, "...x.....x..x...", 0.35f),
        },
        ["empty"] = new[]
        {
            ("Kick", DrumSound.Kick, "................", 0.9f),
            ("Snare", DrumSound.Snare, "................", 0.75f),
            ("Hat", DrumSound.HatClosed, "................", 0.45f),
        },
    };

    // ---------------------------------------------------------------- persistenza

    public void Save()
    {
        var st = new SavedState
        {
            Volume = Volume, Swing = Swing, FollowDecks = FollowDecks, ManualBpm = ManualBpm,
            Tracks = Tracks.Select(t => new SavedTrack
            {
                Name = t.Track.Name, Sound = t.Track.Sound, SamplePath = t.Track.SamplePath,
                Pattern = (bool[])t.Track.Pattern.Clone(), Volume = t.Track.Volume, Muted = t.Track.Muted,
            }).ToList(),
        };
        try { JsonStore.Save(AppPaths.RhythmFile, st); } catch { }
    }

    private void Load()
    {
        var st = JsonStore.Load<SavedState>(AppPaths.RhythmFile);
        Volume = st.Volume; Swing = st.Swing; ManualBpm = st.ManualBpm; FollowDecks = st.FollowDecks;
        foreach (var s in st.Tracks)
        {
            var t = Engine.AddTrack(s.Name, s.Sound, s.SamplePath);
            t.Volume = s.Volume; t.Muted = s.Muted;
            if (s.Pattern.Length == RhythmTrack.Steps) t.Pattern = s.Pattern;
            Tracks.Add(new RhythmTrackViewModel(t, this));
        }
    }
}
