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
    private DateTime _lastAutosave = DateTime.UtcNow;

    public MainViewModel()
    {
        AppPaths.EnsureDirs();
        Settings = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
        if (!string.IsNullOrWhiteSpace(Settings.DownloadFolder)) AppPaths.DownloadsDir = Settings.DownloadFolder;
        Engine = new AudioEngine();
        Rhythm = new RhythmViewModel(Engine.Rhythm, CurrentSetBpm);
        Library = new LibraryService();
        Plugins = new PluginManager();
        Midi = new MidiService();
        Midi.ActionTriggered += HandleMidiAction;

        DeckA = new DeckViewModel(Engine.DeckA);
        DeckB = new DeckViewModel(Engine.DeckB);
        foreach (var d in new[] { DeckA, DeckB })
        {
            d.TrackEnded += OnDeckEnded;
            d.TrackLoaded += dv => { if (_autoMixTriggeredFor == dv) _autoMixTriggeredFor = null; UpdateProjectorState(); UpdateSuggestions(); };
            d.Played += OnTrackPlayed;
            d.CuesChanged += dv => { if (dv.Track != null) Library.Save(dv.Track); LibraryView.Refresh(); };
            d.GenreChanged += t => AfterGenreChange(t);
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
        AutoMixEndless = Settings.AutoMixEndless;
        SetGenres = Settings.SetGenres ?? "";
        MixViewVisible = Settings.MixViewVisible;
        HideCryptic = Settings.HideCryptic;
        BottomStripVisible = Settings.BottomStripVisible;
        DeckA.FxVisible = DeckB.FxVisible = Settings.FxPanelsVisible;
        foreach (var d in new[] { DeckA, DeckB })
            d.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(DeckViewModel.FxVisible) && s is DeckViewModel dv) Settings.FxPanelsVisible = dv.FxVisible; };
        BpmLock = Settings.BpmLock;
        BpmLockValue = Settings.BpmLockValue;
        BpmMatch = Settings.BpmMatch;
        TransitionStyle = string.IsNullOrEmpty(Settings.TransitionStyle) || Settings.TransitionStyle == "glide" ? "auto" : Settings.TransitionStyle;
        SuggestBy = Settings.SuggestBy ?? "";
        CrossfadeSeconds = Settings.CrossfadeSeconds;
        IdleTitle = Settings.IdleTitle;
        IdleSubtitle = Settings.IdleSubtitle;

        Queue.CollectionChanged += (_, _) => { UpdateProjectorState(); SaveQueue(); UpdateSuggestions(); PrefetchNextGrid(); };
        InitLive();
        Engine.OutputRestarted += msg => Application.Current?.Dispatcher.BeginInvoke(() => { StatusText = msg; CrashLog.Write("audio: " + msg); });

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(40) };
        _timer.Tick += (_, _) => Tick();
    }

    public AppSettings Settings { get; }
    public AudioEngine Engine { get; }
    /// <summary>Sequencer ritmico di supporto (finestra Ritmi).</summary>
    public RhythmViewModel Rhythm { get; }

    /// <summary>BPM "della serata": blocco BPM se attivo, altrimenti il deck che si sente di più; 0 se ignoti.</summary>
    public double CurrentSetBpm()
    {
        if (BpmLock) return BpmLockValue;
        var r = (DeckA.IsPlaying && DeckB.IsPlaying) ? (Crossfader <= 0 ? DeckA : DeckB)
              : DeckA.IsPlaying ? DeckA : DeckB.IsPlaying ? DeckB : null;
        return r == null ? 0 : EffectiveBpm(r);
    }
    public LibraryService Library { get; }
    public PluginManager Plugins { get; }
    /// <summary>Sorgenti di importazione disponibili (integrate + plugin) e quella scelta.</summary>
    public ObservableCollection<Mixfonia.Plugins.IImportSource> ImportSources { get; } = new();
    [ObservableProperty] private Mixfonia.Plugins.IImportSource? _selectedSource;
    partial void OnSelectedSourceChanged(Mixfonia.Plugins.IImportSource? value) { if (value != null) Settings.ImportSourceId = value.Id; OnPropertyChanged(nameof(ImportHint)); }
    public string ImportHint => SelectedSource?.InputHint ?? "Nessuna sorgente: Impostazioni → Plugin e fonti";
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
    partial void OnSelectedTrackChanged(Track? value) { OnPropertyChanged(nameof(GenreOptions)); OnPropertyChanged(nameof(SelectedTrackGenres)); }
    /// <summary>Tag genere del brano selezionato in libreria (riga sotto la griglia).</summary>
    public IEnumerable<string> SelectedTrackGenres => SelectedTrack?.Genres.ToList() ?? new List<string>();
    [ObservableProperty] private QueueEntry? _selectedQueueEntry;
    [ObservableProperty] private string _singerName = "";
    [ObservableProperty] private int _queueKeyShift;
    [ObservableProperty] private double _crossfader;
    [ObservableProperty] private double _masterVolume = 1.0;
    [ObservableProperty] private bool _autoMix;
    [ObservableProperty] private bool _autoMixUseCues = true;
    /// <summary>Automix senza fine: se la coda è vuota pesca dai suggeriti, poi da tutta la libreria. Non si ferma mai.</summary>
    [ObservableProperty] private bool _autoMixEndless = true;
    /// <summary>Vista di mixaggio (onde sovrapposte) visibile.</summary>
    [ObservableProperty] private bool _mixViewVisible = true;
    partial void OnMixViewVisibleChanged(bool value) => Settings.MixViewVisible = value;
    /// <summary>Striscia in basso (jingle + download) visibile: nascosta dà spazio a libreria e coda.</summary>
    [ObservableProperty] private bool _bottomStripVisible = true;
    partial void OnBottomStripVisibleChanged(bool value) => Settings.BottomStripVisible = value;
    partial void OnAutoMixEndlessChanged(bool value) => Settings.AutoMixEndless = value;
    /// <summary>Generi della serata (separati da virgola): l'automix pesca solo lì.</summary>
    [ObservableProperty] private string _setGenres = "";
    partial void OnSetGenresChanged(string value) => Settings.SetGenres = value;
    [ObservableProperty] private double _crossfadeSeconds = 6;
    [ObservableProperty] private string _statusText = "Pronto";
    /// <summary>Dimensione attuale dell'interfaccia, mostrata nella barra di stato (Ctrl + / − / 0).</summary>
    [ObservableProperty] private string _uiScaleLabel = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private double _scanPercent;
    [ObservableProperty] private bool _isProjectorOpen;
    /// <summary>Monitor di regia: finestrella sullo schermo del DJ con la stessa scena del proiettore.</summary>
    [ObservableProperty] private bool _isMonitorOpen;
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
    [ObservableProperty] private double _masterL;
    [ObservableProperty] private double _masterR;

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
        ResolveFillPlaylist();
        JamendoSource.ClientId = Settings.JamendoClientId;
        Plugins.Load(TrackExists, s => StatusText = s);
        foreach (var src in Plugins.ImportSources) ImportSources.Add(src);
        SelectedSource = ImportSources.FirstOrDefault(s => s.Id == Settings.ImportSourceId) ?? ImportSources.FirstOrDefault();
        WireStems();
        StartAnimation();
        LoadLicense();
        Keys.Load(Settings.KeyMappings, useDefaultsIfEmpty: true);
        StartControllerWatch();
        UsageStats.Load();
        if (Settings.UsageStatsOptIn)
        {
            _ = UsageStats.SendAsync(Settings);
            _ = UsageStats.FetchModelAsync();
        }
        _timer.Start();
        StatusText = $"Uscita: {Engine.OutputDescription} · {Tracks.Count} brani";
        if (Settings.LibraryFolders.Count > 0) StartFolderWatchers();
        _ = VerifyDbAsync();
        _ = CheckForUpdatesAsync(silent: true);
    }


    /// <summary>(artista, titolo) → true se il brano è già in libreria (per saltare i doppioni durante le importazioni).</summary>
    public bool TrackExists(string artist, string title)
    {
        var a = SearchUtil.NormalizeForCompare(artist);
        var t = SearchUtil.NormalizeForCompare(title);
        if (t.Length < 3) return false;
        return Library.Tracks.Any(x =>
        {
            var xt = SearchUtil.NormalizeForCompare(x.Title);
            var xa = SearchUtil.NormalizeForCompare(x.Artist);
            bool titleOk = xt.Contains(t) || (xt.Length >= 3 && t.Contains(xt));
            bool artistOk = a.Length == 0 || xa.Contains(a) || a.Contains(xa) || xt.Contains(a);
            return titleOk && artistOk;
        });
    }

    /// <summary>Salvataggio d'emergenza da un crash: solo file, niente audio, non deve lanciare.</summary>
    public void EmergencySave()
    {
        try { SaveSettings(); } catch { }
        try { SaveQueue(); } catch { }
        try { Rhythm.Save(); } catch { }
    }

    public void Shutdown()
    {
        // statistiche: quello che non è partito resta sul PC e si riproverà la prossima volta
        UsageStats.Save();
        if (Settings.UsageStatsOptIn) { try { UsageStats.SendAsync(Settings, force: true).Wait(TimeSpan.FromSeconds(4)); } catch { } }
        _timer.Stop();
        _scanCts?.Cancel();
        _downloadCts?.Cancel();
        SaveSettings();
        Rhythm.Save();
        _remote?.Dispose();
        SaveQueue();
        Midi.Dispose();
        _suno?.Dispose();
        SaveCelebration();
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
        Settings.MidiMappings = UserMidiMappings();
        Settings.KeyMappings = Keys.Export();
        Settings.Pads = Pads.Select(p => new PadDto { Index = p.Index, Name = p.Name, FilePath = p.FilePath }).ToList();
        SaveLiveSettings();
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
        if (IsAnalyzing && _analyzeTotal > 0 && DateTime.UtcNow.Millisecond < 120)
            AnalyzeStatus = $"Analisi {_analyzeDone}/{_analyzeTotal} · {(int)(DateTime.UtcNow - _analyzeStartedUtc).TotalSeconds} s: {_analyzeCurrent}";
        double ml = Views.LevelMeter.ToScale(Engine.MasterPeakL), mr = Views.LevelMeter.ToScale(Engine.MasterPeakR);
        MasterL = ml > MasterL ? ml : Math.Max(0, MasterL - 0.06);
        MasterR = mr > MasterR ? mr : Math.Max(0, MasterR - 0.06);
        if (MicOn) { double mm = Views.LevelMeter.ToScale(Engine.Mic.Peak); MicLevel = mm > MicLevel ? mm : Math.Max(0, MicLevel - 0.06); }

        if (_crossfadeTarget is double target)
        {
            double v = Crossfader;
            double step = _crossfadeSpeed * dt;
            if (Math.Abs(target - v) <= step)
            {
                Crossfader = target;
                _crossfadeTarget = null;
                EndTransition();
                // a fine dissolvenza fermiamo il deck che è stato sfumato
                var faded = target > 0 ? DeckA : DeckB;
                if (faded.IsPlaying) faded.Deck.Pause();
                if (faded.HasTrack) faded.Deck.Seek(0);
                if (_autoMixTriggeredFor == faded) _autoMixTriggeredFor = null;
            }
            else
            {
                Crossfader = v + Math.Sign(target - v) * step;
                StepTransition(Crossfader);
            }
        }

        TickMix(dt);
        // salvataggio periodico: se l'app cade a metà serata, impostazioni e coda sono al massimo di un minuto fa
        if ((now - _lastAutosave).TotalSeconds > 60) { _lastAutosave = now; try { SaveSettings(); SaveQueue(); } catch (Exception ex) { CrashLog.Write("autosave: " + ex.Message); } }
        if (AutoMix) CheckAutoMix();
        CheckFillMusic();
        TickControllerJog();
        UpdateActiveKaraokeDeck();
        UpdateDedication();
    }

    partial void OnCrossfaderChanged(double value) => Engine.Crossfader = value;
    partial void OnMasterVolumeChanged(double value) => Engine.MasterVolume = (float)value;

    // ---------------------------------------------------------------- libreria

    /// <summary>Nasconde i titoli incomprensibili (file del Cestino "$R…", codici senza vocali).</summary>
    [ObservableProperty] private bool _hideCryptic = true;
    partial void OnHideCrypticChanged(bool value) { Settings.HideCryptic = value; LibraryView.Refresh(); }

    private static readonly System.Text.RegularExpressions.Regex CrypticRx = new(@"^[A-Z0-9$_-]{5,}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    public static bool IsCryptic(Track t)
    {
        var name = Path.GetFileNameWithoutExtension(t.FilePath);
        if (name.StartsWith("$R") || name.StartsWith("$I")) return true;         // Cestino di Windows
        if (!string.IsNullOrWhiteSpace(t.Artist)) return false;
        var title = t.Title.Trim();
        if (title.Length < 5 || title.Contains(' ')) return false;
        if (!CrypticRx.IsMatch(title)) return false;
        bool digits = title.Any(char.IsDigit), letters = title.Any(char.IsLetter);
        bool vowels = title.Any(c => "AEIOU".Contains(c));
        return (digits && letters) || !vowels;                                 // codici tipo RG3ORPK, BXKTRZ
    }

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
            "midi" => t.IsMidi,
            "compat" => _compatScores.TryGetValue(t.Id, out var sc) && sc >= 0.35,
            "key" => KeyFilterOk(t),
            _ => true,
        };
        if (!kindOk) return false;
        if (HideCryptic && IsCryptic(t)) return false;
        if (!ShowDuplicates && t.HiddenDuplicateOf != null) return false;
        if (_genreFilter.Count > 0 && !t.Genres.Any(g => _genreFilter.Contains(g))) return false;
        if (t.Missing) return false;
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
            double s = SuggestScore(r, t);
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
        CollapseDuplicates();
        RebuildGenreChips();
        _remote?.LibraryChanged();
    }

    /// <summary>
    /// Brani uguali (stesso tipo, artista e titolo, durata simile) vengono mostrati una volta sola: resta visibile la copia migliore
    /// (qualità/tag/analisi), le altre sono "doppioni nascosti" e restano nel database. Solo raggruppamento: nessun file viene toccato.
    /// </summary>
    private void CollapseDuplicates()
    {
        foreach (var t in Tracks) t.HiddenDuplicateOf = null;
        int hidden = 0;
        foreach (var g in Tracks.Where(t => !t.IsMidi).GroupBy(t => t.Kind + "|" + SearchUtil.NormalizeForCompare(t.Artist) + "|" + SearchUtil.NormalizeForCompare(t.Title)))
        {
            if (g.Key.Length < 6 || g.Count() < 2) continue;
            var remaining = g.OrderByDescending(DuplicateFinder.Score).ToList();
            while (remaining.Count > 1)
            {
                var keep = remaining[0];
                var same = remaining.Skip(1).Where(t => keep.DurationSec <= 0 || t.DurationSec <= 0 || Math.Abs(t.DurationSec - keep.DurationSec) <= 3).ToList();
                foreach (var t in same) { t.HiddenDuplicateOf = keep; hidden++; }
                remaining.RemoveAll(t => t == keep || same.Contains(t));
            }
        }
        HiddenDuplicates = hidden;
    }

    /// <summary>Numero di copie nascoste perché doppioni di un altro brano.</summary>
    [ObservableProperty] private int _hiddenDuplicates;
    /// <summary>Mostra anche le copie doppie (per scegliere/eliminare a mano).</summary>
    [ObservableProperty] private bool _showDuplicates;
    partial void OnShowDuplicatesChanged(bool value) => LibraryView.Refresh();

    /// <summary>
    /// File e cartelle trascinati nella libreria da Esplora risorse: i file vengono aggiunti subito,
    /// le cartelle entrano fra quelle della libreria e vengono scandite.
    /// </summary>
    public async void AddPathsToLibrary(IEnumerable<string> paths)
    {
        int added = 0; bool folders = false;
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                folders = true;
                if (!Settings.LibraryFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) Settings.LibraryFolders.Add(p);
            }
            else if (File.Exists(p) && Library.AddFile(p) != null) added++;
        }
        if (added > 0) { RefreshTracks(); Library.Save(); StatusText = added == 1 ? "Aggiunto 1 brano alla libreria" : $"Aggiunti {added} brani alla libreria"; }
        if (folders) { SaveSettings(); await RescanAsync(); }
        else if (added == 0) StatusText = "Niente da aggiungere: trascina file audio, video o karaoke";
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
            var added = await Library.ScanAsync(Settings.LibraryFolders, progress, _scanCts.Token);
            RefreshTracks();
            StartFolderWatchers();
            StatusText = $"Libreria: {Tracks.Count} brani";

            // 1) rinomina intelligente dei brani nuovi/riletti (titolo senza artista, artista corretto), senza toccare i file
            int cleaned = 0;
            await Task.Run(() => { foreach (var t in added) if (TitleCleaner.Apply(t, writeTags: false)) cleaned++; });
            if (cleaned > 0) { Library.Save(added); LibraryView.Refresh(); }

            // 2) i doppioni non si toccano: in libreria se ne vede uno solo (CollapseDuplicates); il pulsante Doppioni serve per liberare spazio

            // 3) analisi BPM/tonalità/forma d'onda di tutti i brani non ancora analizzati, in background
            if (!IsAnalyzing && Tracks.Any(t => !t.Analyzed)) _ = AnalyzeMissingAsync();
            StatusText = $"Libreria: {Tracks.Count} brani" + (cleaned > 0 ? $" · {cleaned} titoli sistemati" : "") + (Tracks.Any(t => !t.Analyzed) ? " · analisi in corso…" : "");
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

    /// <summary>Mixa subito il brano selezionato in libreria (o passato come parametro).</summary>
    [RelayCommand]
    private void MixNow(Track? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        PlayNextWith(new QueueEntry { Track = t, Singer = SingerName.Trim(), KeyShift = QueueKeyShift });
        SingerName = ""; QueueKeyShift = 0;
    }

    /// <summary>Mette il brano in cima alla coda (prossimo a partire).</summary>
    [RelayCommand]
    private void QueueToTop(Track? t)
    {
        t ??= SelectedTrack;
        if (t == null) return;
        Queue.Insert(0, new QueueEntry { Track = t, Singer = SingerName.Trim(), KeyShift = QueueKeyShift });
        SingerName = ""; QueueKeyShift = 0;
        StatusText = $"In cima alla coda: {t.Display}";
    }

    [RelayCommand] private void QueueEntryMixNow(QueueEntry? e) { if (e != null) PlayNextWith(e); }
    [RelayCommand] private void QueueEntryToTop(QueueEntry? e) { if (e == null) return; int i = Queue.IndexOf(e); if (i > 0) Queue.Move(i, 0); }

    /// <summary>Brano trascinato dalla libreria sulla coda: va nel punto dove è stato lasciato (o in fondo).</summary>
    public void AddTrackToQueue(Track track, QueueEntry? before)
    {
        AddToQueue(track, SingerName, QueueKeyShift);
        SingerName = ""; QueueKeyShift = 0;
        if (before == null || Queue.Count < 2) return;
        int to = Queue.IndexOf(before);
        if (to >= 0 && to < Queue.Count - 1) Queue.Move(Queue.Count - 1, to);
    }

    public void AddToQueue(Track track, string singer, int keyShift)
    {
        if (keyShift == 0) keyShift = RememberedKey(singer, track);
        Queue.Add(new QueueEntry { Track = track, Singer = singer.Trim(), KeyShift = keyShift });
        ApplyRotation();
        RefreshKnownSingers();
        StatusText = $"In coda: {track.Display}" + (string.IsNullOrWhiteSpace(singer) ? "" : $" ({singer})");
    }

    [RelayCommand]
    private void RemoveFromQueue(QueueEntry? entry)
    {
        entry ??= SelectedQueueEntry;
        if (entry == null) return;
        // se l'aveva scelto l'automix e il DJ lo toglie, stasera non va riproposto (altrimenti tornerebbe subito)
        if (entry.Note.StartsWith("automix", StringComparison.OrdinalIgnoreCase)) Feedback.RejectForSession(entry.Track);
        Queue.Remove(entry);
        UpdateSuggestions();
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

    public MidiRenderService MidiRender { get; } = new();

    /// <summary>MIDI/KAR: rende l'audio (la prima volta scarica FluidSynth + soundfont) e poi carica sul deck.</summary>
    private async void LoadMidiAsync(DeckViewModel deck, Track track, string singer, int keyShift)
    {
        try
        {
            StatusText = $"Preparo il MIDI: {track.Display}…";
            var handler = new Action<string, double>((m, p) => StatusText = m);
            MidiRender.Progress += handler;
            try { await MidiRender.RenderAsync(track.Id, track.FilePath, CancellationToken.None); }
            finally { MidiRender.Progress -= handler; }
            deck.TempoPercent = 0;
            deck.Load(track, singer, keyShift);
            StatusText = $"Deck {deck.Name}: {track.Display}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"MIDI non riproducibile \"{track.Display}\":\n{ex.Message}", "MIDI / KAR", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// File trascinato da Esplora risorse su un deck: se è già in libreria lo carica, altrimenti lo aggiunge e poi lo carica.
    /// </summary>
    public bool LoadFileToDeck(DeckViewModel deck, string path)
    {
        if (!File.Exists(path)) { StatusText = "File non trovato: " + path; return false; }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!SourceFactory.AudioExtensions.Contains(ext) && !SourceFactory.VideoExtensions.Contains(ext) && ext is not (".cdg" or ".kar" or ".mid" or ".midi"))
        { StatusText = "Formato non supportato: " + ext; return false; }
        var track = Library.Tracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) ?? Library.AddFile(path);
        if (track == null) { StatusText = "Non riesco ad aggiungere " + Path.GetFileName(path); return false; }
        if (!Tracks.Contains(track)) { RefreshTracks(); }
        return LoadToDeck(deck, track);
    }

    public bool LoadToDeck(DeckViewModel deck, Track track, string singer = "", int keyShift = 0, bool confirmIfPlaying = true)
    {
        if (confirmIfPlaying && deck.IsPlaying)
        {
            var r = MessageBox.Show($"Il DECK {deck.Name} sta suonando \"{deck.Title}\".\nSostituirlo con \"{track.Display}\"?",
                "Deck in riproduzione", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return false;
        }
        if (_mix != null && (_mix.In == deck || _mix.Out == deck)) AbortMix("deck ricaricato");
        if (track.IsMidi && MidiRenderService.Rendered(track.Id) == null) { LoadMidiAsync(deck, track, singer, keyShift); return true; }
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
    private void PlayNext() => PlayNextWith(null);

    /// <summary>MIX NOW: carica il brano (o il primo in coda) sul deck libero, lo avvia e sfuma subito. Con automix "senza fine" e coda vuota pesca dai suggeriti.</summary>
    public void PlayNextWith(QueueEntry? entry)
    {
        var playing = DeckA.IsPlaying && (!DeckB.IsPlaying || Crossfader <= 0) ? DeckA
                    : DeckB.IsPlaying ? DeckB : null;
        var target = playing == DeckA ? DeckB : DeckA;

        if (entry == null && Queue.Count == 0 && AutoMixEndless && playing != null) TryFillQueueFromSuggestions(playing);
        var e = entry ?? (Queue.Count > 0 ? Queue[0] : null);
        if (e != null)
        {
            if (!LoadToDeck(target, e.Track, e.Singer, e.KeyShift)) return;
            Queue.Remove(e);
        }
        else if (!target.HasTrack)
        {
            StatusText = "Coda vuota e nessun brano sul deck " + target.Name;
            return;
        }
        if (playing != null) _autoMixTriggeredFor = playing; // l'automix non deve rifare il passaggio

        if (playing != null && playing.Track != null && target.Track != null)
        {
            // passaggio "da DJ": parte sul prossimo battere, beat agganciati, tecnica scelta in base allo stile
            _ = MixToAsync(playing, target, startNow: true);
            return;
        }
        MatchIncomingTempo(target, playing);
        if (AutoMixUseCues && target.Track?.IsKaraoke == false && target.Track.IntroEndSec > 2 && target.Deck.PositionSec < 1)
            target.Deck.Seek(Math.Max(0, target.Track.IntroEndSec - 1));
        target.Deck.Play();
        Crossfader = target == DeckB ? 1 : -1;
    }

    [RelayCommand] private void CrossfadeToA() => StartCrossfade(-1);
    [RelayCommand] private void CrossfadeToB() => StartCrossfade(1);

    public void StartCrossfade(double target)
    {
        double secs = Math.Max(0.2, CrossfadeSeconds);
        var incoming = target > 0 ? DeckB : DeckA;
        var outgoing = target > 0 ? DeckA : DeckB;
        if (outgoing.Track != null) _lastOutgoingTrack = outgoing.Track;
        if (incoming.HasTrack && outgoing.HasTrack && outgoing.IsPlaying && Math.Abs(Crossfader - target) > 0.1) BeginTransition(outgoing, incoming, target);
        else _transition = null;
        _crossfadeTarget = target;
        _crossfadeSpeed = 2.0 / secs; // percorso da -1 a +1 in "secs" secondi
    }

    /// <summary>Automix in pausa: finisce il brano e si ferma lì, senza perdere le impostazioni (tasto PAUSA della sezione).</summary>
    [ObservableProperty] private bool _autoMixHold;
    partial void OnAutoMixHoldChanged(bool value)
    {
        OnPropertyChanged(nameof(AutoMixStateLabel));
        StatusText = value ? "Auto-mix in pausa: finisce questo brano e si ferma" : AutoMix ? "Auto-mix attivo" : "Auto-mix spento";
    }
    partial void OnAutoMixChanged(bool value) { Settings.AutoMix = value; OnPropertyChanged(nameof(AutoMixStateLabel)); }

    /// <summary>Stato leggibile per la sezione automix (anche a due metri dal portatile).</summary>
    public string AutoMixStateLabel => !AutoMix ? "SPENTO" : AutoMixHold ? "IN PAUSA" : "ATTIVO";

    /// <summary>
    /// FERMA TUTTO (Esc). In serata deve esistere un comando che, qualunque cosa stia succedendo, riporta il silenzio:
    /// ferma i due deck, i pad, la batteria, il riempimento, il passaggio in corso e spegne l'automix.
    /// Da qui in poi niente può ripartire da solo: tutto quello che fa partire la musica da sé viene disattivato.
    /// </summary>
    [RelayCommand]
    public void Panic()
    {
        try { if (IsMixing) AbortMix("FERMA TUTTO"); } catch { }
        _crossfadeTarget = null;
        AutoMix = false; AutoMixHold = false;
        AutoMixEndless = false;                       // "mai fermarsi" è la cosa che più facilmente rifà partire la musica
        if (FillMusicOn) FillMusicOn = false; else StopFill(fade: false);
        foreach (var d in new[] { DeckA, DeckB })
        {
            d.CancelEchoOutCommand.Execute(null);
            d.AutoJog = false; d.IsJogging = false;
            d.Deck.Pause();
            d.IsFill = false; d.GainDb = 0;
        }
        _fillDeck = null;
        _autoMixTriggeredFor = null;
        try { StopAllPads(); } catch { }
        try { if (Rhythm.Engine.IsRunning) Rhythm.Engine.Stop(); } catch { }
        TalkOver = false;
        StatusText = "FERMA TUTTO: silenzio. Auto-mix e riempimento spenti, niente riparte da solo.";
    }

    /// <summary>La console non comanda più niente (resta collegata e visibile nella spia): via di fuga se manda da sola.</summary>
    [ObservableProperty] private bool _midiMuted;
    partial void OnMidiMutedChanged(bool value)
    {
        Midi.Muted = value;
        StatusText = value ? "Console ignorata: i comandi arrivano ma non fanno niente (ripremi per riattivarla)" : "Console riattivata";
    }

    /// <summary>Ferma subito l'automix: annulla il passaggio in corso e lo spegne.</summary>
    [RelayCommand]
    private void AutoMixStop()
    {
        if (IsMixing) AbortMix("fermato dal DJ");
        _crossfadeTarget = null;
        AutoMix = false; AutoMixHold = false;
        StatusText = "Auto-mix fermato: da qui in avanti comandi tu";
    }

    /// <summary>Pausa/riprendi l'automix senza perdere le impostazioni.</summary>
    [RelayCommand]
    private void AutoMixTogglePause() => AutoMixHold = !AutoMixHold;

    private void CheckAutoMix()
    {
        if (AutoMixHold) return;
        foreach (var (d, other, dir) in new[] { (DeckA, DeckB, 1.0), (DeckB, DeckA, -1.0) })
        {
            if (!d.IsPlaying || !d.HasTrack) continue;
            if (d.IsFill) continue; // il riempimento lo manda via il DJ col prossimo cantante
            if (_autoMixTriggeredFor == d) continue;
            if (other.IsPlaying) continue;
            if (IsMixing) continue;
            if (Queue.Count == 0 && !(AutoMixEndless && TryFillQueueFromSuggestions(d))) continue;
            PrefetchNextGrid();
            double dur = d.Deck.DurationSec;
            // finestra di innesco: il passaggio più lungo (16 battute) più un margine per pianificare; il punto esatto lo decide PlanMixAsync
            double bpm = d.Track?.Bpm > 0 ? d.Track.Bpm * Math.Max(0.5, d.Deck.Tempo) : 0;
            double lead = bpm > 0 ? 16 * 4 * 60.0 / bpm + 6 : CrossfadeSeconds + 2;
            double mixAt = dur - lead;
            if (AutoMixUseCues && d.Track?.OutroStartSec > 0 && d.Track.OutroStartSec < dur - 0.5)
                mixAt = Math.Min(mixAt, d.Track.OutroStartSec - 4);
            if (d.Deck.PositionSec < mixAt) continue;

            var e = Queue[0];
            if (!LoadToDeck(other, e.Track, e.Singer, e.KeyShift, confirmIfPlaying: false)) { _autoMixTriggeredFor = d; continue; }
            Queue.Remove(e);
            _autoMixTriggeredFor = d;
            _ = MixToAsync(d, other, startNow: false);
        }
    }

    /// <summary>
    /// Coda vuota con automix: sceglie il prossimo brano con continuità. Ordine: generi della serata (se impostati) →
    /// stesso genere del brano in corso → stessa decade → solo BPM/tonalità. Il motivo della scelta è scritto in coda e nella barra di stato.
    /// </summary>
    private bool TryFillQueueFromSuggestions(DeckViewModel playing)
    {
        var current = playing.Track;
        var onDecks = new HashSet<string>();
        if (DeckA.Track != null) onDecks.Add(DeckA.Track.Id);
        if (DeckB.Track != null) onDecks.Add(DeckB.Track.Id);
        var pool = Tracks.Where(t => !t.IsKaraoke && !onDecks.Contains(t.Id) && File.Exists(t.FilePath)).ToList();
        if (pool.Count == 0) return false;

        var setGenres = SetGenreList();
        var fresh = pool.Where(t => !t.PlayedThisSession && !Feedback.IsRejectedNow(t)).ToList();
        // generi della serata: il pool si restringe a quelli (se ce n'è abbastanza)
        if (setGenres.Count > 0)
        {
            var inSet = fresh.Where(t => MatchesSetGenres(t, setGenres)).ToList();
            if (inSet.Count > 0) fresh = inSet;
            else StatusText = "Automix: nessun brano non ancora suonato nei generi della serata — scelgo fuori";
        }

        Track? pick = null; string why = "";
        if (current != null)
        {
            bool curGenre = !string.IsNullOrWhiteSpace(current.Genre);
            var tiers = new List<(Func<Track, bool> ok, string why)>();
            if (setGenres.Count > 0) tiers.Add((t => MatchesSetGenres(t, setGenres), "generi serata"));
            if (curGenre) tiers.Add((t => GenreAffinity(current, t) >= 0.8, $"stesso genere ({current.Genre})"));
            if (current.Year > 0) tiers.Add((t => DecadeAffinity(current, t) >= 1.0, $"stessa decade ({current.Decade})"));
            tiers.Add((_ => true, curGenre ? "solo BPM/tonalità" : "solo BPM/tonalità — il brano in corso non ha genere"));
            foreach (var (ok, w) in tiers)
            {
                var best = fresh.Where(ok)
                    .Select(t => (t, s: SuggestScore(current, t) * (t.Analyzed ? 1.0 : 0.6)))
                    .Where(x => x.s > 0.3)
                    .OrderByDescending(x => x.s).ThenBy(x => x.t.PlayCount)
                    .Take(5).ToList();
                if (best.Count > 0) { var b = best[Random.Shared.Next(Math.Min(3, best.Count))]; pick = b.t; why = $"{w}, match {b.s * 100:0}%"; break; }
            }
            if (pick == null && curGenre)
            {
                pick = fresh.Where(t => GenreAffinity(current, t) >= 0.8).OrderBy(t => t.PlayCount).ThenBy(_ => Random.Shared.Next()).FirstOrDefault();
                if (pick != null) why = $"stesso genere ({current.Genre}), BPM/tonalità non compatibili";
            }
            if (pick == null && current.Year > 0)
            {
                pick = fresh.Where(t => DecadeAffinity(current, t) >= 1.0).OrderBy(t => t.PlayCount).ThenBy(_ => Random.Shared.Next()).FirstOrDefault();
                if (pick != null) why = $"stessa decade ({current.Decade}), BPM/tonalità non compatibili";
            }
        }
        if (pick == null)
        {
            pick = fresh.OrderBy(t => t.PlayCount).ThenBy(t => t.LastPlayedUtc ?? DateTime.MinValue).ThenBy(_ => Random.Shared.Next()).FirstOrDefault();
            if (pick != null) why = current == null ? "primo brano" : "nessun riferimento utile: brano meno suonato";
        }
        if (pick == null) { pick = pool.OrderBy(t => t.LastPlayedUtc ?? DateTime.MinValue).First(); why = "tutti già suonati: riparto dal più vecchio"; }
        Queue.Add(new QueueEntry { Track = pick, Note = "automix · " + why });
        StatusText = $"Automix: aggiunto {pick.Display} ({why})";
        return true;
    }

    /// <summary>Generi della serata (testo libero separato da virgole) → lista normalizzata.</summary>
    private List<string[]> SetGenreList() =>
        (SetGenres ?? "").Split(',', ';').Select(s => SearchUtil.Words(s)).Where(w => w.Length > 0).ToList();

    private static bool MatchesSetGenres(Track t, List<string[]> genres)
    {
        // ogni tag del brano viene confrontato con ogni genere della serata (tutte le parole, o prefisso per i generi a una parola)
        foreach (var tag in t.Genres)
        {
            var w = SearchUtil.Words(tag);
            if (w.Length == 0) continue;
            foreach (var g in genres)
                if (g.All(x => w.Contains(x)) || (g.Length == 1 && w.Any(x => x.StartsWith(g[0])))) return true;
        }
        return false;
    }

    private void OnDeckEnded(DeckViewModel d)
    {
        if (d.Track != null) _endedByItself = d.Track.Id;   // il brano è arrivato in fondo: per le statistiche vale come "funziona"
        if (_autoMixTriggeredFor == d) _autoMixTriggeredFor = null;
        d.Tick();
        // Con automix attivo e coda piena, se per qualche motivo la dissolvenza non è partita
        if (AutoMix && Queue.Count == 0 && AutoMixEndless && !DeckA.IsPlaying && !DeckB.IsPlaying) TryFillQueueFromSuggestions(d);
        if (AutoMix && Queue.Count > 0 && !DeckA.IsPlaying && !DeckB.IsPlaying && !(FillMusicOn && (Queue[0].Track.IsKaraoke || d.IsFill)))
        {
            var other = d == DeckA ? DeckB : DeckA;
            var e = Queue[0];
            if (LoadToDeck(other, e.Track, e.Singer, e.KeyShift, confirmIfPlaying: false))
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
    [RelayCommand] private void ToggleMonitor() => IsMonitorOpen = !IsMonitorOpen;

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
        var list = Library.Db.LoadPlaylists();
        if (list.Count == 0 && File.Exists(AppPaths.PlaylistsFile))
        {
            // prima apertura con il database: importa le playlist dal vecchio json
            list = JsonStore.Load<List<Playlist>>(AppPaths.PlaylistsFile);
            if (list.Count > 0) Library.Db.SaveAllPlaylists(list);
            // il vecchio playlists.json resta al suo posto
        }
        foreach (var p in list) Playlists.Add(p);
    }

    /// <summary>Salva tutte le playlist nel database (poche righe: veloce).</summary>
    public void SavePlaylists()
    {
        try { Library.Db.SaveAllPlaylists(Playlists); } catch (Exception ex) { StatusText = "Playlist non salvate: " + ex.Message; }
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
    private string _analyzeCurrent = "";
    private DateTime _analyzeStartedUtc;
    private int _analyzeDone, _analyzeTotal;
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
            _analyzeCurrent = track.Display; _analyzeStartedUtc = DateTime.UtcNow;
            var (audioPath, _) = LibraryService.PrepareForPlayback(track);
            // un file che non si lascia decodificare (o un disco lentissimo) non deve bloccare la coda: massimo 3 minuti a brano
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            AnalysisResult r;
            try { r = await Task.Run(() => AudioAnalyzer.Analyze(audioPath, timeout.Token), timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                track.Analyzed = true; // saltato: non riprovare all'infinito
                StatusText = $"Analisi saltata (troppo lenta): {track.Display}";
                return;
            }
            track.Bpm = r.Bpm;
            track.Key = r.Key;
            if (!track.CuesManual) { track.IntroEndSec = r.IntroEndSec; track.OutroStartSec = r.OutroStartSec; }
            track.Energy = r.Energy; track.Brightness = r.Brightness;   // carattere del suono, per i suggerimenti
            if (!track.BeatManual && r.BeatOffsetSec >= 0) track.BeatOffsetSec = r.BeatOffsetSec;   // griglia agganciata ai colpi veri
            if (!track.BeatManual && r.Beats is { Length: > 8 })
            {
                track.Beats = r.Beats.Select(b => (float)b).ToArray();                                 // griglia fluida
                var (gbpm, drift) = Audio.BeatTracker.Summary(r.Beats);
                if (gbpm > 0 && Math.Abs(gbpm - track.Bpm) / track.Bpm < 0.06) track.Bpm = Math.Round(gbpm, 1);
                track.BeatDriftPercent = Math.Round(drift, 1);
            }
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

        Library.Save(track);
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
        // anche i brani analizzati prima della 1.6 vanno rifatti: non hanno energia/brillantezza (servono ai suggerimenti)
        var todo = Tracks.Where(t => !t.Analyzed || t.Energy <= 0).ToList();
        if (todo.Count == 0) { StatusText = "Tutti i brani sono già analizzati"; return; }
        _analyzeCts = new CancellationTokenSource();
        IsAnalyzing = true;
        int done = 0;
        try
        {
            foreach (var t in todo)
            {
                _analyzeCts.Token.ThrowIfCancellationRequested();
                _analyzeDone = ++done; _analyzeTotal = todo.Count;
                AnalyzeStatus = $"Analisi {done}/{todo.Count}: {t.Display}";
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

    // ---------------------------------------------------------------- stile di passaggio (come un DJ dal vivo)

    /// <summary>"fade" dissolvenza semplice · "glide" cross-BPM · "bass" cross-BPM + scambio bassi · "echo" cross-BPM + bassi + filtro/echo-out.</summary>
    [ObservableProperty] private string _transitionStyle = "bass";
    partial void OnTransitionStyleChanged(string value) => Settings.TransitionStyle = value;

    private sealed class Transition
    {
        public DeckViewModel Outgoing = null!, Incoming = null!;
        public double From, To;                // valori del crossfader
        public double OutTempo0, OutTempo1;    // fattore tempo del deck in uscita: iniziale → finale (BPM del brano entrante)
        public double InTempo0, InTempo1;      // deck entrante: agganciato al brano in uscita → tempo naturale
        public bool Glide, BassSwap, EchoOut;
        public bool EchoFired;
    }
    private Transition? _transition;

    /// <summary>Prepara il passaggio "da DJ": il tempo scivola dai BPM del brano in uscita a quelli del brano entrante mentre il crossfader si muove.</summary>
    private void BeginTransition(DeckViewModel outgoing, DeckViewModel incoming, double target)
    {
        _transition = null;
        if (TransitionStyle == "fade") return;
        bool karaoke = outgoing.IsKaraoke || incoming.IsKaraoke;
        var t = new Transition
        {
            Outgoing = outgoing, Incoming = incoming, From = Crossfader, To = target,
            BassSwap = TransitionStyle is "bass" or "echo" && !karaoke,
            EchoOut = TransitionStyle == "echo" && !karaoke,
        };
        // cross-BPM: solo se entrambi hanno BPM, niente blocco BPM e niente karaoke
        double inBpm = incoming.Track?.Bpm ?? 0, outBpm = EffectiveBpm(outgoing);
        if (!BpmLock && !karaoke && inBpm > 0 && outBpm > 0 && BpmMatch)
        {
            t.InTempo0 = incoming.Deck.Tempo;             // già agganciato ai BPM in uscita da MatchIncomingTempo
            t.InTempo1 = 1.0;                              // arriva al suo tempo naturale
            double inNative = inBpm;                       // BPM naturali del brano entrante (×1/×2/×½ più vicini a quelli in uscita)
            foreach (var mult in new[] { 2.0, 0.5 }) if (Math.Abs(inBpm * mult - outBpm) < Math.Abs(inNative - outBpm)) inNative = inBpm * mult;
            double outTempo1 = outgoing.Deck.Tempo * inNative / outBpm;
            t.OutTempo0 = outgoing.Deck.Tempo;
            t.OutTempo1 = Math.Clamp(outTempo1, 0.75, 1.25);
            t.Glide = Math.Abs(t.InTempo1 - t.InTempo0) > 0.002 || Math.Abs(t.OutTempo1 - t.OutTempo0) > 0.002;
        }
        if (t.BassSwap) incoming.EqLow = -14;              // il brano entra senza bassi, poi li prende
        _transition = t;
    }

    /// <summary>Avanzamento del passaggio (0..1) chiamato dal timer a ogni movimento del crossfader.</summary>
    private void StepTransition(double crossfader)
    {
        var t = _transition;
        if (t == null) return;
        double p = Math.Clamp((crossfader - t.From) / (t.To - t.From), 0, 1);
        double s = p * p * (3 - 2 * p); // smoothstep
        if (t.Glide)
        {
            t.Incoming.Deck.Tempo = t.InTempo0 + (t.InTempo1 - t.InTempo0) * s;
            t.Outgoing.Deck.Tempo = t.OutTempo0 + (t.OutTempo1 - t.OutTempo0) * s;
        }
        if (t.BassSwap)
        {
            // entrante: bassi da −14 a 0 nella prima metà; uscente: bassi da 0 a −14 nella seconda metà
            t.Incoming.EqLow = -14 * (1 - Math.Clamp(p / 0.55, 0, 1));
            t.Outgoing.EqLow = -14 * Math.Clamp((p - 0.45) / 0.55, 0, 1);
        }
        if (t.EchoOut)
        {
            t.Outgoing.FilterValue = 0.85 * Math.Clamp((p - 0.35) / 0.65, 0, 1); // high-pass progressivo
            if (!t.EchoFired && p >= 0.8) { t.EchoFired = true; t.Outgoing.EchoOutCommand.Execute(null); }
        }
    }

    private void EndTransition()
    {
        var t = _transition;
        _transition = null;
        if (t == null) return;
        if (t.Glide)
        {
            // il brano entrante arriva al suo tempo: allineo l'indicatore del deck
            t.Incoming.TempoPercent = (int)Math.Round((t.InTempo1 - 1) * 100);
            t.Incoming.Deck.Tempo = t.InTempo1;
            t.Outgoing.TempoPercent = 0;
        }
        t.Outgoing.EqLow = 0; t.Outgoing.FilterValue = 0; t.Incoming.EqLow = 0;
        if (t.Outgoing.EchoOutRunning) t.Outgoing.CancelEchoOutCommand.Execute(null);
        t.Outgoing.Deck.Fx.Reset();
    }

    // ---------------------------------------------------------------- storico riproduzioni e suggerimenti

    public ObservableCollection<Track> Suggestions { get; } = new();
    [ObservableProperty] private string _suggestionsLabel = "";
    private readonly HashSet<string> _playedThisSession = new();

    private Track? _previousPlayed;
    private string? _endedByItself;

    private void OnTrackPlayed(DeckViewModel deck, Track track)
    {
        // statistiche d'uso (solo se l'utente ha acconsentito: vedi UsageStats)
        UsageStats.Record(Settings, _previousPlayed, track, "played", _previousPlayed != null && _endedByItself == _previousPlayed.Id);
        _previousPlayed = track;
        track.PlayCount++;
        track.LastPlayedUtc = DateTime.UtcNow;
        track.PlayedThisSession = true;
        _playedThisSession.Add(track.Id);
        PlayLog.Record(track, deck.Name);
        RememberSinger(deck, track);
        Library.Save(track);
        LibraryView.Refresh();
        UpdateSuggestions();
    }

    /// <summary>Prossimi brani consigliati: compatibili col brano in riproduzione, non ancora suonati stasera, non in coda.</summary>
    public void UpdateSuggestions() => UpdateSuggestionsFor(CompatReference());

    /// <summary>Suggeriti dopo un brano preciso (usato anche da --suggesttest).</summary>
    public void UpdateSuggestionsFor(Track? r)
    {
        Suggestions.Clear();
        if (r == null) { SuggestionsLabel = ""; return; }
        var queued = new HashSet<string>(Queue.Select(q => q.Track.Id));
        var all = Tracks
            .Where(t => t.Id != r.Id && !queued.Contains(t.Id) && !t.PlayedThisSession && !t.IsKaraoke)
            .Select(t => { var (s, why, off) = SuggestScoreWhy(r, t); return (t, s: s * (t.Analyzed ? 1.0 : 0.6), why, off); })
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s)
            .ThenBy(x => x.t.PlayCount)
            .ToList();

        // prima scelta: brani dello stesso mondo musicale e con un punteggio decente
        var good = all.Where(x => !x.off && x.s > 0.45).Take(6).ToList();
        // se non ce n'è nessuno mostriamo comunque i "meno peggio", ma segnati con ⚠ e col motivo
        var fallback = good.Count > 0 ? new List<(Track t, double s, string why, bool off)>() : all.Take(3).ToList();

        foreach (var (t, s, why, off) in good.Concat(fallback))
        {
            bool weak = good.Count == 0;
            t.MatchLabel = (weak ? "⚠ " : "") + (s * 100).ToString("0") + "%";
            t.MatchWhy = (weak ? "ripiego — " : "") + why;
            Suggestions.Add(t);
        }

        var health = MusicTaste.LibraryGenreHealth(Tracks);
        var genreInfo = MusicTaste.UsefulGenres(r).Count > 0 ? " · " + string.Join(", ", MusicTaste.UsefulGenres(r)) : (health.Length > 0 ? " · " + health : "");
        SuggestionsLabel = good.Count > 0 ? $"dopo: {r.Display}" + genreInfo
            : fallback.Count > 0 ? $"⚠ niente di davvero adatto dopo {r.Display}: ecco i meno peggio" + genreInfo
            : $"Nessun brano mixabile dopo {r.Display}" + genreInfo;
    }

    [RelayCommand] private void RefreshSuggestions() => UpdateSuggestions();

    /// <summary>👎 "non c'entra": il brano sparisce dai suggeriti di stasera e pesa meno in futuro (soprattutto dopo questo brano).</summary>
    [RelayCommand]
    private void SuggestionReject(Track? t)
    {
        if (t == null) return;
        var r = CompatReference();
        if (r != null) UsageStats.Record(Settings, r, t, "rejected");
        Feedback.Rate(r, t, -1);
        Suggestions.Remove(t);
        StatusText = $"Segnato: \"{t.Display}\" non c'entra" + (r != null ? $" dopo \"{r.Display}\"" : "") + " — il suggeritore ne terrà conto";
        UpdateSuggestions();
    }

    /// <summary>👍 "perfetto": rinforza questo abbinamento.</summary>
    [RelayCommand]
    private void SuggestionApprove(Track? t)
    {
        if (t == null) return;
        var r = CompatReference();
        Feedback.Rate(r, t, +1);
        StatusText = $"Segnato: \"{t.Display}\" va bene" + (r != null ? $" dopo \"{r.Display}\"" : "");
        UpdateSuggestions();
    }

    /// <summary>Brano che stava suonando prima dell'ultimo passaggio (per giudicare l'abbinamento a posteriori).</summary>
    private Track? _lastOutgoingTrack;

    /// <summary>👎 sul deck: "questo brano non c'entrava dopo il precedente" (utile quando l'ha scelto l'automix).</summary>
    [RelayCommand]
    private void DeckReject(DeckViewModel? d)
    {
        if (d?.Track == null) return;
        var prev = _lastOutgoingTrack != null && _lastOutgoingTrack != d.Track ? _lastOutgoingTrack : null;
        Feedback.Rate(prev, d.Track, -1);
        StatusText = $"Segnato: \"{d.Track.Display}\" non c'entrava" + (prev != null ? $" dopo \"{prev.Display}\"" : "") + " — il suggeritore ne terrà conto";
        UpdateSuggestions();
    }

    [RelayCommand]
    private void ExternalSuggestionReject(ExternalSuggestion? s)
    {
        if (s == null) return;
        Feedback.RejectExternal(s.Display);
        ExternalSuggestions.Remove(s);
    }

    /// <summary>"" libero · "decade" stessa decade · "genre" stesso genere. Vale per suggeriti e filtro Compatibili.</summary>
    [ObservableProperty] private string _suggestBy = "";
    partial void OnSuggestByChanged(string value)
    {
        Settings.SuggestBy = value;
        UpdateSuggestions();
        if (LibraryFilter == "compat") { ComputeCompatibility(); ApplyLibrarySort(); LibraryView.Refresh(); }
    }

    /// <summary>Pollici su/giù del DJ sui suggerimenti (memoria del suggeritore).</summary>
    public SuggestionFeedback Feedback { get; } = new();

    /// <summary>Compatibilità BPM/tonalità pesata con la coerenza decade/genere richiesta e con i giudizi del DJ.</summary>
    /// <summary>
    /// Punteggio secco (automix, filtro "compatibili"). I brani fuori stile valgono la metà: l'automix li usa solo
    /// se non ha alternative, perché in serata il silenzio è peggio di un accostamento discutibile.
    /// </summary>
    private double SuggestScore(Track r, Track t)
    {
        var (score, _, off) = SuggestScoreWhy(r, t);
        return off ? score * 0.5 : score;
    }

    /// <summary>
    /// Punteggio e motivo. Parte dalla compatibilità BPM/tonalità (mixabilità) e la pesa con l'affinità musicale
    /// (artista, genere vero, epoca, carattere del suono): senza questo pezzo uscivano accostamenti senza senso,
    /// tipo Battisti dopo gli AC/DC, perché i file scaricati hanno tutti genere "Music".
    /// </summary>
    private (double Score, string Why, bool OffStyle) SuggestScoreWhy(Track r, Track t)
    {
        if (Feedback.IsRejectedNow(t)) return (0, "", false);
        var (s, why, off) = SetFlow.Rank(t, FlowContextNow(r));
        if (s <= 0) return (0, why, off);
        s *= Feedback.Factor(r, t);
        // il filtro scelto dal DJ stringe ulteriormente
        if (SuggestBy == "decade") s *= DecadeAffinity(r, t);
        else if (SuggestBy == "genre") s *= GenreAffinity(r, t);
        return (s, why, off);
    }

    /// <summary>Come vogliamo che vada la serata adesso: il brano di riferimento, l'intenzione del DJ e cosa è già suonato.</summary>
    private FlowContext FlowContextNow(Track r) =>
        new(r, FlowIntentNow, PlayLogTonight(), RotationOn || Queue.Any(q => !string.IsNullOrWhiteSpace(q.Singer)));

    /// <summary>Brani già suonati stasera, in ordine.</summary>
    private List<Track> PlayLogTonight() =>
        Tracks.Where(t => t.PlayedThisSession && t.LastPlayedUtc != null).OrderBy(t => t.LastPlayedUtc).ToList();

    /// <summary>Intenzione per il prossimo brano: tieni l'energia, alzala, calma la sala (barra dei suggeriti).</summary>
    [ObservableProperty] private FlowIntent _flowIntentNow = FlowIntent.Auto;
    partial void OnFlowIntentNowChanged(FlowIntent value) => UpdateSuggestions();
    public string FlowIntentLabel => FlowIntentNow switch
    {
        FlowIntent.Up => "▲ alza",
        FlowIntent.Down => "▼ calma",
        FlowIntent.Keep => "= tieni",
        _ => "auto",
    };

    [RelayCommand]
    private void CycleFlowIntent()
    {
        FlowIntentNow = FlowIntentNow switch
        {
            FlowIntent.Auto => FlowIntent.Up,
            FlowIntent.Up => FlowIntent.Keep,
            FlowIntent.Keep => FlowIntent.Down,
            _ => FlowIntent.Auto,
        };
        OnPropertyChanged(nameof(FlowIntentLabel));
        StatusText = "Suggerimenti: " + FlowIntentLabel;
    }

    private static double DecadeAffinity(Track r, Track t)
    {
        if (r.Year <= 0 || t.Year <= 0) return 0.5;          // anno ignoto: non escludiamo, ma scende
        int d = Math.Abs(r.Year / 10 - t.Year / 10);
        return d switch { 0 => 1.0, 1 => 0.65, _ => 0.2 };
    }

    /// <summary>Affinità di genere fra due brani (tag multipli): 1 = un tag in comune, 0.8 = parole in comune ("Pop Rock"/"Rock"), 0.5 = ignoto, 0.2 = diversi.</summary>
    private static double GenreAffinity(Track r, Track t)
    {
        var ga = MusicTaste.UsefulGenres(r); var gb = MusicTaste.UsefulGenres(t);
        if (ga.Count == 0 || gb.Count == 0) return 0.5;
        if (ga.Any(x => gb.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)))) return 1.0;
        var a = SearchUtil.Words(r.Genre); var b = SearchUtil.Words(t.Genre);
        return a.Intersect(b).Any() ? 0.8 : 0.2;
    }

    [RelayCommand]
    private void SuggestionToQueue(Track? t)
    {
        if (t == null) return;
        if (CompatReference() is { } refT) UsageStats.Record(Settings, refT, t, "accepted");
        Queue.Add(new QueueEntry { Track = t });
        Suggestions.Remove(t);
        StatusText = "In coda: " + t.Display;
    }

    [RelayCommand]
    private void SuggestionToFreeDeck(Track? t)
    {
        if (t == null) return;
        if (CompatReference() is { } refT) UsageStats.Record(Settings, refT, t, "accepted");
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

    // ---------------------------------------------------------------- raccolte rapide per genere

    public sealed partial class GenreChip : ObservableObject
    {
        public string Name { get; init; } = "";
        public int Count { get; set; }
        [ObservableProperty] private bool _isSelected;
    }

    /// <summary>Chip dei generi più usati in libreria (max 30), cliccabili per filtrare.</summary>
    public ObservableCollection<GenreChip> GenreChips { get; } = new();
    private readonly HashSet<string> _genreFilter = new(StringComparer.OrdinalIgnoreCase);

    private void RebuildGenreChips()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tracks)
            foreach (var g in t.Genres) counts[g] = counts.GetValueOrDefault(g) + 1;
        var top = counts.Where(kv => IsSaneGenre(kv.Key) && kv.Value >= 2).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase).Take(40).ToList();
        GenreChips.Clear();
        foreach (var kv in top) GenreChips.Add(new GenreChip { Name = kv.Key, Count = kv.Value, IsSelected = _genreFilter.Contains(kv.Key) });
        // filtri su generi spariti: via
        foreach (var g in _genreFilter.Where(g => !counts.ContainsKey(g)).ToList()) _genreFilter.Remove(g);
        OnPropertyChanged(nameof(GenreChips));
    }

    /// <summary>Click su una raccolta: aggiunge/toglie il genere dal filtro (più generi = unione).</summary>
    [RelayCommand]
    private void ToggleGenreFilter(GenreChip? chip)
    {
        if (chip == null) return;
        if (chip.IsSelected) _genreFilter.Add(chip.Name); else _genreFilter.Remove(chip.Name);
        LibraryView.Refresh();
        StatusText = _genreFilter.Count == 0 ? "Filtro generi tolto" : "Libreria: " + string.Join(" + ", _genreFilter);
    }

    [RelayCommand]
    private void ClearGenreFilter()
    {
        _genreFilter.Clear();
        foreach (var c in GenreChips) c.IsSelected = false;
        LibraryView.Refresh();
    }

    // ---------------------------------------------------------------- genere / anno a mano

    /// <summary>Voce del menu Genere: nome e se il brano selezionato ce l'ha già.</summary>
    public sealed class GenreOption
    {
        public string Name { get; init; } = "";
        public bool IsChecked { get; init; }
    }

    /// <summary>Generi proponibili nel menu (catalogo AI + tag già presenti in libreria), con la spunta per il brano selezionato.</summary>
    public List<GenreOption> GenreOptions
    {
        get
        {
            var t = SelectedTrack;
            // tag della libreria: solo quelli "veri" (almeno 3 brani, niente codici numerici o sigle strane); il catalogo AI sempre
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in Tracks) foreach (var g in x.Genres) counts[g] = counts.GetValueOrDefault(g) + 1;
            var fromLibrary = counts.Where(kv => kv.Value >= 3 && IsSaneGenre(kv.Key)).Select(kv => kv.Key);
            var list = GenreClassifier.Genres.Concat(Settings.CustomGenres).Concat(fromLibrary);
            if (t != null) list = list.Concat(t.Genres); // i tag del brano selezionato compaiono sempre (per poterli togliere)
            return list
                .Select(g => g.Trim()).Where(g => g.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => new GenreOption { Name = g, IsChecked = t?.HasGenre(g) == true })
                .ToList();
        }
    }

    /// <summary>Un genere "vero": almeno una lettera e non un codice (es. "168", "AIL", "alt z").</summary>
    public static bool IsSaneGenre(string g)
    {
        g = g.Trim();
        if (g.Length < 3 || !g.Any(char.IsLetter)) return false;
        if (g.All(c => char.IsUpper(c) || char.IsDigit(c)) && g.Length <= 4) return false; // sigle
        return true;
    }

    /// <summary>Raccolte per genere: riga singola oppure tutte (pulsante "altri…").</summary>
    [ObservableProperty] private bool _genreChipsExpanded;

    /// <summary>Aggiunge/toglie un tag di genere al brano selezionato (un brano può averne più d'uno). Scrive anche il tag nel file.</summary>
    [RelayCommand]
    private void ToggleGenre(string? genre)
    {
        var t = SelectedTrack;
        if (t == null || string.IsNullOrWhiteSpace(genre)) return;
        if (!t.HasGenre(genre) && t.Genres.Count() >= DeckViewModel.MaxGenreTags) { StatusText = $"Massimo {DeckViewModel.MaxGenreTags} generi per brano"; return; }
        t.ToggleGenre(genre);
        AfterGenreChange(t);
    }

    /// <summary>Scrive i generi a mano (separati da ; o virgola), vuoto = nessuno.</summary>
    [RelayCommand]
    private void SetGenre()
    {
        var t = SelectedTrack;
        if (t == null) return;
        var s = Views.InputDialog.Show("Generi", $"Generi per \"{t.Display}\" (più tag separati da ; o virgola):", t.Genre);
        if (s == null) return;
        t.Genre = string.Join("; ", Track.SplitGenres(s).Take(DeckViewModel.MaxGenreTags));
        AfterGenreChange(t);
    }

    private void AfterGenreChange(Track t)
    {
        // un genere scritto a mano resta disponibile per tutti gli altri brani
        foreach (var g in t.Genres)
            if (!GenreClassifier.Genres.Contains(g, StringComparer.OrdinalIgnoreCase) && !Settings.CustomGenres.Contains(g, StringComparer.OrdinalIgnoreCase))
                Settings.CustomGenres.Add(g);
        t.InvalidateSearchCache();
        foreach (var d in new[] { DeckA, DeckB }) if (d.Track == t) d.RefreshGenreTags();
        WriteGenreYearTag(t);
        Library.Save(t);
        LibraryView.Refresh();
        UpdateSuggestions();
        StatusText = t.Genre.Length == 0 ? $"Nessun genere: {t.Display}" : $"Generi \"{t.Genre}\": {t.Display}";
        OnPropertyChanged(nameof(GenreOptions));
        OnPropertyChanged(nameof(SelectedTrackGenres));
        RebuildGenreChips();
    }

    /// <summary>Imposta l'anno del brano selezionato (chiede).</summary>
    [RelayCommand]
    private void SetYear()
    {
        var t = SelectedTrack;
        if (t == null) return;
        var s = Views.InputDialog.Show("Anno", $"Anno di uscita per \"{t.Display}\" (vuoto = sconosciuto):", t.Year > 0 ? t.Year.ToString() : "");
        if (s == null) return;
        t.Year = int.TryParse(s.Trim(), out var y) && y is > 1900 and < 2100 ? y : 0;
        t.InvalidateSearchCache();
        WriteGenreYearTag(t);
        Library.Save(t);
        LibraryView.Refresh();
        StatusText = t.Year > 0 ? $"Anno {t.Year}: {t.Display}" : $"Anno tolto: {t.Display}";
    }

    private static void WriteGenreYearTag(Track t)
    {
        if (t.Kind is TrackKind.CdgZip or TrackKind.Midi) return;
        try
        {
            using var tf = TagLib.File.Create(t.FilePath);
            tf.Tag.Genres = t.Genres.ToArray();
            if (t.Year > 0) tf.Tag.Year = (uint)t.Year;
            tf.Save();
        }
        catch { /* file in sola lettura o formato senza tag: resta solo in libreria */ }
    }

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
            var (inst, voc, dir) = await Stems.SeparateAsync(track.Id, audioPath, CancellationToken.None);
            track.InstrumentalPath = inst;
            track.VocalsPath = voc;
            track.StemsDir = dir;
            Library.Save(track);
            StatusText = $"Stem pronti (voce, batteria, basso, altro): {track.Display}";
            foreach (var d in new[] { DeckA, DeckB }) if (d.Track == track) { d.HasInstrumental = true; d.HasStems = track.HasStems; }
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
    /// <summary>C'è una versione nuova ma è uscita dopo la scadenza degli aggiornamenti della licenza.</summary>
    [ObservableProperty] private bool _updateBlocked;
    [ObservableProperty] private bool _updating;
    public string AppVersion => "v" + Updater.CurrentVersion;

    public async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var v = await Updater.CheckAsync();
            if (v != null && !await CanInstallVersionAsync(v))
            {
                UpdateBlocked = true; UpdateAvailable = false;
                UpdateStatus = $"Versione {v} disponibile: è uscita dopo la scadenza dei tuoi aggiornamenti ({UpdatesUntil:dd/MM/yyyy}). Rinnova (10 €/anno) per riceverla.";
            }
            else if (v != null) { UpdateAvailable = true; UpdateBlocked = false; UpdateStatus = $"Aggiornamento {v} disponibile"; }
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

    // ---------------------------------------------------------------- Animazione: festeggiato, messaggi, Suno

    public Celebration Celebration { get; private set; } = new();
    public ObservableCollection<Track> SunoTracks { get; } = new();
    private SunoWatcher? _suno;
    [ObservableProperty] private bool _assignDedicationToNextSuno = true;
    [ObservableProperty] private bool _lyricsBusy;
    [ObservableProperty] private string _lyricsStatus = "";
    [ObservableProperty] private string _dedicationTitle = "";
    [ObservableProperty] private string _dedicationText = "";
    public string SunoFolder => SunoWatcher.Folder;
    public bool HasAnthropicKey => !string.IsNullOrEmpty(Secret.Unprotect(Settings.AnthropicApiKeyProtected));

    private void StartAnimation()
    {
        Celebration = JsonStore.Load<Celebration>(AppPaths.CelebrationFile);
        Celebration.Messages ??= new();
        foreach (var t in Tracks.Where(t => t.IsSuno)) SunoTracks.Add(t);
        try
        {
            _suno = new SunoWatcher();
            _suno.FileReady += OnSunoFile;
        }
        catch (Exception ex) { StatusText = "Cartella Suno non monitorabile: " + ex.Message; }
    }

    public void SaveCelebration() { try { JsonStore.Save(AppPaths.CelebrationFile, Celebration); } catch { } }

    public void SetAnthropicApiKey(string? key)
    {
        Settings.AnthropicApiKeyProtected = Secret.Protect(key?.Trim());
        OnPropertyChanged(nameof(HasAnthropicKey));
        SaveSettings();
    }

    private void OnSunoFile(string path)
    {
        var track = Library.AddFile(path);
        if (track == null) return;
        track.IsSuno = true;
        TitleCleaner.Apply(track, writeTags: false);
        if (string.IsNullOrWhiteSpace(track.Artist)) track.Artist = "Suno";
        if (AssignDedicationToNextSuno && !string.IsNullOrWhiteSpace(Celebration.Name))
        {
            track.Dedication = Celebration.DedicationText;
            track.DedicationTitle = string.IsNullOrWhiteSpace(Celebration.SongTitle) ? $"Per {Celebration.Name}" : Celebration.SongTitle;
            if (!string.IsNullOrWhiteSpace(Celebration.SongTitle)) track.Title = Celebration.SongTitle;
        }
        Library.Save(track);
        var old = Tracks.FirstOrDefault(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (old != null) Tracks.Remove(old);
        Tracks.Add(track);
        SunoTracks.Insert(0, track);
        LibraryCount = Tracks.Count;
        AnalyzeInBackground(track);
        StatusText = $"Importato da Suno: {track.Display}" + (track.Dedication != null ? " (con dedica)" : "");
    }

    [RelayCommand]
    private void AddGuestMessage()
    {
        Celebration.Messages.Add(new GuestMessage());
        SaveCelebration();
    }

    [RelayCommand]
    private void RemoveGuestMessage(GuestMessage? m)
    {
        if (m != null) Celebration.Messages.Remove(m);
        SaveCelebration();
    }

    [RelayCommand]
    private async Task GenerateLyricsAsync()
    {
        if (LyricsBusy) return;
        var key = Secret.Unprotect(Settings.AnthropicApiKeyProtected);
        if (string.IsNullOrWhiteSpace(Celebration.Name)) { LyricsStatus = "Inserisci il nome del festeggiato"; return; }
        LyricsBusy = true;
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                LyricsStatus = "Nessuna chiave API: uso i messaggi così come sono (Impostazioni → chiave Anthropic per il testo AI)";
                var (t0, l0) = LyricsService.BuildFromMessages(Celebration);
                Celebration.SongTitle = t0; Celebration.Lyrics = l0;
            }
            else
            {
                LyricsStatus = "Scrivo il testo con Claude…";
                var (t, l) = await LyricsService.GenerateAsync(Celebration, key, CancellationToken.None);
                Celebration.SongTitle = t; Celebration.Lyrics = l;
                LyricsStatus = "Testo pronto: rileggilo, modificalo se vuoi, poi \"Copia e apri Suno\"";
            }
            SaveCelebration();
        }
        catch (Exception ex) { LyricsStatus = "Errore: " + ex.Message; }
        finally { LyricsBusy = false; }
    }

    [RelayCommand]
    private void UseMessagesAsLyrics()
    {
        var (t, l) = LyricsService.BuildFromMessages(Celebration);
        Celebration.SongTitle = t; Celebration.Lyrics = l;
        LyricsStatus = "Testo montato dai messaggi (senza AI)";
        SaveCelebration();
    }

    /// <summary>Copia il testo negli appunti e apre Suno (modalità Custom: incolla testo, stile e titolo).</summary>
    [RelayCommand]
    private void CopyAndOpenSuno()
    {
        if (string.IsNullOrWhiteSpace(Celebration.Lyrics)) { LyricsStatus = "Prima genera il testo"; return; }
        try { Clipboard.SetText(Celebration.Lyrics.Trim()); } catch { }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SunoWatcher.SunoCreateUrl) { UseShellExecute = true }); } catch { }
        SaveCelebration();
        LyricsStatus = $"Testo copiato. Su Suno: Custom → incolla il testo, stile \"{Celebration.Style}\", titolo \"{Celebration.SongTitle}\". Scarica l'MP3 in {SunoFolder}";
    }

    [RelayCommand]
    private void CopyStyle()
    {
        try { Clipboard.SetText(Celebration.Style); LyricsStatus = "Stile copiato negli appunti"; } catch { }
    }

    [RelayCommand]
    private void OpenSunoFolder()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(SunoFolder) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void AssignDedication(Track? t)
    {
        if (t == null) return;
        t.Dedication = Celebration.DedicationText;
        t.DedicationTitle = string.IsNullOrWhiteSpace(Celebration.SongTitle) ? $"Per {Celebration.Name}" : Celebration.SongTitle;
        Library.Save(t);
        UpdateProjectorState();
        StatusText = "Dedica assegnata a " + t.Display;
    }

    [RelayCommand]
    private void ClearDedication(Track? t)
    {
        if (t == null) return;
        t.Dedication = null; t.DedicationTitle = null;
        Library.Save(t);
        UpdateProjectorState();
    }

    [RelayCommand]
    private void SunoTrackToQueue(Track? t)
    {
        if (t == null) return;
        Queue.Add(new QueueEntry { Track = t, Singer = string.IsNullOrWhiteSpace(Celebration.Name) ? "" : "🎉 " + Celebration.Name });
        StatusText = "In coda: " + t.Display;
    }

    /// <summary>Dedica da mostrare: quella del brano che si sente di più (o dell'unico in riproduzione).</summary>
    private void UpdateDedication()
    {
        DeckViewModel? d = null;
        if (DeckA.IsPlaying && DeckB.IsPlaying) d = Crossfader <= 0 ? DeckA : DeckB;
        else if (DeckA.IsPlaying) d = DeckA;
        else if (DeckB.IsPlaying) d = DeckB;
        var t = d?.Track;
        var text = t?.Dedication ?? "";
        var title = t?.DedicationTitle ?? "";
        if (text != DedicationText) DedicationText = text;
        if (title != DedicationTitle) DedicationTitle = title;
    }

    // ---------------------------------------------------------------- licenza (perpetua per macchina + aggiornamenti annuali per account)

    /// <summary>Servizio cloud Mixfonia (Vercel): cassa Stripe → licenza istantanea, scaletta remota.</summary>
    public const string CloudBaseUrl = "https://voxa-cloud.vercel.app";
    public const int TrialDays = 30;
    public static string PurchaseUrl => $"{CloudBaseUrl}/dona?m={LicenseService.MachineId}";
    public string RenewUrl => License != null && !License.IsLegacy ? $"{PurchaseUrl}&email={Uri.EscapeDataString(License.Account)}" : PurchaseUrl;
    private static readonly System.Net.Http.HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record CloudLicense(string? Key, string? Token, bool Revoked);

    /// <summary>Chiede al cloud chiave e token aggiornamenti di questa macchina (dopo un acquisto, un rinnovo o una reinstallazione).</summary>
    public async Task<CloudLicense?> FetchLicenseFromCloudAsync()
    {
        try
        {
            using var r = await CloudHttp.GetAsync($"{CloudBaseUrl}/api/license?m={LicenseService.MachineId}");
            var body = await r.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!r.IsSuccessStatusCode)
                return root.TryGetProperty("revoked", out var rv) && rv.ValueKind == System.Text.Json.JsonValueKind.True ? new CloudLicense(null, null, true) : null;
            return new CloudLicense(
                root.TryGetProperty("key", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null,
                root.TryGetProperty("token", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null, false);
        }
        catch { return null; }
    }

    /// <summary>Dopo l'apertura della cassa: attende chiave/token e attiva da solo (max 15 minuti). Ritorna true se qualcosa è cambiato.</summary>
    public async Task<bool> WaitForCloudLicenseAsync(CancellationToken ct)
    {
        var before = (Settings.LicenseCode, Settings.UpdatesToken);
        for (int i = 0; i < 180 && !ct.IsCancellationRequested; i++)
        {
            var c = await FetchLicenseFromCloudAsync();
            if (c != null && ApplyCloudLicense(c) && (Settings.LicenseCode, Settings.UpdatesToken) != before) return true;
            try { await Task.Delay(5000, ct); } catch { break; }
        }
        return false;
    }

    public LicenseService.LicenseInfo? License { get; private set; }
    public LicenseService.UpdatesToken? Token { get; private set; }
    public bool IsLicensed => License != null;
    /// <summary>Ultimo giorno in cui le release pubblicate si possono installare (licenza o token dell'account, il più tardi).</summary>
    public DateTime? UpdatesUntil
    {
        get
        {
            if (License == null) return null;
            var u = License.UpdatesUntil;
            if (Token != null && !License.IsLegacy && Token.Account == License.Account && Token.Until > u) u = Token.Until;
            return u;
        }
    }
    public bool UpdatesActive => UpdatesUntil is { } u && u.Date >= DateTime.UtcNow.Date;
    public int TrialDaysLeft => Settings.TrialStart is { } s ? Math.Max(0, TrialDays - (int)(DateTime.UtcNow - s).TotalDays) : TrialDays;
    public bool IsTrial => !IsLicensed && TrialDaysLeft > 0;
    /// <summary>Senza licenza e prova finita: l'app suona ma proiettore con scritta, niente cloud/AI.</summary>
    public bool IsDemo => !IsLicensed && TrialDaysLeft <= 0;
    public string SupportLabel => IsLicensed ? $"❤ {License!.Name}" + (UpdatesActive ? "" : " · aggiornamenti scaduti") : IsTrial ? $"Prova: {TrialDaysLeft} giorni" : "DEMO · acquista la licenza (20 €)";

    private void LoadLicense()
    {
        if (Settings.TrialStart == null) { Settings.TrialStart = DateTime.UtcNow; SaveSettings(); }
        License = LicenseService.Verify(Settings.LicenseCode);
        Token = LicenseService.VerifyToken(Settings.UpdatesToken);
        NotifyLicense();
        // acquisto fatto da un altro dispositivo, rinnovo dell'account o app reinstallata: recupero silenzioso
        _ = Task.Run(async () => { var c = await FetchLicenseFromCloudAsync(); if (c != null) Application.Current?.Dispatcher.BeginInvoke(() => ApplyCloudLicense(c)); });
    }

    private void NotifyLicense()
    {
        foreach (var p in new[] { nameof(IsLicensed), nameof(SupportLabel), nameof(IsTrial), nameof(IsDemo), nameof(TrialDaysLeft), nameof(UpdatesUntil), nameof(UpdatesActive), nameof(RenewUrl) })
            OnPropertyChanged(p);
    }

    /// <summary>Applica quanto arriva dal cloud: chiave nuova/diversa, token di rinnovo. Ritorna true se valido.</summary>
    public bool ApplyCloudLicense(CloudLicense c)
    {
        bool ok = false;
        if (c.Key != null && c.Key != Settings.LicenseCode) ok |= ActivateLicense(c.Key);
        else if (c.Key != null) ok = true;
        if (c.Token != null && c.Token != Settings.UpdatesToken) ok |= ActivateLicense(c.Token);
        return ok;
    }

    /// <summary>Attiva una chiave (per questa macchina) o un token aggiornamenti (dell'account della chiave).</summary>
    public bool ActivateLicense(string? code)
    {
        var tok = LicenseService.VerifyToken(code);
        if (tok != null)
        {
            if (License == null || License.IsLegacy || tok.Account != License.Account) { StatusText = "Il token aggiornamenti è di un altro account."; return false; }
            Settings.UpdatesToken = code!.Trim(); Token = tok; SaveSettings(); NotifyLicense();
            StatusText = $"Aggiornamenti attivi fino al {UpdatesUntil:dd/MM/yyyy}.";
            return true;
        }
        var info = LicenseService.Verify(code);
        if (info == null) return false;
        Settings.LicenseCode = code!.Trim();
        License = info;
        if (Token != null && Token.Account != info.Account) { Token = null; Settings.UpdatesToken = null; }
        SaveSettings();
        NotifyLicense();
        StatusText = $"Grazie {info.Name}! Licenza attiva.";
        return true;
    }

    /// <summary>Promemoria all'avvio: in demo ogni giorno, in prova solo negli ultimi 7 giorni (una volta al giorno).</summary>
    public bool ShouldShowSupportReminder()
    {
        if (IsLicensed) return false;
        if (IsTrial && TrialDaysLeft > 7) return false;
        var last = Settings.LastSupportReminder;
        if (last != null && (DateTime.UtcNow - last.Value).TotalHours < 20) return false;
        Settings.LastSupportReminder = DateTime.UtcNow;
        SaveSettings();
        return true;
    }

    /// <summary>Funzioni riservate alla licenza (cloud, AI): in demo spiega e apre la finestra licenza.</summary>
    public bool RequireLicense(string feature)
    {
        if (!IsDemo) return true;
        StatusText = $"{feature}: serve la licenza Mixfonia (20 €, per sempre).";
        if (Application.Current?.MainWindow is { } w) new Views.SupportWindow(this) { Owner = w }.ShowDialog();
        return !IsDemo;
    }

    /// <summary>Le release pubblicate dopo la scadenza degli aggiornamenti non si installano (la data la dice GitHub).</summary>
    public async Task<bool> CanInstallVersionAsync(string version)
    {
        if (IsDemo || IsTrial) return true; // in prova si aggiorna sempre; è la licenza scaduta che blocca
        if (UpdatesUntil is not { } until) return true;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, $"https://api.github.com/repos/OdineOsborne/karaokedj/releases/tags/v{version}");
            req.Headers.UserAgent.ParseAdd("Mixfonia");
            using var r = await CloudHttp.SendAsync(req);
            if (!r.IsSuccessStatusCode) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var published = doc.RootElement.GetProperty("published_at").GetDateTime().ToUniversalTime().Date;
            return published <= until.Date;
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------- rimozione brani (pulizia doppioni)

    /// <summary>Toglie un brano dalla libreria; coda e playlist puntano al brano sostitutivo (se dato).</summary>
    public void RemoveTrackFromLibrary(Track t, Track? replaceWith = null)
    {
        Library.Remove(t);
        Tracks.Remove(t);
        SunoTracks.Remove(t);
        foreach (var q in Queue.Where(q => q.Track == t).ToList())
        {
            if (replaceWith != null) q.Track = replaceWith; else Queue.Remove(q);
        }
        foreach (var p in Playlists)
        {
            for (int i = 0; i < p.TrackIds.Count; i++)
                if (p.TrackIds[i] == t.Id) { if (replaceWith != null) p.TrackIds[i] = replaceWith.Id; else p.TrackIds.RemoveAt(i--); }
            p.NotifyCountChanged();
        }
        LibraryCount = Tracks.Count;
        Library.Save(); SavePlaylists(); SaveQueue();
    }

    // ---------------------------------------------------------------- MIDI

    // ---------------------------------------------------------------- azioni (tastiera + MIDI)

    public KeyboardService Keys { get; } = new();
    /// <summary>La finestra principale porta il fuoco sulla ricerca.</summary>
    public event Action? SearchFocusRequested;

    private void HandleMidiAction(string action, int value, bool continuous)
    {
        double norm = value / 127.0;
        // fader e manopole: un valore basso è una posizione (fader giù, EQ in taglio), non il rilascio di un tasto.
        // Solo per i pulsanti mandati su CC "premuto" vuol dire valore alto; sulle note il rilascio è velocity 0.
        bool isKnob = AppActions.Find(action)?.IsContinuous == true;
        bool pressed = isKnob || (continuous ? value >= 64 : value > 0);
        ExecuteAction(action, pressed, norm, continuous);
    }

    private DateTime _lastAudibleHint, _lastJogHint;

    /// <summary>
    /// Se muovi EQ, filtro, trim o fader di un deck che in quel momento non si sente (fermo, fader a zero,
    /// escluso dal crossfader, master a zero), lo scrive nella barra di stato: in serata evita di girare manopole a vuoto.
    /// </summary>
    private void HintIfInaudible(DeckViewModel d, string sub)
    {
        if (sub is not ("eqlow" or "eqmid" or "eqhigh" or "filtervalue" or "volume" or "fader" or "pan")) return;
        if ((DateTime.UtcNow - _lastAudibleHint).TotalSeconds < 3) return;
        string? why = !d.HasTrack ? "non ha nessun brano caricato"
            : !d.IsPlaying ? "è in pausa"
            : d.Fader < 0.02 && sub != "fader" ? "ha il fader di canale a zero"
            : d.Deck.CrossGain < 0.05 ? "è escluso dal crossfader"
            : MasterVolume < 0.02 ? "esce col volume master a zero"
            : null;
        if (why == null) return;
        _lastAudibleHint = DateTime.UtcNow;
        StatusText = $"Deck {d.Name} {why}: il comando non si sente";
    }

    /// <summary>Solo per diagnostica (--selftest): simula un messaggio MIDI già mappato su un'azione.</summary>
    public void SimulateMidi(string action, int value, bool continuous = true) => HandleMidiAction(action, value, continuous);

    /// <summary>
    /// Esegue un'azione del catalogo <see cref="AppActions"/>. <paramref name="pressed"/> false = rilascio (solo per le azioni "tieni premuto").
    /// <paramref name="norm"/> 0..1 per i controlli continui.
    /// </summary>
    public void ExecuteAction(string action, bool pressed, double norm = 1, bool continuous = false)
    {
        var deck = action.StartsWith("a.") ? DeckA : action.StartsWith("b.") ? DeckB : null;
        var sub = deck != null ? action[2..] : action;

        if (deck != null)
        {
            if (!pressed)
            {
                if (sub is "rev" or "slow" or "fwd" or "back" or "nudgeup" or "nudgedown") deck.HoldReleaseCommand.Execute(null);
                if (sub == "jogtouch") { deck.JogEnd(); }
                return;
            }
            switch (sub)
            {
                case "play": deck.TogglePlay(); break;
                case "stop": deck.Stop(); break;
                case "cue": deck.Cue(); break;
                case "playcue": deck.PlayFromCue(); break;
                case "tap": deck.Tap(); break;
                case "sync": SyncDeckCommand.Execute(deck); break;
                case "back10": deck.Back10(); break;
                case "fwd10": deck.Forward10(); break;
                case "eject": deck.Eject(); break;
                case "volume": if (continuous) deck.GainDb = (norm - 0.5) * 24; break;      // −12 … +12 dB, centro = unity
                case "tempo": if (continuous) deck.TempoPercent = (int)Math.Round((norm - 0.5) * 50); break; // −25 … +25
                case "temporeset": deck.TempoReset(); break;
                case "pan": if (continuous) deck.Pan = norm * 2 - 1; break;
                case "panreset": deck.Pan = 0; break;
                case "pingpong": deck.EchoPingPong = !deck.EchoPingPong; break;
                case "keyup": deck.KeyUp(); break;
                case "keydown": deck.KeyDown(); break;
                case "keyreset": deck.KeyReset(); break;
                case "keylock": deck.KeyLock = !deck.KeyLock; break;
                case "loop1": deck.LoopBeatsCommand.Execute("1"); break;
                case "loop2": deck.LoopBeatsCommand.Execute("2"); break;
                case "loop4": deck.LoopBeatsCommand.Execute("4"); break;
                case "loop8": deck.LoopBeatsCommand.Execute("8"); break;
                case "loophalf": deck.LoopHalfCommand.Execute(null); break;
                case "loopdouble": deck.LoopDoubleCommand.Execute(null); break;
                case "loopexit": deck.LoopExitCommand.Execute(null); break;
                case "jumpback4": deck.BeatJump("-4"); break;
                case "jumpfwd4": deck.BeatJump("4"); break;
                case "jumpback8": deck.BeatJump("-8"); break;
                case "jumpfwd8": deck.BeatJump("8"); break;
                case "quantize": deck.Quantize = !deck.Quantize; break;
                case "keymatch": KeyMatch(deck); break;
                case "cuepfl": deck.CueOn = !deck.CueOn; break;
                case "fader": if (continuous) deck.Fader = norm; break;
                case "jogtouch": deck.JogStart(); deck.LastJogMessage = DateTime.UtcNow; break;
                case "nudgeup": if (deck.Deck.IsPlaying) { deck.Deck.JogStart(); deck.Deck.JogRate(1.06, 0.05); } break;
                case "nudgedown": if (deck.Deck.IsPlaying) { deck.Deck.JogStart(); deck.Deck.JogRate(0.94, 0.05); } break;
                default:
                    if (sub.StartsWith("hotcue") && int.TryParse(sub[6..], out var hc)) deck.HotCue((hc - 1).ToString());
                    break;
                case "filter": deck.FilterOn = !deck.FilterOn; break;
                case "filtervalue": if (continuous) deck.FilterValue = norm * 2 - 1; break;
                case "filterreset": deck.FilterValue = 0; break;
                case "echo": deck.EchoOn = !deck.EchoOn; break;
                case "echoout": deck.EchoOutCommand.Execute(null); break;
                case "reverb": deck.ReverbOn = !deck.ReverbOn; break;
                case "flanger": deck.FlangerOn = !deck.FlangerOn; break;
                case "phaser": deck.PhaserOn = !deck.PhaserOn; break;
                case "crush": deck.CrushOn = !deck.CrushOn; break;
                case "gate": deck.GateOn = !deck.GateOn; break;
                case "fxreset": deck.FxResetCommand.Execute(null); break;
                case "vocaloff": deck.VocalRemove = !deck.VocalRemove; break;
                case "aivocal": deck.ToggleAiVocalCommand.Execute(null); break;
                case "stems": deck.ToggleStemsCommand.Execute(null); break;
                case "stemvocals": if (continuous) deck.StemVocals = norm * 1.5; break;
                case "stemdrums": if (continuous) deck.StemDrums = norm * 1.5; break;
                case "stembass": if (continuous) deck.StemBass = norm * 1.5; break;
                case "stemother": if (continuous) deck.StemOther = norm * 1.5; break;
                case "eqlow": if (continuous) deck.EqLow = (norm - 0.5) * 24; break;
                case "eqmid": if (continuous) deck.EqMid = (norm - 0.5) * 24; break;
                case "eqhigh": if (continuous) deck.EqHigh = (norm - 0.5) * 24; break;
                case "killlow": deck.KillLowCommand.Execute(null); break;
                case "killmid": deck.KillMidCommand.Execute(null); break;
                case "killhigh": deck.KillHighCommand.Execute(null); break;
                case "eqreset": deck.EqResetCommand.Execute(null); break;
                case "brake": deck.BrakeCommand.Execute(null); break;
                case "backspin": deck.BackspinCommand.Execute(null); break;
                case "spinfwd": deck.SpinForwardCommand.Execute(null); break;
                case "spinback": deck.SpinBackCommand.Execute(null); break;
                // i "tieni premuto" lavorano sul disco che gira: su un deck fermo non devono avviarlo
                case "rev": if (deck.Deck.IsPlaying) deck.ReverseHoldCommand.Execute(null); break;
                case "slow": if (deck.Deck.IsPlaying) deck.SlowHoldCommand.Execute(null); break;
                case "fwd": if (deck.Deck.IsPlaying) deck.ForwardHoldCommand.Execute(null); break;
                case "back": if (deck.Deck.IsPlaying) deck.BackwardHoldCommand.Execute(null); break;
                case "jog":
                    // encoder relativo (jog wheel MIDI): 1..63 avanti, 65..127 indietro (delta in tacche).
                    // Senza la mano sul piatto è un pitch bend (la traccia non torna indietro: è il comportamento dei mixer veri).
                    if (continuous)
                    {
                        int v = (int)Math.Round(norm * 127);
                        int delta = v == 0 ? 0 : v < 64 ? v : v - 128;
                        if (deck.IsJogging) { deck.LastJogMessage = DateTime.UtcNow; deck.JogRate(Math.Clamp(delta * JogTicksToRate, -8, 8)); }
                        // Regola di ferro: il piatto NON fa partire un deck fermo. Se il DJ ha messo in pausa,
                        // sfiorare o urtare la console non deve far ripartire la musica (vedi anche Panic).
                        else if (delta != 0 && deck.Deck.IsPlaying) deck.Nudge(Math.Sign(delta));
                    }
                    break;
                case "jogscratch":
                    // Alcune console (Hercules Instinct P8, Inpulse…) mandano il movimento del piatto su un CC diverso
                    // quando ci appoggi la mano, ma non mandano nessun tasto "tocco": qui il tocco lo deduciamo dal messaggio,
                    // e si esce dallo scratch da soli quando il piatto smette di mandare (vedi TickControllerJog).
                    if (continuous)
                    {
                        int v = (int)Math.Round(norm * 127);
                        int delta = v == 0 ? 0 : v < 64 ? v : v - 128;
                        // Il tocco dedotto dal messaggio vale solo su un deck che sta già suonando: su un deck in pausa
                        // un piatto che manda da solo (o una manata di passaggio) farebbe ripartire la musica, e la serata è persa.
                        if (!deck.IsJogging && (!deck.Deck.IsPlaying || delta == 0))
                        {
                            if (delta != 0 && deck.HasTrack && (DateTime.UtcNow - _lastJogHint).TotalSeconds > 4)
                            {
                                _lastJogHint = DateTime.UtcNow;
                                StatusText = $"Deck {deck.Name} è fermo: il piatto non lo fa partire (premi PLAY e poi gira il piatto)";
                            }
                            break;
                        }
                        if (!deck.IsJogging) deck.JogStart();
                        deck.AutoJog = true;
                        deck.LastJogMessage = DateTime.UtcNow;
                        deck.JogRate(Math.Clamp(delta * JogTicksToRate, -8, 8));
                    }
                    break;
            }
            if (continuous) HintIfInaudible(deck, sub);
            return;
        }

        if (!pressed) { if (action == "talk") TalkOver = false; return; }
        switch (action)
        {
            case "crossfader": if (continuous) { _crossfadeTarget = null; if (_mix != null) AbortMix("crossfader mosso a mano"); Crossfader = norm * 2 - 1; } break;
            case "master": if (continuous) MasterVolume = norm * 1.2; break;
            case "next": PlayNextCommand.Execute(null); break;
            case "fadeA": StartCrossfade(-1); break;
            case "fadeB": StartCrossfade(1); break;
            case "automix": AutoMix = !AutoMix; break;
            case "projector": IsProjectorOpen = !IsProjectorOpen; break;
            case "monitor": IsMonitorOpen = !IsMonitorOpen; break;
            case "search": SearchFocusRequested?.Invoke(); break;
            case "addqueue": AddToQueueCommand.Execute(null); break;
            case "queuetop": QueueToTopCommand.Execute(null); break;
            case "mixnow": MixNowCommand.Execute(null); break;
            case "loadA": LoadSelectedToACommand.Execute(null); break;
            case "loadB": LoadSelectedToBCommand.Execute(null); break;
            case "browse": if (continuous) { int v = (int)Math.Round(norm * 127); MoveLibrarySelection(v == 0 ? 0 : v < 64 ? v : v - 128); } break;
            case "browseup": MoveLibrarySelection(-1); break;
            case "browsedown": MoveLibrarySelection(1); break;
            case "browseload": if (SelectedTrack != null) LoadToDeck(FreeDeck(), SelectedTrack, SingerName, QueueKeyShift); break;
            case "rhythm.play": Rhythm.TogglePlayCommand.Execute(null); break;
            case "rhythm.tap": Rhythm.Tap(); break;
            case "rhythm.resync": Rhythm.ResyncCommand.Execute(null); break;
            case "rhythm.volume": if (continuous) Rhythm.Volume = (float)(norm * 1.2); break;
            case "padstop": StopAllPadsCommand.Execute(null); break;
            case "panic": Panic(); break;
            case "midimute": MidiMuted = !MidiMuted; break;
            case "mic": MicOn = !MicOn; break;
            case "talk": TalkOver = pressed; break;
            case "micvolume": if (continuous) MicGainDb = (norm - 0.5) * 48; break;
            case "cuemix": if (continuous) CueMix = norm; break;
            case "cuevolume": if (continuous) CueVolume = norm; break;
            case "fill": FillMusicOn = !FillMusicOn; break;
            case "rotation": RotationOn = !RotationOn; break;
            default:
                if (action.StartsWith("pad") && int.TryParse(action[3..], out var n)) TriggerPadByIndex(n - 1);
                break;
        }
    }

    /// <summary>Tacche del jog → velocità di scratch (dipende dalla risoluzione del piatto; le console Pioneer/Numark stanno intorno a 0,1).</summary>
    public const double JogTicksToRate = 0.12;

    /// <summary>Encoder BROWSE della console: sposta la selezione in libreria.</summary>
    public void MoveLibrarySelection(int delta)
    {
        if (delta == 0) return;
        var items = LibraryView.Cast<Track>().ToList();
        if (items.Count == 0) return;
        int i = SelectedTrack != null ? items.IndexOf(SelectedTrack) : -1;
        i = Math.Clamp(i + delta, 0, items.Count - 1);
        SelectedTrack = items[i];
        LibraryScrollRequested?.Invoke(SelectedTrack);
    }
    public event Action<Track>? LibraryScrollRequested;

    /// <summary>Dal timer: se la mano è sul piatto ma non arrivano più tacche, il disco si ferma (come un vinile tenuto).</summary>
    private void TickControllerJog()
    {
        foreach (var d in new[] { DeckA, DeckB })
        {
            if (d.LastJogMessage == default) continue;
            double ms = (DateTime.UtcNow - d.LastJogMessage).TotalMilliseconds;
            // piatto fermo sotto la mano: il vinile si ferma
            if (d.IsJogging && ms > 70) { d.JogRate(0); if (!d.AutoJog) d.LastJogMessage = DateTime.UtcNow; }
            // tocco dedotto (console senza tasto "mano sul piatto"): dopo un attimo di silenzio la traccia riparte da sola
            if (d.AutoJog && ms > 260) { d.AutoJog = false; d.JogEnd(); d.LastJogMessage = default; }
        }
    }

    /// <summary>Tasto premuto/rilasciato nella finestra principale. Ritorna true se gestito.</summary>
    public bool HandleKey(string gesture, bool pressed)
    {
        var a = Keys.ActionFor(gesture);
        if (a == null) return false;
        var def = AppActions.Find(a);
        if (!pressed && def?.IsHold != true) return true; // il rilascio conta solo per le azioni "tieni premuto"
        ExecuteAction(a, pressed);
        return true;
    }

    // ---------------------------------------------------------------- generi con AI

    [ObservableProperty] private bool _isClassifying;
    private CancellationTokenSource? _classifyCts;

    /// <summary>Assegna genere (e anno) ai brani audio che non ce l'hanno, a lotti di 80 con Claude. Serve per la continuità dell'automix.</summary>
    [RelayCommand]
    private async Task ClassifyGenresAsync()
    {
        if (IsClassifying) { _classifyCts?.Cancel(); return; }
        if (!RequireLicense("Generi con AI")) return;
        var key = Secret.Unprotect(Settings.AnthropicApiKeyProtected);
        if (string.IsNullOrEmpty(key)) { StatusText = "Serve la chiave API Anthropic (Impostazioni → AI)"; return; }
        var todo = Tracks.Where(t => !t.IsKaraoke && string.IsNullOrWhiteSpace(t.Genre) && !MainViewModel.IsCryptic(t)).ToList();
        if (todo.Count == 0) { StatusText = "Tutti i brani hanno già un genere"; return; }
        if (MessageBox.Show($"Assegnare genere e anno con l'AI a {todo.Count} brani senza tag?\n(circa {Math.Ceiling(todo.Count / 80.0)} richieste a Claude, qualche minuto)", "Generi con AI",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _classifyCts = new CancellationTokenSource();
        IsClassifying = true;
        int done = 0, set = 0;
        try
        {
            foreach (var batch in todo.Chunk(80))
            {
                _classifyCts.Token.ThrowIfCancellationRequested();
                StatusText = $"Generi AI: {done}/{todo.Count}…";
                var res = await GenreClassifier.ClassifyAsync(batch, key, _classifyCts.Token);
                foreach (var t in batch)
                {
                    if (!res.TryGetValue(t.Id, out var r)) continue;
                    t.Genre = string.Join("; ", Track.SplitGenres(r.genre).Take(DeckViewModel.MaxGenreTags)); if (t.Year <= 0 && r.year > 0) t.Year = r.year;
                    t.InvalidateSearchCache(); set++;
                }
                done += batch.Length;
                Library.Save();
                LibraryView.Refresh();
            }
            StatusText = $"Generi AI: assegnati {set} su {todo.Count}";
            RebuildGenreChips();
        }
        catch (OperationCanceledException) { StatusText = $"Generi AI interrotto ({done}/{todo.Count})"; }
        catch (Exception ex) { StatusText = "Generi AI: " + ex.Message; }
        finally { IsClassifying = false; }
    }

    // ---------------------------------------------------------------- database: verifica all'avvio e cartelle sorvegliate

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _pendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _watchTimer;
    /// <summary>Brani il cui file non è raggiungibile ora (disco scollegato?): restano nel database, non si vedono.</summary>
    [ObservableProperty] private int _missingTracks;

    /// <summary>
    /// All'avvio non si riscansiona nulla: il database è la verità. In background si verifica che i file esistano
    /// (quelli mancanti vengono nascosti, non cancellati), si preparano gli indici di ricerca e si ottimizza il DB.
    /// </summary>
    private async Task VerifyDbAsync()
    {
        var all = Tracks.ToList();
        if (all.Count == 0) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var missing = await Task.Run(() =>
        {
            int n = 0;
            // un solo Exists per cartella prima: se la radice non c'è (disco scollegato) evitiamo 20.000 accessi lenti
            var dirOk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in all)
            {
                var dir = Path.GetDirectoryName(t.FilePath) ?? "";
                if (!dirOk.TryGetValue(dir, out var ok)) dirOk[dir] = ok = Directory.Exists(dir);
                t.Missing = !ok || !File.Exists(t.FilePath);
                if (t.Missing) n++;
                _ = t.SearchWords; // pre-calcola l'indice di ricerca
            }
            return n;
        });
        MissingTracks = missing;
        CollapseDuplicates();
        LibraryView.Refresh();
        try { await Task.Run(() => Library.Db.Optimize()); } catch { }
        if (missing > 0) StatusText = $"Libreria pronta ({sw.ElapsedMilliseconds} ms) · {missing} brani non raggiungibili ora (disco scollegato?): nascosti, non cancellati";
    }

    /// <summary>Sorveglia le cartelle della libreria: i file nuovi/rinominati/cancellati entrano ed escono da soli, senza riscansione.</summary>
    private void StartFolderWatchers()
    {
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
        _watchers.Clear();
        foreach (var folder in Settings.LibraryFolders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 64 * 1024 };
                w.Created += (_, e) => QueueFile(e.FullPath);
                w.Changed += (_, e) => QueueFile(e.FullPath);
                w.Renamed += (_, e) => { QueueFile(e.OldFullPath); QueueFile(e.FullPath); };
                w.Deleted += (_, e) => QueueFile(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { /* percorsi di rete o permessi: si usa Riscansiona */ }
        }
        _watchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _watchTimer.Tick -= WatchTick; _watchTimer.Tick += WatchTick;
        _watchTimer.Start();
    }

    private void QueueFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!(SourceFactory.AudioExtensions.Contains(ext) || SourceFactory.VideoExtensions.Contains(ext) || ext == ".zip" || ext == ".cdg" || MidiRenderService.IsMidi(path))) return;
        lock (_pendingFiles) _pendingFiles.Add(path);
    }

    /// <summary>Applica i cambiamenti accumulati (debounce: i file grandi arrivano a pezzi).</summary>
    private async void WatchTick(object? sender, EventArgs e)
    {
        List<string> batch;
        lock (_pendingFiles) { if (_pendingFiles.Count == 0) return; batch = _pendingFiles.ToList(); _pendingFiles.Clear(); }
        int added = 0, removed = 0;
        foreach (var path in batch)
        {
            var p = Path.GetExtension(path).Equals(".cdg", StringComparison.OrdinalIgnoreCase) ? Path.ChangeExtension(path, ".mp3") : path;
            var existing = Library.FindByPath(p);
            if (!File.Exists(p))
            {
                if (existing != null) { RemoveTrackFromLibrary(existing); removed++; }
                continue;
            }
            // file ancora in scrittura? riprova al prossimo giro
            try { using var fs = File.Open(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch { lock (_pendingFiles) _pendingFiles.Add(path); continue; }
            var t = await Task.Run(() => Library.AddFile(p));
            if (t == null) continue;
            TitleCleaner.Apply(t, writeTags: false);
            Library.Save(t);
            var old = Tracks.FirstOrDefault(x => string.Equals(x.FilePath, p, StringComparison.OrdinalIgnoreCase));
            if (old != null) { if (old.Id == t.Id) continue; Tracks.Remove(old); }
            Tracks.Add(t);
            added++;
            if (Settings.AutoAnalyze) AnalyzeInBackground(t);
        }
        if (added + removed > 0)
        {
            LibraryCount = Tracks.Count;
            CollapseDuplicates();
            LibraryView.Refresh();
            _remote?.LibraryChanged();
            StatusText = $"Libreria aggiornata: +{added} −{removed}";
        }
    }

    // ---------------------------------------------------------------- QR sul proiettore

    [ObservableProperty] private bool _qrOverlayVisible;
    [ObservableProperty] private System.Windows.Media.ImageSource? _qrOverlayImage;
    [ObservableProperty] private string _qrOverlayCaption = "";
    private CancellationTokenSource? _qrCts;

    /// <summary>Mostra un QR grande sul proiettore per <paramref name="seconds"/> secondi (0 = finché non viene nascosto).</summary>
    public async void ShowQrOnProjector(string url, string caption, int seconds)
    {
        _qrCts?.Cancel();
        var cts = _qrCts = new CancellationTokenSource();
        QrOverlayImage = Views.RemoteWindow.MakeQr(url, 12);
        QrOverlayCaption = caption;
        QrOverlayVisible = true;
        if (!IsProjectorOpen) IsProjectorOpen = true;
        if (seconds <= 0) return;
        try { await Task.Delay(TimeSpan.FromSeconds(seconds), cts.Token); QrOverlayVisible = false; } catch (OperationCanceledException) { }
    }

    public void HideQrOnProjector() { _qrCts?.Cancel(); QrOverlayVisible = false; }

    // ---------------------------------------------------------------- scaletta remota (QR → telefono)

    private RemoteSetlistService? _remote;
    public RemoteSetlistService Remote => _remote ??= new RemoteSetlistService(CloudBaseUrl, RemoteState, RemoteLibrary, ApplyRemoteCommand);

    private object RemoteState()
    {
        object DeckInfo(DeckViewModel d) => new
        {
            name = d.Name, title = d.Track?.Title ?? "", artist = d.Track?.Artist ?? "", singer = d.Singer,
            playing = d.IsPlaying, remaining = Math.Round(Math.Max(0, d.DurationSec - d.PositionSec) / 5) * 5, // arrotondato: meno push
        };
        return new
        {
            decks = new { a = DeckInfo(DeckA), b = DeckInfo(DeckB) },
            queue = Queue.Select(e => new { id = e.Track.Id, title = e.Track.Title, artist = e.Track.Artist, singer = e.Singer }).ToList(),
            suggestions = Suggestions.Take(6).Select(t => new { id = t.Id, title = t.Title, artist = t.Artist }).ToList(),
            autoMix = AutoMix,
            libVersion = Remote.LibraryVersion,
        };
    }

    private object RemoteLibrary() =>
        Tracks.Select(t => new { id = t.Id, a = t.Artist, t = t.Title, b = t.Bpm > 0 ? (int)Math.Round(t.Bpm) : 0, k = t.IsKaraoke ? "K" : "" }).ToList();

    /// <summary>Comandi dal telefono, eseguiti sul thread UI.</summary>
    private void ApplyRemoteCommand(RemoteCommand c)
    {
        Track? T(string? id) => id == null ? null : Library.FindById(id);
        switch (c.Cmd)
        {
            case "add": if (T(c.Id) is { } t1) AddToQueue(t1, c.Singer ?? "", 0); break;
            case "addtop": if (T(c.Id) is { } t2) { Queue.Insert(0, new QueueEntry { Track = t2, Singer = c.Singer ?? "" }); StatusText = $"Dal telefono, prossimo: {t2.Display}"; } break;
            case "remove": if (c.Index is int ri && ri >= 0 && ri < Queue.Count) RemoveFromQueue(Queue[ri]); break;
            case "up": if (c.Index is int ui && ui > 0 && ui < Queue.Count) Queue.Move(ui, ui - 1); break;
            case "down": if (c.Index is int di && di >= 0 && di < Queue.Count - 1) Queue.Move(di, di + 1); break;
            case "top": if (c.Index is int ti && ti > 0 && ti < Queue.Count) Queue.Move(ti, 0); break;
            case "move": if (c.Index is int mi && c.To is int mt && mi >= 0 && mi < Queue.Count && mt >= 0 && mt < Queue.Count && mi != mt) Queue.Move(mi, mt); break;
            case "next": PlayNextCommand.Execute(null); StatusText = "Dal telefono: mix now"; break;
            case "fadeA": StartCrossfade(-1); break;
            case "request": AddRequest(c); break;
        }
    }

    // ---------------------------------------------------------------- download

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var input = DownloadInput.Trim();
        if (string.IsNullOrEmpty(input) || IsDownloading) return;
        var first = await DownloadCoreAsync(input);
        if (first != null) DownloadInput = "";
    }

    /// <summary>Scarica (URL, Spotify, testo libero → ricerca) e aggiunge alla libreria. Ritorna il primo brano scaricato, null se fallito.</summary>
    private async Task<Track?> DownloadCoreAsync(string input)
    {
        if (IsDownloading) return null;
        _downloadCts = new CancellationTokenSource();
        IsDownloading = true;
        DownloadPercent = 0;
        var progress = new Progress<Mixfonia.Plugins.ImportProgress>(s =>
        {
            DownloadStatus = s.Message;
            if (s.Percent >= 0) DownloadPercent = s.Percent;
        });
        try
        {
            var source = SelectedSource ?? throw new InvalidOperationException("Nessuna sorgente di importazione: installa un plugin o scegli una fonte in Impostazioni → Plugin e fonti");
            var paths = await source.ImportAsync(input, DownloadVideo && source.SupportsVideo, AppPaths.DownloadsDir, progress, _downloadCts.Token);
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
            if (!Settings.LibraryFolders.Contains(AppPaths.DownloadsDir, StringComparer.OrdinalIgnoreCase))
            {
                Settings.LibraryFolders.Add(AppPaths.DownloadsDir);
                SaveSettings();
            }
            return first;
        }
        catch (OperationCanceledException) { DownloadStatus = "Annullato"; return null; }
        catch (Exception ex) { DownloadStatus = "Errore: " + ex.Message; return null; }
        finally { IsDownloading = false; }
    }

    [RelayCommand] private void CancelDownload() => _downloadCts?.Cancel();

    // ---------------------------------------------------------------- suggeriti fuori libreria (AI → download → coda)

    public ObservableCollection<ExternalSuggestion> ExternalSuggestions { get; } = new();
    [ObservableProperty] private bool _isSuggestingExternal;
    [ObservableProperty] private string _externalSuggestionsLabel = "";

    /// <summary>Chiede a Claude brani non in libreria adatti a seguire quello in corso (coerenza: decade/genere come i suggeriti).</summary>
    [RelayCommand]
    private async Task SuggestExternalAsync()
    {
        if (IsSuggestingExternal) return;
        if (!RequireLicense("Suggerimenti AI")) return;
        var r = CompatReference();
        if (r == null) { ExternalSuggestionsLabel = "Manda in play (o seleziona) un brano di riferimento"; return; }
        var key = Secret.Unprotect(Settings.AnthropicApiKeyProtected);
        if (string.IsNullOrEmpty(key)) { ExternalSuggestionsLabel = "Serve la chiave API Anthropic (Impostazioni → Generale → AI)"; return; }
        IsSuggestingExternal = true;
        ExternalSuggestionsLabel = $"Cerco brani nuovi dopo {r.Display}…";
        try
        {
            var list = await SuggestService.SuggestAsync(r, SuggestBy, Library.Tracks, Feedback.ExternalRejected, key, CancellationToken.None);
            ExternalSuggestions.Clear();
            foreach (var s in list) ExternalSuggestions.Add(s);
            ExternalSuggestionsLabel = list.Count == 0 ? "Nessuna proposta fuori libreria" : $"fuori libreria, dopo: {r.Display}";
        }
        catch (Exception ex) { ExternalSuggestionsLabel = "Errore AI: " + ex.Message; }
        finally { IsSuggestingExternal = false; }
    }

    /// <summary>Scarica il brano proposto (ricerca YouTube via yt-dlp) e lo mette in coda.</summary>
    [RelayCommand]
    private async Task DownloadSuggestionAsync(ExternalSuggestion? s)
    {
        if (s == null || IsDownloading) return;
        StatusText = $"Scarico {s.Display}…";
        var track = await DownloadCoreAsync(s.Query);
        if (track == null) { StatusText = "Download non riuscito: " + DownloadStatus; return; }
        // il titolo/artista proposti dall'AI sono più affidabili del nome del video
        if (!string.IsNullOrWhiteSpace(s.Artist)) track.Artist = s.Artist;
        if (!string.IsNullOrWhiteSpace(s.Title)) track.Title = s.Title;
        track.InvalidateSearchCache();
        TitleCleaner.Apply(track, writeTags: true);
        Library.Save(track);
        LibraryView.Refresh();
        ExternalSuggestions.Remove(s);
        AddToQueue(track, "", 0);
    }

    /// <summary>Scarica e mette in cima alla coda (prossimo).</summary>
    [RelayCommand]
    private async Task DownloadSuggestionNextAsync(ExternalSuggestion? s)
    {
        if (s == null || IsDownloading) return;
        await DownloadSuggestionAsync(s);
        var e = Queue.LastOrDefault();
        if (e != null && Queue.Count > 1) Queue.Move(Queue.Count - 1, 0);
    }
}
