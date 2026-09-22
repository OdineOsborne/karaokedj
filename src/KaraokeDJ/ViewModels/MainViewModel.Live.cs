using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;
using KaraokeDJ.Services;

namespace KaraokeDJ.ViewModels;

/// <summary>Una prenotazione arrivata dal pubblico (pagina /canta via QR): da accettare o scartare.</summary>
public sealed partial class SongRequest : ObservableObject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public Track? Track { get; init; }
    public string TitleText { get; init; } = "";
    public string Singer { get; init; } = "";
    public string Note { get; init; } = "";
    public DateTime At { get; init; } = DateTime.Now;
    public string Display => (string.IsNullOrWhiteSpace(Singer) ? "" : Singer + " → ") + (Track?.Display ?? TitleText) + (string.IsNullOrWhiteSpace(Note) ? "" : $"  «{Note}»");
    public string When => At.ToString("HH:mm");
}

/// <summary>Cosa ricorda dei cantanti: quante canzoni, quando, e la tonalità usata per ogni brano.</summary>
public sealed class SingerInfo
{
    public DateTime LastSeenUtc { get; set; }
    public int Songs { get; set; }
    public Dictionary<string, int> KeyByTrack { get; set; } = new();
}

/// <summary>
/// Parte "live" del mixer: microfono con talk-over, cuffia (pre-ascolto), tonalità compatibili, rotazione dei cantanti,
/// musica di riempimento fra un cantante e l'altro e prenotazioni dal pubblico.
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- microfono

    [ObservableProperty] private bool _micOn;
    [ObservableProperty] private bool _talkOver;
    [ObservableProperty] private double _micGainDb;
    [ObservableProperty] private bool _micEcho;
    [ObservableProperty] private bool _micReverb = true;
    [ObservableProperty] private bool _micAutoDuck = true;
    [ObservableProperty] private double _micDuckDb = -10;
    [ObservableProperty] private double _micEqLow;
    [ObservableProperty] private double _micEqMid;
    [ObservableProperty] private double _micEqHigh;
    /// <summary>Sezione EQ/effetti del microfono aperta nella barra LIVE.</summary>
    [ObservableProperty] private bool _micEqVisible;
    [ObservableProperty] private double _micLevel;
    [ObservableProperty] private string _micStatus = "";
    public string MicGainLabel => Math.Abs(MicGainDb) < 0.05 ? "0 dB" : MicGainDb.ToString("+0;-0") + " dB";
    public string MicDuckLabel => MicDuckDb.ToString("0") + " dB";

    private void InitLive()
    {
        MicEqLow = Settings.MicEqLow; MicEqMid = Settings.MicEqMid; MicEqHigh = Settings.MicEqHigh;
        MicGainDb = Settings.MicGainDb; MicEcho = Settings.MicEcho; MicReverb = Settings.MicReverb; MicAutoDuck = Settings.MicAutoDuck; MicDuckDb = Settings.MicDuckDb;
        CueMix = Settings.CueMix; CueVolume = Settings.CueVolume;
        DeckA.Quantize = DeckB.Quantize = Settings.Quantize;
        foreach (var d in new[] { DeckA, DeckB })
            d.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(DeckViewModel.Quantize) && s is DeckViewModel dv) { Settings.Quantize = dv.Quantize; var o = dv == DeckA ? DeckB : DeckA; if (o.Quantize != dv.Quantize) o.Quantize = dv.Quantize; } };
        // come l'auto-mix: il riempimento parte spento a ogni avvio (è l'altra cosa che può far suonare
        // un brano senza che nessuno abbia premuto play), il volume invece si ricorda
        FillMusicOn = false; FillVolume = Settings.FillVolume;
        RotationOn = Settings.RotationOn; PublicRequestsOn = Settings.PublicRequestsOn;
        TickerOn = Settings.TickerOn; TickerText = Settings.TickerText ?? "";
        Engine.Mic.DuckThreshold = (float)Math.Pow(10, Settings.MicDuckThresholdDb / 20);
        _singers = JsonStore.Load<Dictionary<string, SingerInfo>>(Path.Combine(AppPaths.Root, "singers.json")) ?? new();
        RefreshKnownSingers();
        if (!string.IsNullOrEmpty(Settings.CueDeviceId)) ApplyCueDevice(Settings.CueDeviceId);
        if (Settings.MicOn) MicOn = true;
    }

    private void SaveLiveSettings()
    {
        Settings.MicEqLow = MicEqLow; Settings.MicEqMid = MicEqMid; Settings.MicEqHigh = MicEqHigh;
        Settings.MicOn = MicOn; Settings.MicGainDb = MicGainDb; Settings.MicEcho = MicEcho; Settings.MicReverb = MicReverb; Settings.MicAutoDuck = MicAutoDuck; Settings.MicDuckDb = MicDuckDb;
        Settings.CueMix = CueMix; Settings.CueVolume = CueVolume;
        Settings.FillMusicOn = FillMusicOn; Settings.FillVolume = FillVolume; Settings.FillPlaylistId = FillPlaylist?.Id;
        Settings.RotationOn = RotationOn; Settings.PublicRequestsOn = PublicRequestsOn;
    }

    partial void OnMicOnChanged(bool value)
    {
        if (value) ApplyMic(); else { Engine.Mic.Stop(); MicStatus = ""; TalkOver = false; }
    }

    /// <summary>Avvia il microfono sull'ingresso scelto nelle impostazioni.</summary>
    public void ApplyMic()
    {
        Engine.Mic.Start(Settings.MicDeviceId);
        MicStatus = Engine.Mic.Status;
        if (!Engine.Mic.IsOn) { StatusText = MicStatus; _micOn = false; OnPropertyChanged(nameof(MicOn)); return; }
        ApplyMicParams();
        StatusText = MicStatus;
    }

    private void ApplyMicParams()
    {
        var m = Engine.Mic;
        m.Gain = (float)Math.Pow(10, MicGainDb / 20);
        m.Fx.EchoOn = MicEcho; m.Fx.EchoTimeSec = 0.28f; m.Fx.EchoFeedback = 0.3f; m.Fx.EchoMix = 0.25f;
        m.Fx.ReverbOn = MicReverb; m.Fx.ReverbSize = 0.55f; m.Fx.ReverbMix = 0.22f;
        m.AutoDuck = MicAutoDuck;
        m.DuckGain = (float)Math.Pow(10, MicDuckDb / 20);
        m.Fx.EqLow = (float)MicEqLow; m.Fx.EqMid = (float)MicEqMid; m.Fx.EqHigh = (float)MicEqHigh;
        m.TalkOver = TalkOver;
    }

    partial void OnMicGainDbChanged(double value) { ApplyMicParams(); OnPropertyChanged(nameof(MicGainLabel)); }
    partial void OnMicEchoChanged(bool value) => ApplyMicParams();
    partial void OnMicReverbChanged(bool value) => ApplyMicParams();
    partial void OnMicAutoDuckChanged(bool value) => ApplyMicParams();
    partial void OnMicDuckDbChanged(double value) { ApplyMicParams(); OnPropertyChanged(nameof(MicDuckLabel)); }
    partial void OnMicEqLowChanged(double value) => ApplyMicParams();
    partial void OnMicEqMidChanged(double value) => ApplyMicParams();
    partial void OnMicEqHighChanged(double value) => ApplyMicParams();
    partial void OnTalkOverChanged(bool value) { if (value && !MicOn) MicOn = true; Engine.Mic.TalkOver = value; }

    [RelayCommand] private void MicGainReset() => MicGainDb = 0;

    // ---------------------------------------------------------------- cuffia (pre-ascolto)

    [ObservableProperty] private double _cueMix;
    [ObservableProperty] private double _cueVolume = 1;
    [ObservableProperty] private bool _cueAvailable;
    public string CueMixLabel => CueMix < 0.05 ? "CUE" : CueMix > 0.95 ? "MASTER" : $"{(1 - CueMix) * 100:0}/{CueMix * 100:0}";

    public void ApplyCueDevice(string? deviceId)
    {
        Settings.CueDeviceId = deviceId;
        if (string.IsNullOrEmpty(deviceId)) { Engine.StopCue(); CueAvailable = false; return; }
        Engine.StartCue(deviceId);
        CueAvailable = Engine.CueRunning;
        StatusText = Engine.CueDescription;
    }

    partial void OnCueMixChanged(double value) { Engine.CueMix = (float)value; OnPropertyChanged(nameof(CueMixLabel)); }
    partial void OnCueVolumeChanged(double value) => Engine.CueVolume = (float)value;

    // ---------------------------------------------------------------- tonalità compatibili (Camelot)

    /// <summary>Tonalità effettiva del deck (trasposizione e tempo senza key lock inclusi), "" se ignota.</summary>
    public static string EffectiveKey(DeckViewModel d)
    {
        if (d.Track == null || string.IsNullOrEmpty(d.Track.Key)) return "";
        double factor = 1.0 + d.TempoPercent / 100.0;
        int semis = d.KeyShift + (d.KeyLock ? 0 : (int)Math.Round(12 * Math.Log2(factor)));
        return AudioAnalyzer.Transpose(d.Track.Key, semis);
    }

    /// <summary>Regola Camelot: stesso numero (A↔B) o stessa lettera con numero ±1.</summary>
    public static bool KeyCompatible(string keyA, string keyB)
    {
        var a = AudioAnalyzer.CamelotOfKey(keyA); var b = AudioAnalyzer.CamelotOfKey(keyB);
        if (a.Length < 2 || b.Length < 2) return false;
        if (!int.TryParse(a[..^1], out var na) || !int.TryParse(b[..^1], out var nb)) return false;
        char la = a[^1], lb = b[^1];
        if (na == nb) return true;
        int diff = ((na - nb) % 12 + 12) % 12;
        return la == lb && (diff == 1 || diff == 11);
    }

    /// <summary>Deck di riferimento per la tonalità: quello che si sente di più.</summary>
    private DeckViewModel? KeyReferenceDeck(DeckViewModel? except = null)
    {
        var cands = new[] { DeckA, DeckB }.Where(d => d != except && d.HasTrack).ToList();
        if (cands.Count == 0) return null;
        var playing = cands.Where(d => d.IsPlaying).ToList();
        if (playing.Count == 1) return playing[0];
        if (playing.Count == 2) return Crossfader <= 0 ? DeckA : DeckB;
        return cands[0];
    }

    /// <summary>Trasporta il deck (al massimo ±3 semitoni) fino a una tonalità compatibile con l'altro deck.</summary>
    [RelayCommand]
    public void KeyMatch(DeckViewModel? deck)
    {
        if (deck?.Track == null) return;
        var other = deck == DeckA ? DeckB : DeckA;
        var refKey = EffectiveKey(other);
        if (string.IsNullOrEmpty(refKey) || string.IsNullOrEmpty(deck.Track.Key)) { StatusText = "Tonalità sconosciuta: analizza i brani prima"; return; }
        foreach (var shift in new[] { 0, 1, -1, 2, -2, 3, -3 })
        {
            var k = AudioAnalyzer.Transpose(deck.Track.Key, shift);
            if (KeyCompatible(k, refKey))
            {
                deck.KeyShift = shift;
                StatusText = shift == 0 ? $"Deck {deck.Name}: già compatibile ({k} · {AudioAnalyzer.CamelotOfKey(k)})" : $"Deck {deck.Name}: {shift:+0;-0} semitoni → {k} ({AudioAnalyzer.CamelotOfKey(k)}), compatibile con {refKey}";
                return;
            }
        }
        StatusText = $"Nessuna trasposizione entro ±3 semitoni rende {deck.Track.Key} compatibile con {refKey}";
    }

    /// <summary>Filtro libreria "♪": solo brani in tonalità compatibile con il deck che si sente.</summary>
    private bool KeyFilterOk(Track t)
    {
        var r = KeyReferenceDeck();
        var rk = r != null ? EffectiveKey(r) : (SelectedTrack?.Key ?? "");
        if (string.IsNullOrEmpty(rk)) return true;
        return !string.IsNullOrEmpty(t.Key) && KeyCompatible(t.Key, rk);
    }

    // ---------------------------------------------------------------- rotazione cantanti e memoria

    [ObservableProperty] private bool _rotationOn = true;
    private Dictionary<string, SingerInfo> _singers = new();
    public ObservableCollection<string> KnownSingers { get; } = new();

    private static string SingerKey(string s) => s.Trim().ToLowerInvariant();

    private void SaveSingers() { try { JsonStore.Save(Path.Combine(AppPaths.Root, "singers.json"), _singers); } catch { } }

    private void RefreshKnownSingers()
    {
        var names = _singers.Where(kv => (DateTime.UtcNow - kv.Value.LastSeenUtc).TotalDays < 400)
            .OrderByDescending(kv => kv.Value.LastSeenUtc).Select(kv => kv.Key).ToList();
        foreach (var q in Queue) if (!string.IsNullOrWhiteSpace(q.Singer) && !names.Contains(SingerKey(q.Singer))) names.Insert(0, q.Singer.Trim());
        KnownSingers.Clear();
        foreach (var n in names.Take(60)) KnownSingers.Add(_singers.TryGetValue(n, out _) ? Pretty(n) : n);
        static string Pretty(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>Tonalità che il cantante ha usato l'ultima volta su questo brano (0 se mai).</summary>
    public int RememberedKey(string singer, Track t) =>
        !string.IsNullOrWhiteSpace(singer) && _singers.TryGetValue(SingerKey(singer), out var i) && i.KeyByTrack.TryGetValue(t.Id, out var k) ? k : 0;

    /// <summary>Quante canzoni ha già in coda / cantato stasera questo cantante.</summary>
    public int SingerCountTonight(string singer) =>
        string.IsNullOrWhiteSpace(singer) ? 0 : Queue.Count(q => SingerKey(q.Singer) == SingerKey(singer)) + _sungTonight.GetValueOrDefault(SingerKey(singer));
    private readonly Dictionary<string, int> _sungTonight = new();

    /// <summary>Chiamato quando un brano parte davvero (dopo 10 s): memoria del cantante e tonalità.</summary>
    private void RememberSinger(DeckViewModel deck, Track track)
    {
        if (string.IsNullOrWhiteSpace(deck.Singer)) return;
        var key = SingerKey(deck.Singer);
        if (!_singers.TryGetValue(key, out var info)) _singers[key] = info = new SingerInfo();
        info.LastSeenUtc = DateTime.UtcNow; info.Songs++;
        info.KeyByTrack[track.Id] = deck.KeyShift;
        _sungTonight[key] = _sungTonight.GetValueOrDefault(key) + 1;
        SaveSingers();
        RefreshKnownSingers();
    }

    /// <summary>
    /// Rotazione equa: la coda viene riordinata a "giri" — la prima canzone di ogni cantante, poi la seconda di ognuno, ecc.
    /// Chi ha già cantato stasera parte dal giro successivo, così un nuovo arrivato non aspetta dietro a chi ha già fatto tre pezzi.
    /// </summary>
    public void ApplyRotation()
    {
        if (!RotationOn || Queue.Count < 2) return;
        var rounds = new Dictionary<string, int>();
        var keyed = new List<(QueueEntry e, int round, int idx)>();
        for (int i = 0; i < Queue.Count; i++)
        {
            var e = Queue[i];
            var k = SingerKey(e.Singer);
            int r = rounds.GetValueOrDefault(k) + (k.Length > 0 ? _sungTonight.GetValueOrDefault(k) : 0);
            rounds[k] = rounds.GetValueOrDefault(k) + 1;
            keyed.Add((e, r, i));
        }
        var ordered = keyed.OrderBy(x => x.round).ThenBy(x => x.idx).Select(x => x.e).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int cur = Queue.IndexOf(ordered[i]);
            if (cur != i) Queue.Move(cur, i);
        }
    }

    [RelayCommand] private void ToggleRotation() { RotationOn = !RotationOn; if (RotationOn) ApplyRotation(); }
    partial void OnRotationOnChanged(bool value) { Settings.RotationOn = value; if (value) ApplyRotation(); }

    // ---------------------------------------------------------------- musica di riempimento

    [ObservableProperty] private bool _fillMusicOn;
    [ObservableProperty] private Playlist? _fillPlaylist;
    [ObservableProperty] private double _fillVolume = 0.7;
    [ObservableProperty] private string _fillStatus = "";
    private DeckViewModel? _fillDeck;
    private DateTime _lastFillTry;
    public bool IsFillDeck(DeckViewModel d) => _fillDeck == d;

    partial void OnFillMusicOnChanged(bool value) { Settings.FillMusicOn = value; if (!value) StopFill(fade: true); }
    partial void OnFillVolumeChanged(double value) { Settings.FillVolume = value; if (_fillDeck != null) _fillDeck.GainDb = 20 * Math.Log10(Math.Max(0.05, value)); }

    /// <summary>Playlist di riempimento scelta nelle impostazioni (ricaricata dopo il caricamento delle playlist).</summary>
    private void ResolveFillPlaylist()
    {
        FillPlaylist = Playlists.FirstOrDefault(p => p.Id == Settings.FillPlaylistId);
    }

    private Track? PickFillTrack()
    {
        IEnumerable<Track> pool = FillPlaylist != null
            ? FillPlaylist.TrackIds.Select(Library.FindById).Where(t => t != null)!
            : Tracks.Where(t => !t.IsKaraoke);
        var cands = pool.Where(t => t != null && !t.Missing && !t.IsKaraoke && t.HiddenDuplicateOf == null && !IsCryptic(t)).ToList();
        if (cands.Count == 0) return null;
        // il meno suonato di recente, con un po' di caso
        return cands.OrderBy(t => t.LastPlayedUtc ?? DateTime.MinValue).Take(Math.Max(1, cands.Count / 3)).OrderBy(_ => Random.Shared.Next()).First();
    }

    /// <summary>Da chiamare dal timer: se c'è silenzio fra un cantante e l'altro, parte la musica di riempimento.</summary>
    private void CheckFillMusic()
    {
        if (_fillDeck is { } fd)
        {
            var other = fd == DeckA ? DeckB : DeckA;
            if (fd.IsPlaying && other.IsPlaying && !IsMixing) { StopFill(fade: true); return; } // è partita la base: il riempimento sfuma
            if (!fd.IsPlaying) { fd.GainDb = 0; fd.IsFill = false; _fillDeck = null; }      // finito o fermato a mano
            else return;
        }
        if (!FillMusicOn || IsMixing) return;
        if (DeckA.IsPlaying || DeckB.IsPlaying) return;
        if ((DateTime.UtcNow - _lastFillTry).TotalSeconds < 3) return;
        _lastFillTry = DateTime.UtcNow;
        var t = PickFillTrack();
        if (t == null) { FillStatus = "Riempimento: nessun brano (scegli una playlist)"; return; }
        // il deck che non tiene pronta la prossima base
        var deck = !DeckA.HasTrack ? DeckA : !DeckB.HasTrack ? DeckB : (DeckA.IsKaraoke ? DeckB : DeckA);
        if (deck.HasTrack && deck.IsKaraoke && deck.Progress < 0.02 && (deck == DeckA ? DeckB : DeckA).HasTrack) return; // entrambi i deck tengono basi pronte: non tocco niente
        if (!LoadToDeck(deck, t, "", 0, confirmIfPlaying: false)) return;
        _fillDeck = deck; deck.IsFill = true;
        deck.GainDb = 20 * Math.Log10(Math.Max(0.05, FillVolume));
        _autoMixTriggeredFor = deck; // l'automix non deve mixare via il riempimento: lo fa il DJ col prossimo cantante
        Crossfader = deck == DeckB ? 1 : -1;
        deck.Deck.Play();
        FillStatus = "Riempimento: " + t.Display;
        StatusText = FillStatus;
    }

    /// <summary>Ferma il riempimento (con dissolvenza verso l'altro deck se sta suonando).</summary>
    public void StopFill(bool fade)
    {
        var d = _fillDeck; if (d == null) return;
        _fillDeck = null;
        var other = d == DeckA ? DeckB : DeckA;
        if (fade && d.IsPlaying && other.IsPlaying) { StartCrossfade(other == DeckB ? 1 : -1); _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(0.5, CrossfadeSeconds) + 0.3)).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(() => { if (!IsFillDeck(d)) { d.Deck.Pause(); d.GainDb = 0; d.IsFill = false; } })); }
        else { d.Deck.Pause(); d.GainDb = 0; d.IsFill = false; }
        FillStatus = "";
    }

    [RelayCommand] private void ToggleFill() => FillMusicOn = !FillMusicOn;

    // ---------------------------------------------------------------- prenotazioni dal pubblico

    [ObservableProperty] private bool _publicRequestsOn = true;
    public ObservableCollection<SongRequest> Requests { get; } = new();
    public string PublicPageUrl => $"{CloudBaseUrl}/canta?s={Remote.SessionId}";
    public bool HasRequests => Requests.Count > 0;

    partial void OnPublicRequestsOnChanged(bool value) => Settings.PublicRequestsOn = value;

    private void AddRequest(RemoteCommand c)
    {
        if (!PublicRequestsOn) return;
        var t = c.Id != null ? Library.FindById(c.Id) : null;
        var req = new SongRequest { Track = t, TitleText = c.Title ?? "", Singer = (c.Singer ?? "").Trim(), Note = (c.Note ?? "").Trim() };
        if (t == null && string.IsNullOrWhiteSpace(req.TitleText)) return;
        Requests.Add(req);
        OnPropertyChanged(nameof(HasRequests));
        StatusText = "Prenotazione dal pubblico: " + req.Display;
    }

    [RelayCommand]
    private void AcceptRequest(SongRequest? r)
    {
        if (r == null) return;
        Requests.Remove(r); OnPropertyChanged(nameof(HasRequests));
        if (r.Track == null) { StatusText = $"Richiesta senza brano in libreria: {r.TitleText}"; return; }
        if (!string.IsNullOrWhiteSpace(r.Note)) { r.Track.Dedication = r.Note; r.Track.DedicationTitle = string.IsNullOrWhiteSpace(r.Singer) ? "Dedica" : $"Da {r.Singer}"; }
        AddToQueue(r.Track, r.Singer, RememberedKey(r.Singer, r.Track));
    }

    [RelayCommand]
    private void RejectRequest(SongRequest? r)
    {
        if (r == null) return;
        Requests.Remove(r); OnPropertyChanged(nameof(HasRequests));
    }

    [RelayCommand]
    private void ShowPublicQr()
    {
        if (!RequireLicense("Prenotazioni dal pubblico")) return;
        if (!Remote.IsRunning) Remote.Start();
        ShowQrOnProjector(PublicPageUrl, "Inquadra e prenota la tua canzone", 25);
        StatusText = "QR prenotazioni sul proiettore: " + PublicPageUrl;
    }
}
