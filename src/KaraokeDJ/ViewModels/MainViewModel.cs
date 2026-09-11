using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    public const int PadCount = 12;

    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _downloadCts;
    private double? _crossfadeTarget;
    private double _crossfadeSpeed;
    private DeckViewModel? _autoMixTriggeredFor;
    private DateTime _lastTick = DateTime.UtcNow;

    public MainViewModel()
    {
        AppPaths.EnsureDirs();
        Settings = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        Engine = new AudioEngine();
        Library = new LibraryService();
        Downloader = new DownloadService();
        Midi = new MidiService();
        Midi.ActionTriggered += HandleMidiAction;

        DeckA = new DeckViewModel(Engine.DeckA);
        DeckB = new DeckViewModel(Engine.DeckB);
        foreach (var d in new[] { DeckA, DeckB })
        {
            d.TrackEnded += OnDeckEnded;
            d.TrackLoaded += dv => { if (_autoMixTriggeredFor == dv) _autoMixTriggeredFor = null; UpdateProjectorState(); UpdateSuggestions(); };
            d.Played += OnTrackPlayed;
            d.CdgOffsetMs = Settings.CdgOffsetMs;
        }

        LibraryView = CollectionViewSource.GetDefaultView(Tracks);
        LibraryView.Filter = FilterTrack;
        LibraryView.SortDescriptions.Add(new SortDescription(nameof(Track.Artist), ListSortDirection.Ascending));
        LibraryView.SortDescriptions.Add(new SortDescription(nameof(Track.Title), ListSortDirection.Ascending));

        for (int i = 0; i < PadCount; i++)
        {
            var dto = Settings.Pads.FirstOrDefault(p => p.Index == i);
            Pads.Add(new PadItem { Index = i, Name = dto?.Name ?? "", FilePath = dto?.FilePath });
        }
        Engine.Pads.PadFinished += idx => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (idx >= 0 && idx < Pads.Count) Pads[idx].IsPlaying = Engine.Pads.IsPlaying(idx);
        });

        MasterVolume = Settings.MasterVolume;
        AutoMix = Settings.AutoMix;
        AutoMixUseCues = Settings.AutoMixUseCues;
        BpmLock = Settings.BpmLock;
        BpmLockValue = Settings.BpmLockValue;
        BpmMatch = Settings.BpmMatch;
        CrossfadeSeconds = Settings.CrossfadeSeconds;
        IdleTitle = Settings.IdleTitle;
        IdleSubtitle = Settings.IdleSubtitle;

        Queue.CollectionChanged += (_, _) => { UpdateProjectorState(); SaveQueue(); UpdateSuggestions(); };

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
    }

    public AppSettings Settings { get; }
    public AudioEngine Engine { get; }
    public LibraryService Library { get; }
    public DownloadService Downloader { get; }
    public MidiService Midi { get; }
    public DeckViewModel DeckA { get; }
    public DeckViewModel DeckB { get; }

    public ObservableCollection<Track> Tracks { get; } = new();
    public ICollectionView LibraryView { get; }
    public ObservableCollection<QueueEntry> Queue { get; } = new();
    public ObservableCollection<PadItem> Pads { get; } = new();
    public ObservableCollection<string> NextSingers { get; } = new();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _libraryFilter = "all"; // all | audio | cdg | video
    [ObservableProperty] private Track? _selectedTrack;
    [ObservableProperty] private QueueEntry? _selectedQueueEntry;
    [ObservableProperty] private string _singerName = "";
    [ObservableProperty] private int _queueKeyShift;
    [ObservableProperty] private double _crossfader;
    [ObservableProperty] private double _masterVolume = 1.0;
    [ObservableProperty] private bool _autoMix;
    [ObservableProperty] private bool _autoMixUseCues = true;
    [ObservableProperty] private double _crossfadeSeconds = 6;
    [ObservableProperty] private string _statusText = "Pronto";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private bool _isProjectorOpen;
    [ObservableProperty] private DeckViewModel? _activeKaraokeDeck;
    [ObservableProperty] private string _nowSinging = "";
    [ObservableProperty] private string _idleTitle = "";
    [ObservableProperty] private string _idleSubtitle = "";
    [ObservableProperty] private int _libraryCount;
    [ObservableProperty] private string _downloadInput = "";
    [ObservableProperty] private bool _downloadVideo;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private bool _isEditingPads;

    public string QueueKeyLabel => QueueKeyShift == 0 ? "0" : (QueueKeyShift > 0 ? $"+{QueueKeyShift}" : QueueKeyShift.ToString());
    partial void OnQueueKeyShiftChanged(int value) => OnPropertyChanged(nameof(QueueKeyLabel));

    // ---------------------------------------------------------------- avvio / chiusura

    public void Start()
    {
        try { Engine.Start(Settings.OutputDeviceId); }
        catch (Exception ex) { StatusText = "Errore audio: " + ex.Message; }

        Library.Load();
        RefreshTracks();
        LoadQueue();
        LoadPlaylists();
        Downloader.TrackExists = (artist, title) =>
        {
            var a = DownloadService.NormalizeForCompare(artist);
            var t = DownloadService.NormalizeForCompare(title);
            if (t.Length < 3) return false;
            return Library.Tracks.Any(x =>
            {
                var xt = DownloadService.NormalizeForCompare(x.Title);
                var xa = DownloadService.NormalizeForCompare(x.Artist);
                bool titleOk = xt.Contains(t) || (xt.Length >= 3 && t.Contains(xt));
                bool artistOk = a.Length == 0 || xa.Contains(a) || a.Contains(xa) || xt.Contains(a);
                return titleOk && artistOk;
            });
        };
        WireStems();
        Midi.LoadMappings(Settings.MidiMappings);
        if (!string.IsNullOrEmpty(Settings.MidiDeviceName) && !Midi.Open(Settings.MidiDeviceName))
            StatusText = "Controller MIDI non trovato: " + Settings.MidiDeviceName;
        _timer.Start();
        StatusText = $"Uscita: {Engine.OutputDescription} · {Tracks.Count} brani";
        if (Settings.LibraryFolders.Count > 0)
            _ = RescanAsync();
        _ = CheckForUpdatesAsync(silent: true);
    }

    public void Shutdown()
    {
        _timer.Stop();
        _scanCts?.Cancel();
        _downloadCts?.Cancel();
        SaveSettings();
        SaveQueue();
        Midi.Dispose();
        Engine.Dispose();
    }

    public void SaveSettings()
    {
        Settings.MasterVolume = MasterVolume;
        Settings.AutoMix = AutoMix;
        Settings.AutoMixUseCues = AutoMixUseCues;
        Settings.BpmLock = BpmLock;
        Settings.BpmLockValue = BpmLockValue;
        Settings.BpmMatch = BpmMatch;
        Settings.CrossfadeSeconds = CrossfadeSeconds;
        Settings.IdleTitle = IdleTitle;
        Settings.IdleSubtitle = IdleSubtitle;
        Settings.MidiMappings = Midi.ExportMappings();
        Settings.Pads = Pads.Select(p => new PadDto { Index = p.Index, Name = p.Name, FilePath = p.FilePath }).ToList();
        JsonStore.Save(AppPaths.SettingsFile, Settings);
    }

    public void ApplyAudioDevice(string? deviceId)
    {
        Settings.OutputDeviceId = deviceId;
        try { Engine.Start(deviceId); StatusText = "Uscita: " + Engine.OutputDescription; }
        catch (Exception ex) { StatusText = "Errore audio: " + ex.Message; }
    }

    public void ApplyCdgOffset(int ms)
    {
        Settings.CdgOffsetMs = ms;
        DeckA.CdgOffsetMs = ms;
        DeckB.CdgOffsetMs = ms;
    }

    // ---------------------------------------------------------------- timer

    private void Tick()
    {
        var now = DateTime.UtcNow;
        double dt = (now - _lastTick).TotalSeconds;
        _lastTick = now;

        DeckA.Tick();
        DeckB.Tick();

        if (_crossfadeTarget is double target)
        {
            double v = Crossfader;
            double step = _crossfadeSpeed * dt;
            if (Math.Abs(target - v) <= step)
            {
                Crossfader = target;
                _crossfadeTarget = null;
                // a fine dissolvenza fermiamo il deck che è stato sfumato
                var faded = target > 0 ? DeckA : DeckB;
                if (faded.IsPlaying) faded.Deck.Pause();
                if (faded.HasTrack) faded.Deck.Seek(0);
                if (_autoMixTriggeredFor == faded) _autoMixTriggeredFor = null;
            }
            else
            {
                Crossfader = v + Math.Sign(target - v) * step;
            }
        }

        if (AutoMix) CheckAutoMix();
        UpdateActiveKaraokeDeck();
    }

    partial void OnCrossfaderChanged(double value) => Engine.Crossfader = value;
    partial void OnMasterVolumeChanged(double value) => Engine.MasterVolume = (float)value;

    // ---------------------------------------------------------------- libreria

    private bool FilterTrack(object o)
    {
        if (o is not Track t) return false;
        // Playlist selezionata: mostra i suoi brani; se si sta cercando, cerca in tutta la libreria (per aggiungere)
        if (SelectedPlaylist != null && string.IsNullOrWhiteSpace(SearchText) && !SelectedPlaylist.TrackIds.Contains(t.Id)) return false;
        bool kindOk = LibraryFilter switch
        {
            "audio" => t.Kind == TrackKind.Audio,
            "cdg" => t.IsCdg,
            "video" => t.IsVideo,
            "compat" => _compatScores.TryGetValue(t.Id, out var sc) && sc >= 0.35,
            _ => true,
        };
        if (!kindOk) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        return SearchUtil.Matches(t, _searchWords);
    }

    private string[] _searchWords = Array.Empty<string>();
    private readonly Dictionary<string, double> _compatScores = new();
    [ObservableProperty] private string _compatReferenceLabel = "";

    partial void OnSearchTextChanged(string value)
    {
        _searchWords = SearchUtil.Words(value);
        LibraryView.Refresh();
    }

    partial void OnLibraryFilterChanged(string value)
    {
        if (value == "compat") ComputeCompatibility();
        else foreach (var t in Tracks) t.MatchLabel = "";
        ApplyLibrarySort();
        LibraryView.Refresh();
    }

    /// <summary>Brano di riferimento per i suggerimenti: il deck che si sente di più, altrimenti il brano selezionato.</summary>
    private Track? CompatReference()
    {
        if (DeckA.IsPlaying && DeckB.IsPlaying) return Crossfader <= 0 ? DeckA.Track : DeckB.Track;
        if (DeckA.IsPlaying) return DeckA.Track;
        if (DeckB.IsPlaying) return DeckB.Track;
        return SelectedTrack ?? DeckA.Track ?? DeckB.Track;
    }

    private void ComputeCompatibility()
    {
        _compatScores.Clear();
        var r = CompatReference();
        if (r == null) { CompatReferenceLabel = "Nessun brano di riferimento"; return; }
        if (r.Bpm <= 0 && string.IsNullOrEmpty(r.Key)) AnalyzeInBackground(r);
        CompatReferenceLabel = $"Compatibili con: {r.Display}" + (r.Bpm > 0 ? $" ({r.Bpm:0} BPM, {r.KeyLabel})" : "");
        foreach (var t in Tracks)
        {
            double s = SearchUtil.Compatibility(r, t);
            _compatScores[t.Id] = s;
            t.MatchLabel = s >= 0.35 ? (s * 100).ToString("0") + "%" : "";
        }
        StatusText = CompatReferenceLabel;
    }

    [RelayCommand]
    private void RefreshCompatibility()
    {
        if (LibraryFilter != "compat") LibraryFilter = "compat";
        else { ComputeCompatibility(); ApplyLibrarySort(); LibraryView.Refresh(); }
    }

    [RelayCommand] private void SetLibraryFilter(string? f) => LibraryFilter = f ?? "all";

    private void RefreshTracks()
    {
        Tracks.Clear();
        foreach (var t in Library.Tracks) Tracks.Add(t);
        LibraryCount = Tracks.Count;
    }

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Cartella con musica / basi karaoke", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!Settings.LibraryFolders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            Settings.LibraryFolders.Add(dlg.SelectedPath);
            SaveSettings();
        }
        await RescanAsync();
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        if (IsScanning) return;
        _scanCts = new CancellationTokenSource();
        IsScanning = true;
        var progress = new Progress<ScanProgress>(p =>
        {
            ScanPercent = p.Total == 0 ? 0 : 100.0 * p.Done / p.Total;
            StatusText = $"Scansione {p.Done}/{p.Total}: {p.Current}";
        });
        try
        {
            await Library.ScanAsync(Settings.LibraryFolders, progress, _scanCts.Token);
            RefreshTracks();
            StatusText = $"Libreria: {Tracks.Count} brani";
            // analisi BPM/tonalità/forma d'onda dei brani nuovi, in background
            if (Settings.AutoAnalyze && !IsAnalyzing && Tracks.Any(t => !t.Analyzed)) _ = AnalyzeMissingAsync();
        }
        catch (OperationCanceledException) { StatusText = "Scansione annullata"; }
        catch (Exception ex) { StatusText = "Errore scansione: " + ex.Message; }
        finally { IsScanning = false; }
    }

    public void RemoveFolder(string folder)
    {
        Settings.LibraryFolders.Remove(folder);
        SaveSettings();
    }

    // ---------------------------------------------------------------- coda

    [RelayCommand]
    private void AddToQueue()
    {
        if (SelectedTrack == null) return;
        AddToQueue(SelectedTrack, SingerName, QueueKeyShift);
        SingerName = "";
        QueueKeyShift = 0;
    }

    public void AddToQueue(Track track, string singer, int keyShift)
    {
        Queue.Add(new QueueEntry { Track = track, Singer = singer.Trim(), KeyShift = keyShift });
        StatusText = $"In coda: {track.Display}" + (string.IsNullOrWhiteSpace(singer) ? "" : $" ({singer})");
    }

    [RelayCommand]
    private void RemoveFromQueue(QueueEntry? entry)
    {
        entry ??= SelectedQueueEntry;
        if (entry != null) Queue.Remove(entry);
    }

    [RelayCommand]
    private void MoveQueueUp(QueueEntry? entry)
    {
        entry ??= SelectedQueueEntry;
        if (entry == null) return;
        int i = Queue.IndexOf(entry);
        if (i > 0) Queue.Move(i, i - 1);
    }

    [RelayCommand]
    private void MoveQueueDown(QueueEntry? entry)
    {
        entry ??= SelectedQueueEntry;
        if (entry == null) return;
        int i = Queue.IndexOf(entry);
        if (i >= 0 && i < Queue.Count - 1) Queue.Move(i, i + 1);
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (Queue.Count == 0) return;
        if (MessageBox.Show("Svuotare tutta la coda?", "Coda", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Queue.Clear();
    }

    private void SaveQueue()
    {
        try
        {
            JsonStore.Save(AppPaths.QueueFile, Queue.Select(q => new QueueEntryDto { Singer = q.Singer, KeyShift = q.KeyShift, TrackId = q.Track.Id }).ToList());
        }
        catch { }
    }

    private void LoadQueue()
    {
        var dtos = JsonStore.Load<List<QueueEntryDto>>(AppPaths.QueueFile);
        foreach (var d in dtos)
        {
            var t = Library.FindById(d.TrackId);
            if (t != null) Queue.Add(new QueueEntry { Track = t, Singer = d.Singer, KeyShift = d.KeyShift });
        }
    }

    // ---------------------------------------------------------------- deck

    public bool LoadToDeck(DeckViewModel deck, Track track, string singer = "", int keyShift = 0)
    {
        try
        {
            deck.TempoPercent = 0;
            deck.Load(track, singer, keyShift);
            StatusText = $"Deck {deck.Name}: {track.Display}";
            if (BpmLock && !track.IsKaraoke) ApplyTempoForTarget(deck, BpmLockValue);
            AnalyzeInBackground(track);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Impossibile caricare \"{track.Display}\":\n{ex.Message}", "Errore", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    [RelayCommand] private void LoadSelectedToA() { if (SelectedTrack != null) LoadToDeck(DeckA, SelectedTrack, SingerName, QueueKeyShift); }
    [RelayCommand] private void LoadSelectedToB() { if (SelectedTrack != null) LoadToDeck(DeckB, SelectedTrack, SingerName, QueueKeyShift); }

    [RelayCommand]
    private void QueueEntryToA(QueueEntry? e) => LoadQueueEntry(DeckA, e ?? SelectedQueueEntry);

    [RelayCommand]
    private void QueueEntryToB(QueueEntry? e) => LoadQueueEntry(DeckB, e ?? SelectedQueueEntry);

    private void LoadQueueEntry(DeckViewModel deck, QueueEntry? e)
    {
        if (e == null) return;
        if (LoadToDeck(deck, e.Track, e.Singer, e.KeyShift)) Queue.Remove(e);
    }

    /// <summary>Carica il primo della coda nel deck libero (o quello non in riproduzione).</summary>
    [RelayCommand]
    private void LoadNextToFreeDeck()
    {
        if (Queue.Count == 0) { StatusText = "Coda vuota"; return; }
        var deck = FreeDeck();
        LoadQueueEntry(deck, Queue[0]);
    }

    private DeckViewModel FreeDeck()
    {
        if (!DeckA.HasTrack) return DeckA;
        if (!DeckB.HasTrack) return DeckB;
        if (!DeckA.IsPlaying) return DeckA;
        if (!DeckB.IsPlaying) return DeckB;
        // entrambi in riproduzione: quello meno udibile
        return Crossfader >= 0 ? DeckA : DeckB;
    }

    /// <summary>"Prossimo": carica il primo in coda sull'altro deck, lo avvia e sfuma.</summary>
    [RelayCommand]
    private void PlayNext()
    {
        var playing = DeckA.IsPlaying && (!DeckB.IsPlaying || Crossfader <= 0) ? DeckA
                    : DeckB.IsPlaying ? DeckB : null;
        var target = playing == DeckA ? DeckB : DeckA;

        if (Queue.Count > 0)
        {
            var e = Queue[0];
            if (!LoadToDeck(target, e.Track, e.Singer, e.KeyShift)) return;
            Queue.Remove(e);
        }
        else if (!target.HasTrack)
        {
            StatusText = "Coda vuota e nessun brano sul deck " + target.Name;
            return;
        }

        MatchIncomingTempo(target, playing);
        if (AutoMixUseCues && target.Track?.IsKaraoke == false && target.Track.IntroEndSec > 2 && target.Deck.PositionSec < 1)
            target.Deck.Seek(Math.Max(0, target.Track.IntroEndSec - 1));
        target.Deck.Play();
        if (playing != null) StartCrossfade(target == DeckB ? 1 : -1);
        else Crossfader = target == DeckB ? 1 : -1;
    }

    [RelayCommand] private void CrossfadeToA() => StartCrossfade(-1);
    [RelayCommand] private void CrossfadeToB() => StartCrossfade(1);

    public void StartCrossfade(double target)
    {
        double secs = Math.Max(0.2, CrossfadeSeconds);
        _crossfadeTarget = target;
        _crossfadeSpeed = 2.0 / secs; // percorso da -1 a +1 in "secs" secondi
    }

    private void CheckAutoMix()
    {
        foreach (var (d, other, dir) in new[] { (DeckA, DeckB, 1.0), (DeckB, DeckA, -1.0) })
        {
            if (!d.IsPlaying || !d.HasTrack) continue;
            if (_autoMixTriggeredFor == d) continue;
            if (other.IsPlaying) continue;
            if (Queue.Count == 0) continue;
            double dur = d.Deck.DurationSec;
            double mixAt = dur - CrossfadeSeconds - 0.5;
            if (AutoMixUseCues && d.Track?.OutroStartSec > 0 && d.Track.OutroStartSec < dur - 0.5)
                mixAt = Math.Min(mixAt, d.Track.OutroStartSec);
            if (d.Deck.PositionSec < mixAt) continue;

            var e = Queue[0];
            if (!LoadToDeck(other, e.Track, e.Singer, e.KeyShift)) { _autoMixTriggeredFor = d; continue; }
            Queue.Remove(e);
            MatchIncomingTempo(other, d);
            if (AutoMixUseCues && e.Track.IntroEndSec > 2 && !e.Track.IsKaraoke) other.Deck.Seek(Math.Max(0, e.Track.IntroEndSec - 1));
            other.Deck.Play();
            StartCrossfade(dir);
            _autoMixTriggeredFor = d;
        }
    }

    private void OnDeckEnded(DeckViewModel d)
    {
        if (_autoMixTriggeredFor == d) _autoMixTriggeredFor = null;
        d.Tick();
        // Con automix attivo e coda piena, se per qualche motivo la dissolvenza non è partita
        if (AutoMix && Queue.Count > 0 && !DeckA.IsPlaying && !DeckB.IsPlaying)
        {
            var other = d == DeckA ? DeckB : DeckA;
            var e = Queue[0];
            if (LoadToDeck(other, e.Track, e.Singer, e.KeyShift))
            {
                Queue.Remove(e);
                MatchIncomingTempo(other, d);
                other.Deck.Play();
                Crossfader = other == DeckB ? 1 : -1;
            }
        }
        UpdateProjectorState();
    }

    // ---------------------------------------------------------------- proiettore

    private void UpdateActiveKaraokeDeck()
    {
        DeckViewModel? best = null;
        bool aK = DeckA.HasTrack && DeckA.IsKaraoke;
        bool bK = DeckB.HasTrack && DeckB.IsKaraoke;
        if (aK && bK)
        {
            if (DeckA.IsPlaying != DeckB.IsPlaying) best = DeckA.IsPlaying ? DeckA : DeckB;
            else best = Crossfader <= 0 ? DeckA : DeckB;
        }
        else if (aK) best = DeckA;
        else if (bK) best = DeckB;

        if (best != ActiveKaraokeDeck)
        {
            ActiveKaraokeDeck = best;
            UpdateProjectorState();
        }
    }

    private void UpdateProjectorState()
    {
        var d = ActiveKaraokeDeck;
        NowSinging = d?.HasTrack == true
            ? (string.IsNullOrWhiteSpace(d.Singer) ? d.Track!.Display : $"{d.Singer} — {d.Track!.Display}")
            : "";
        NextSingers.Clear();
        foreach (var q in Queue.Take(5))
            NextSingers.Add(string.IsNullOrWhiteSpace(q.Singer) ? q.Track.Display : $"{q.Singer} — {q.Track.Display}");
    }

    [RelayCommand] private void ToggleProjector() => IsProjectorOpen = !IsProjectorOpen;

    // ---------------------------------------------------------------- pad

    [RelayCommand]
    private void TriggerPad(PadItem? pad)
    {
        if (pad == null) return;
        if (IsEditingPads) { AssignPadFile(pad); return; }
        if (!pad.HasFile) return;
        if (pad.IsPlaying) { Engine.Pads.Stop(pad.Index); pad.IsPlaying = false; return; }
        try
        {
            Engine.Pads.Play(pad.Index, pad.FilePath!);
            pad.IsPlaying = true;
        }
        catch (Exception ex) { StatusText = "Pad: " + ex.Message; }
    }

    public void TriggerPadByIndex(int idx)
    {
        if (idx >= 0 && idx < Pads.Count) TriggerPad(Pads[idx]);
    }

    [RelayCommand]
    private void StopAllPads()
    {
        Engine.Pads.StopAll();
        foreach (var p in Pads) p.IsPlaying = false;
    }

    [RelayCommand]
    public void AssignPadFile(PadItem? pad)
    {
        if (pad == null) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"File per il pad {pad.Hotkey}",
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac;*.aif;*.aiff|Tutti i file|*.*",
        };
        if (dlg.ShowDialog() != true) return;
        pad.FilePath = dlg.FileName;
        if (string.IsNullOrWhiteSpace(pad.Name)) pad.Name = Path.GetFileNameWithoutExtension(dlg.FileName);
        SaveSettings();
    }

    [RelayCommand]
    private void ClearPad(PadItem? pad)
    {
        if (pad == null) return;
        Engine.Pads.Stop(pad.Index);
        pad.IsPlaying = false;
        pad.FilePath = null;
        pad.Name = "";
        SaveSettings();
    }

    public void SetPadFile(PadItem pad, string path)
    {
        pad.FilePath = path;
        pad.Name = Path.GetFileNameWithoutExtension(path);
        SaveSettings();
    }

    // ---------------------------------------------------------------- playlist interne

    public ObservableCollection<Playlist> Playlists { get; } = new();
    [ObservableProperty] private Playlist? _selectedPlaylist;
    public bool IsPlaylistSelected => SelectedPlaylist != null;

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        OnPropertyChanged(nameof(IsPlaylistSelected));
        ApplyLibrarySort();
        LibraryView.Refresh();
    }

    private void ApplyLibrarySort()
    {
        if (LibraryView is System.Windows.Data.ListCollectionView lcv)
        {
            if (LibraryFilter == "compat")
            {
                lcv.SortDescriptions.Clear();
                lcv.CustomSort = Comparer<object>.Create((a, b) =>
                    _compatScores.GetValueOrDefault(((Track)b).Id).CompareTo(_compatScores.GetValueOrDefault(((Track)a).Id)));
            }
            else if (SelectedPlaylist != null)
            {
                lcv.SortDescriptions.Clear();
                var pl = SelectedPlaylist;
                lcv.CustomSort = Comparer<object>.Create((a, b) =>
                    pl.TrackIds.IndexOf(((Track)a).Id).CompareTo(pl.TrackIds.IndexOf(((Track)b).Id)));
            }
            else
            {
                lcv.CustomSort = null;
                lcv.SortDescriptions.Clear();
                lcv.SortDescriptions.Add(new SortDescription(nameof(Track.Artist), ListSortDirection.Ascending));
                lcv.SortDescriptions.Add(new SortDescription(nameof(Track.Title), ListSortDirection.Ascending));
            }
        }
    }

    private void LoadPlaylists()
    {
        foreach (var p in JsonStore.Load<List<Playlist>>(AppPaths.PlaylistsFile)) Playlists.Add(p);
    }

    public void SavePlaylists()
    {
        try { JsonStore.Save(AppPaths.PlaylistsFile, Playlists.ToList()); } catch { }
    }

    [RelayCommand]
    private void NewPlaylist()
    {
        var name = Views.InputDialog.Show("Nuova playlist", "Nome della playlist:", "Serata " + DateTime.Now.ToString("dd/MM"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new Playlist { Name = name.Trim() };
        Playlists.Add(p);
        SelectedPlaylist = p;
        SavePlaylists();
    }

    [RelayCommand]
    private void RenamePlaylist()
    {
        if (SelectedPlaylist == null) return;
        var name = Views.InputDialog.Show("Rinomina playlist", "Nuovo nome:", SelectedPlaylist.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        SelectedPlaylist.Name = name.Trim();
        SavePlaylists();
    }

    [RelayCommand]
    private void DeletePlaylist()
    {
        if (SelectedPlaylist == null) return;
        if (MessageBox.Show($"Eliminare la playlist \"{SelectedPlaylist.Name}\"? (i file restano)", "Playlist", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = null;
        SavePlaylists();
    }

    /// <summary>Aggiunge il brano selezionato alla playlist indicata (o a quella corrente).</summary>
    [RelayCommand]
    private void AddSelectedToPlaylist(Playlist? target)
    {
        target ??= SelectedPlaylist;
        if (target == null || SelectedTrack == null) return;
        if (target.TrackIds.Contains(SelectedTrack.Id)) { StatusText = "Già nella playlist"; return; }
        target.TrackIds.Add(SelectedTrack.Id);
        target.NotifyCountChanged();
        SavePlaylists();
        StatusText = $"Aggiunto a \"{target.Name}\": {SelectedTrack.Display}";
        if (target == SelectedPlaylist) LibraryView.Refresh();
    }

    [RelayCommand]
    private void RemoveSelectedFromPlaylist()
    {
        if (SelectedPlaylist == null || SelectedTrack == null) return;
        SelectedPlaylist.TrackIds.Remove(SelectedTrack.Id);
        SelectedPlaylist.NotifyCountChanged();
        SavePlaylists();
        LibraryView.Refresh();
    }

    [RelayCommand]
    private void MoveInPlaylist(string? dir)
    {
        if (SelectedPlaylist == null || SelectedTrack == null) return;
        var ids = SelectedPlaylist.TrackIds;
        int i = ids.IndexOf(SelectedTrack.Id);
        int j = dir == "up" ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= ids.Count) return;
        ids.Move(i, j);
        SavePlaylists();
        LibraryView.Refresh();
    }

    /// <summary>Mette tutta la playlist in coda (nell'ordine della playlist).</summary>
    [RelayCommand]
    private void PlaylistToQueue()
    {
        if (SelectedPlaylist == null) return;
        int n = 0;
        foreach (var id in SelectedPlaylist.TrackIds)
        {
            var t = Library.FindById(id);
            if (t == null) continue;
            Queue.Add(new QueueEntry { Track = t });
            n++;
        }
        StatusText = $"{n} brani di \"{SelectedPlaylist.Name}\" aggiunti alla coda";
    }

    // ---------------------------------------------------------------- analisi BPM / tonalità

    private readonly SemaphoreSlim _analyzeGate = new(1, 1);
    private CancellationTokenSource? _analyzeCts;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private string _analyzeStatus = "";

    /// <summary>Analizza un brano (in background, uno alla volta) e aggiorna libreria e deck.</summary>
    public async Task AnalyzeTrackAsync(Track track, CancellationToken ct = default)
    {
        if (track.IsVideo && track.DurationSec <= 0) { /* i video si analizzano comunque dall'audio */ }
        await _analyzeGate.WaitAsync(ct);
        try
        {
            var (audioPath, _) = LibraryService.PrepareForPlayback(track);
            var r = await Task.Run(() => AudioAnalyzer.Analyze(audioPath, ct), ct);
            track.Bpm = r.Bpm;
            track.Key = r.Key;
            track.IntroEndSec = r.IntroEndSec;
            track.OutroStartSec = r.OutroStartSec;
            track.Analyzed = true;
            if (r.Waveform.Length > 0) WaveformStore.Save(track.Id, r.Waveform);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            track.Analyzed = true; // non riprovare all'infinito
            StatusText = $"Analisi fallita per {track.Display}: {ex.Message}";
        }
        finally { _analyzeGate.Release(); }

        Library.Save();
        LibraryView.Refresh();
        foreach (var d in new[] { DeckA, DeckB })
            if (d.Track == track) d.RefreshAnalysisLabels();
    }

    private void AnalyzeInBackground(Track track)
    {
        if (track.Analyzed) return;
        _ = AnalyzeTrackAsync(track).ContinueWith(t => { if (t.Exception != null) Application.Current?.Dispatcher.BeginInvoke(() => StatusText = "Analisi: " + t.Exception.InnerException?.Message); }, TaskContinuationOptions.OnlyOnFaulted);
    }

    [RelayCommand]
    private async Task AnalyzeSelectedAsync()
    {
        if (SelectedTrack == null) return;
        var t = SelectedTrack;
        StatusText = "Analizzo: " + t.Display;
        await AnalyzeTrackAsync(t);
        StatusText = $"{t.Display}: {t.BpmLabel} BPM, tonalità {t.KeyLabel}";
    }

    /// <summary>Analizza tutti i brani della libreria che non hanno ancora BPM/tonalità.</summary>
    [RelayCommand]
    private async Task AnalyzeMissingAsync()
    {
        if (IsAnalyzing) { _analyzeCts?.Cancel(); return; }
        var todo = Tracks.Where(t => !t.Analyzed).ToList();
        if (todo.Count == 0) { StatusText = "Tutti i brani sono già analizzati"; return; }
        _analyzeCts = new CancellationTokenSource();
        IsAnalyzing = true;
        int done = 0;
        try
        {
            foreach (var t in todo)
            {
                _analyzeCts.Token.ThrowIfCancellationRequested();
                AnalyzeStatus = $"Analisi {++done}/{todo.Count}: {t.Display}";
                await AnalyzeTrackAsync(t, _analyzeCts.Token);
            }
            AnalyzeStatus = $"Analisi completata: {todo.Count} brani";
        }
        catch (OperationCanceledException) { AnalyzeStatus = $"Analisi interrotta ({done}/{todo.Count})"; }
        finally { IsAnalyzing = false; }
    }

    // ---------------------------------------------------------------- BPM: sync e blocco

    [ObservableProperty] private bool _bpmLock;
    /// <summary>Nel passaggio (automix / PROSSIMO) porta il brano entrante ai BPM di quello in uscita.</summary>
    [ObservableProperty] private bool _bpmMatch = true;
    [ObservableProperty] private double _bpmLockValue = 120;

    partial void OnBpmLockChanged(bool value)
    {
        if (value) foreach (var d in new[] { DeckA, DeckB }) ApplyTempoForTarget(d, BpmLockValue);
    }

    partial void OnBpmLockValueChanged(double value)
    {
        if (BpmLock) foreach (var d in new[] { DeckA, DeckB }) ApplyTempoForTarget(d, value);
    }

    /// <summary>BPM effettivi del deck (BPM del brano × tempo), 0 se sconosciuti.</summary>
    private static double EffectiveBpm(DeckViewModel d) =>
        d.Track?.Bpm > 0 ? d.Track.Bpm * (1.0 + d.TempoPercent / 100.0) : 0;

    /// <summary>
    /// Imposta il tempo del deck perché il brano suoni a <paramref name="targetBpm"/>,
    /// scegliendo tra tempo normale, doppio e metà quello che richiede meno variazione. Limite ±25 %.
    /// </summary>
    public bool ApplyTempoForTarget(DeckViewModel deck, double targetBpm)
    {
        var bpm = deck.Track?.Bpm ?? 0;
        if (bpm <= 0 || targetBpm <= 0) return false;
        double bestPct = double.MaxValue;
        foreach (var mult in new[] { 1.0, 2.0, 0.5 })
        {
            double pct = (targetBpm / (bpm * mult) - 1) * 100;
            if (Math.Abs(pct) < Math.Abs(bestPct)) bestPct = pct;
        }
        if (Math.Abs(bestPct) > 25) { StatusText = $"Deck {deck.Name}: {bpm:0} BPM troppo lontani da {targetBpm:0} (serve {bestPct:+0;-0} %)"; return false; }
        deck.TempoPercent = (int)Math.Round(bestPct);
        return true;
    }

    /// <summary>Allinea il tempo del deck ai BPM bloccati oppure a quelli dell'altro deck.</summary>
    [RelayCommand]
    private void SyncDeck(DeckViewModel? deck)
    {
        if (deck == null) return;
        var other = deck == DeckA ? DeckB : DeckA;
        double target = BpmLock ? BpmLockValue : EffectiveBpm(other);
        if (target <= 0) { StatusText = "Nessun riferimento BPM: l'altro deck non ha BPM (analizza il brano)"; return; }
        if (ApplyTempoForTarget(deck, target)) StatusText = $"Deck {deck.Name} sincronizzato a {target:0.0} BPM";
    }

    /// <summary>Tempo di riferimento per un brano in ingresso: BPM bloccati, altrimenti quelli del deck in uscita.</summary>
    private void MatchIncomingTempo(DeckViewModel incoming, DeckViewModel? outgoing)
    {
        if (incoming.Track?.IsKaraoke == true) return; // le basi karaoke restano al loro tempo
        if (BpmLock) { ApplyTempoForTarget(incoming, BpmLockValue); return; }
        if (outgoing == null || !BpmMatch) return;
        double target = EffectiveBpm(outgoing);
        if (target > 0) ApplyTempoForTarget(incoming, target);
    }

    // ---------------------------------------------------------------- storico riproduzioni e suggerimenti

    public ObservableCollection<Track> Suggestions { get; } = new();
    [ObservableProperty] private string _suggestionsLabel = "";
    private readonly HashSet<string> _playedThisSession = new();

    private void OnTrackPlayed(DeckViewModel deck, Track track)
    {
        track.PlayCount++;
        track.LastPlayedUtc = DateTime.UtcNow;
        track.PlayedThisSession = true;
        _playedThisSession.Add(track.Id);
        Library.Save();
        LibraryView.Refresh();
        UpdateSuggestions();
    }

    /// <summary>Prossimi brani consigliati: compatibili col brano in riproduzione, non ancora suonati stasera, non in coda.</summary>
    public void UpdateSuggestions()
    {
        Suggestions.Clear();
        var r = CompatReference();
        if (r == null) { SuggestionsLabel = ""; return; }
        var queued = new HashSet<string>(Queue.Select(q => q.Track.Id));
        var scored = Tracks
            .Where(t => t.Id != r.Id && !queued.Contains(t.Id) && !t.PlayedThisSession && !t.IsKaraoke)
            .Select(t => (t, s: SearchUtil.Compatibility(r, t) * (t.Analyzed ? 1.0 : 0.6)))
            .Where(x => x.s > 0.3)
            .OrderByDescending(x => x.s)
            .ThenBy(x => x.t.PlayCount)
            .Take(6)
            .ToList();
        foreach (var (t, s) in scored) { t.MatchLabel = (s * 100).ToString("0") + "%"; Suggestions.Add(t); }
        SuggestionsLabel = scored.Count == 0 ? $"Nessun suggerimento per {r.Display}" : $"dopo: {r.Display}";
    }

    [RelayCommand] private void RefreshSuggestions() => UpdateSuggestions();

    [RelayCommand]
    private void SuggestionToQueue(Track? t)
    {
        if (t == null) return;
        Queue.Add(new QueueEntry { Track = t });
        Suggestions.Remove(t);
        StatusText = "In coda: " + t.Display;
    }

    [RelayCommand]
    private void SuggestionToFreeDeck(Track? t)
    {
        if (t == null) return;
        var deck = FreeDeck();
        if (LoadToDeck(deck, t)) MatchIncomingTempo(deck, deck == DeckA ? DeckB : DeckA);
    }

    [RelayCommand]
    private void ClearPlayedMarks()
    {
        foreach (var t in Tracks) t.PlayedThisSession = false;
        _playedThisSession.Clear();
        LibraryView.Refresh();
        UpdateSuggestions();
    }

    // ---------------------------------------------------------------- rinomina intelligente

    [RelayCommand]
    private void CleanSelectedTitle()
    {
        if (SelectedTrack == null) return;
        var t = SelectedTrack;
        if (TitleCleaner.Apply(t, writeTags: true)) { Library.Save(); LibraryView.Refresh(); StatusText = $"Rinominato: {t.Display}"; }
        else StatusText = "Titolo già pulito";
        foreach (var d in new[] { DeckA, DeckB }) if (d.Track == t) { d.Title = t.Title; d.Artist = t.Artist; }
    }

    [RelayCommand]
    private async Task CleanAllTitlesAsync()
    {
        if (MessageBox.Show("Pulire titolo e artista di tutti i brani della libreria?\n(scrive anche i tag ID3 nei file; i nomi dei file non cambiano)",
                "Rinomina intelligente", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        int n = 0;
        var list = Tracks.ToList();
        await Task.Run(() => { foreach (var t in list) if (TitleCleaner.Apply(t, writeTags: true)) n++; });
        Library.Save();
        LibraryView.Refresh();
        StatusText = $"Rinomina intelligente: {n} brani aggiornati";
    }

    // ---------------------------------------------------------------- Demucs (separazione voce)

    public StemService Stems { get; } = new();
    [ObservableProperty] private string _aiStatus = "";
    [ObservableProperty] private double _aiPercent = -1;
    [ObservableProperty] private bool _aiBusy;

    private void WireStems()
    {
        Stems.Progress += (msg, pct) => { AiStatus = msg; AiPercent = pct; };
        DeckA.PrepareStems = PrepareStemsAsync;
        DeckB.PrepareStems = PrepareStemsAsync;
    }

    /// <summary>Genera (se manca) la base senza voce del brano con Demucs. Ritorna true se disponibile.</summary>
    public async Task<bool> PrepareStemsAsync(Track track)
    {
        if (track.HasInstrumental) return true;
        if (AiBusy) { StatusText = "Demucs è già al lavoro su un altro brano"; return false; }
        AiBusy = true;
        try
        {
            var (audioPath, _) = LibraryService.PrepareForPlayback(track);
            var (inst, voc) = await Stems.SeparateAsync(track.Id, audioPath, CancellationToken.None);
            track.InstrumentalPath = inst;
            track.VocalsPath = voc;
            Library.Save();
            StatusText = $"Base senza voce pronta: {track.Display}";
            foreach (var d in new[] { DeckA, DeckB }) if (d.Track == track) d.HasInstrumental = true;
            return true;
        }
        catch (Exception ex)
        {
            AiStatus = "Errore Demucs: " + ex.Message;
            MessageBox.Show(ex.Message, "Separazione voce (Demucs)", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally { AiBusy = false; }
    }

    [RelayCommand]
    private async Task PrepareStemsSelectedAsync()
    {
        if (SelectedTrack == null) return;
        await PrepareStemsAsync(SelectedTrack);
    }

    [RelayCommand]
    private async Task InstallAiAsync()
    {
        if (AiBusy) return;
        AiBusy = true;
        try { await Stems.EnsureInstalledAsync(CancellationToken.None); StatusText = "Motore AI (Demucs) installato"; }
        catch (Exception ex) { AiStatus = "Errore: " + ex.Message; MessageBox.Show(ex.Message, "Installazione Demucs", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { AiBusy = false; }
    }

    // ---------------------------------------------------------------- aggiornamenti (Velopack / GitHub Releases)

    public UpdateService Updater { get; } = new();
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updating;
    public string AppVersion => "v" + Updater.CurrentVersion;

    public async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var v = await Updater.CheckAsync();
            if (v != null) { UpdateAvailable = true; UpdateStatus = $"Aggiornamento {v} disponibile"; }
            else if (!silent) UpdateStatus = Updater.IsInstalled ? "Nessun aggiornamento" : "Versione non installata (portabile/debug): niente auto-update";
        }
        catch (Exception ex) { if (!silent) UpdateStatus = "Controllo aggiornamenti fallito: " + ex.Message; }
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync() => await CheckForUpdatesAsync(silent: false);

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (!UpdateAvailable || Updating) return;
        if (DeckA.IsPlaying || DeckB.IsPlaying)
        {
            if (MessageBox.Show("C'è musica in riproduzione: l'aggiornamento riavvia l'app. Continuare?", "Aggiornamento", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        }
        Updating = true;
        try
        {
            SaveSettings(); SaveQueue(); SavePlaylists();
            await Updater.DownloadAndApplyAsync(new Progress<int>(p => UpdateStatus = $"Scarico aggiornamento… {p}%"));
        }
        catch (Exception ex) { UpdateStatus = "Aggiornamento fallito: " + ex.Message; Updating = false; }
    }

    // ---------------------------------------------------------------- MIDI

    private void HandleMidiAction(string action, int value, bool continuous)
    {
        double norm = value / 127.0;
        // per i pulsanti mappati su CC consideriamo solo la pressione (valore alto)
        bool pressed = !continuous || value >= 64;
        var deck = action.StartsWith("a.") ? DeckA : action.StartsWith("b.") ? DeckB : null;
        var sub = deck != null ? action[2..] : action;

        if (deck != null)
        {
            switch (sub)
            {
                case "play": if (pressed) deck.TogglePlay(); break;
                case "stop": if (pressed) deck.Stop(); break;
                case "volume": deck.Volume = norm; break;
                case "tempo": deck.TempoPercent = (int)Math.Round((norm - 0.5) * 50); break; // -25..+25
                case "keyup": if (pressed) deck.KeyUp(); break;
                case "keydown": if (pressed) deck.KeyDown(); break;
                case "keyreset": if (pressed) deck.KeyReset(); break;
                case "keylock": if (pressed) deck.KeyLock = !deck.KeyLock; break;
            }
            return;
        }
        switch (action)
        {
            case "crossfader": _crossfadeTarget = null; Crossfader = norm * 2 - 1; break;
            case "master": MasterVolume = norm * 1.2; break;
            case "next": if (pressed) PlayNextCommand.Execute(null); break;
            case "fadeA": if (pressed) StartCrossfade(-1); break;
            case "fadeB": if (pressed) StartCrossfade(1); break;
            case "projector": if (pressed) IsProjectorOpen = !IsProjectorOpen; break;
            case "padstop": if (pressed) StopAllPadsCommand.Execute(null); break;
            default:
                if (action.StartsWith("pad") && int.TryParse(action[3..], out var n) && pressed) TriggerPadByIndex(n - 1);
                break;
        }
    }

    public void ApplyMidiDevice(string? name)
    {
        Settings.MidiDeviceName = name;
        if (string.IsNullOrEmpty(name)) { Midi.Close(); StatusText = "MIDI disattivato"; return; }
        StatusText = Midi.Open(name) ? "MIDI: " + name : "Impossibile aprire " + name;
    }

    // ---------------------------------------------------------------- download

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var input = DownloadInput.Trim();
        if (string.IsNullOrEmpty(input) || IsDownloading) return;
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadPercent = 0;
        var progress = new Progress<DownloadStatus>(s =>
        {
            DownloadStatus = s.Message;
            if (s.Percent >= 0) DownloadPercent = s.Percent;
        });
        try
        {
            var paths = await Downloader.DownloadAsync(input, DownloadVideo, progress, _downloadCts.Token);
            Track? first = null;
            int added = 0;
            foreach (var path in paths)
            {
                var track = Library.AddFile(path);
                if (track == null) continue;
                TitleCleaner.Apply(track, writeTags: true);
                var old = Tracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (old != null) Tracks.Remove(old);
                Tracks.Add(track);
                first ??= track;
                added++;
                AnalyzeInBackground(track);
            }
            Library.Save();
            LibraryCount = Tracks.Count;
            if (first != null) SelectedTrack = first;
            DownloadStatus = added == 1 && first != null ? "Scaricato: " + first.Display : $"Scaricati {added} brani — {DownloadStatus}";
            DownloadInput = "";
            if (!Settings.LibraryFolders.Contains(AppPaths.DownloadsDir, StringComparer.OrdinalIgnoreCase))
            {
                Settings.LibraryFolders.Add(AppPaths.DownloadsDir);
                SaveSettings();
            }
        }
        catch (OperationCanceledException) { DownloadStatus = "Annullato"; }
        catch (Exception ex) { DownloadStatus = "Errore: " + ex.Message; }
        finally { IsDownloading = false; }
    }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();
}
