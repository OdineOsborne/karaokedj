using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace KaraokeDJ.ViewModels;

/// <summary>
/// Cose che vede il pubblico e che non c'entrano con l'audio: la striscia dei messaggi sul proiettore
/// e l'applausometro. Regola comune: non devono poter fermare, rallentare o far partire la musica.
/// </summary>
public partial class MainViewModel
{
    // ---------------------------------------------------------------- striscia messaggi (ticker)

    /// <summary>Striscia scorrevole in basso sul proiettore: saluti, offerte del locale, avvisi.</summary>
    [ObservableProperty] private bool _tickerOn;
    /// <summary>Un messaggio per riga. Sul proiettore scorrono uno dietro l'altro separati da un punto.</summary>
    [ObservableProperty] private string _tickerText = "";

    partial void OnTickerOnChanged(bool value) { Settings.TickerOn = value; NotifyTicker(); }
    partial void OnTickerTextChanged(string value) { Settings.TickerText = value; NotifyTicker(); }

    private void NotifyTicker() { OnPropertyChanged(nameof(TickerLine)); OnPropertyChanged(nameof(TickerVisible)); }

    public bool TickerVisible => TickerOn && TickerLine.Length > 0;

    /// <summary>Le righe scritte dal DJ in una riga sola, con un margine finale perché il giro non attacchi subito.</summary>
    public string TickerLine
    {
        get
        {
            var parts = (TickerText ?? "").Split('\n', ';')
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            return parts.Length == 0 ? "" : string.Join("      ·      ", parts) + "          ";
        }
    }

    [RelayCommand] private void ToggleTicker() => TickerOn = !TickerOn;

    // ---------------------------------------------------------------- registrazione della serata

    [ObservableProperty] private bool _recording;
    /// <summary>"REC 1:23:45 · 780 MB" mentre registra, altrimenti l'esito dell'ultima registrazione.</summary>
    [ObservableProperty] private string _recordingStatus = "";

    /// <summary>Dove finiscono i file: %AppData%\KaraokeDJ\registrazioni.</summary>
    public static string RecordingsDir => Path.Combine(Services.AppPaths.Root, "registrazioni");

    /// <summary>
    /// Avvia/ferma la registrazione del mix. Non c'entra con FERMA TUTTO: se fermi la musica la registrazione
    /// continua (e registra il silenzio), perché fermarla per sbaglio vorrebbe dire perdere la serata.
    /// </summary>
    [RelayCommand]
    public void ToggleRecording()
    {
        var rec = Engine.Recorder;
        if (rec.IsRecording)
        {
            var path = rec.Stop();
            Recording = false;
            RecordingStatus = path == null ? "" : $"Registrato {Format(rec.Seconds)} in {Path.GetFileName(path)}";
            StatusText = path == null ? "Registrazione non attiva" :
                $"Registrazione salvata: {path} ({rec.Seconds / 60:0} min" + (rec.Dropped > 0 ? $", {rec.Dropped} campioni persi" : "") + ")";
            return;
        }
        try
        {
            var name = DateTime.Now.ToString("yyyy-MM-dd HH-mm") + ".wav";
            rec.Start(Path.Combine(RecordingsDir, name));
            Recording = true;
            RecordingStatus = "REC 0:00";
            StatusText = "Registrazione avviata: " + Path.Combine(RecordingsDir, name);
        }
        catch (Exception ex)
        {
            Recording = false;
            StatusText = "Non riesco a registrare: " + ex.Message;
        }
    }

    private static string Format(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    /// <summary>Aggiorna la scritta REC (chiamata dal timer principale, una volta al secondo basta e avanza).</summary>
    private void TickRecording()
    {
        var rec = Engine.Recorder;
        if (Recording && !rec.IsRecording)      // fermata da sola: disco pieno o errore
        {
            Recording = false;
            RecordingStatus = rec.LastError == null ? "Registrazione fermata" : "Registrazione interrotta";
            return;
        }
        if (!rec.IsRecording) return;
        RecordingStatus = $"REC {Format(rec.Seconds)} · {rec.BytesWritten / 1024 / 1024} MB" + (rec.Dropped > 0 ? " ⚠" : "");
    }

    // ---------------------------------------------------------------- applausometro

    /// <summary>Misura in corso o risultato ancora a schermo.</summary>
    [ObservableProperty] private bool _applauseVisible;
    /// <summary>Livello istantaneo 0…100 (la barra che si muove mentre la gente applaude).</summary>
    [ObservableProperty] private double _applauseValue;
    /// <summary>Punteggio: il massimo raggiunto, 0…100.</summary>
    [ObservableProperty] private int _applauseScore;
    [ObservableProperty] private string _applauseCaption = "";


    private DateTime _applauseStart;
    private bool _applauseMeasuring, _applauseMicWasOff;
    /// <summary>Quanto dura la misura e quanto resta a schermo il risultato.</summary>
    private const double ApplauseSeconds = 7, ApplauseHoldSeconds = 6;

    /// <summary>
    /// Parte l'applausometro: sette secondi di ascolto dal microfono, poi il punteggio resta a schermo.
    /// Se il microfono è spento lo accende per la misura e lo rimette com'era: senza microfono non c'è niente da misurare.
    /// </summary>
    [RelayCommand]
    public void StartApplause()
    {
        if (ApplauseVisible) { StopApplause(); return; }
        _applauseMicWasOff = !MicOn;
        if (_applauseMicWasOff) MicOn = true;
        if (!MicOn)
        {
            StatusText = "Applausometro: serve il microfono, ma non si riesce ad accenderlo (vedi Impostazioni → Audio)";
            _applauseMicWasOff = false;
            return;
        }
        _applauseStart = DateTime.UtcNow;
        _applauseMeasuring = true;
        ApplauseValue = 0; ApplauseScore = 0;
        ApplauseCaption = "FATE RUMORE!";
        ApplauseVisible = true;
        StatusText = "Applausometro: sto ascoltando…";
    }

    /// <summary>Toglie subito l'applausometro dallo schermo (e rimette il microfono com'era).</summary>
    public void StopApplause()
    {
        _applauseMeasuring = false;
        ApplauseVisible = false;
        if (_applauseMicWasOff) { MicOn = false; _applauseMicWasOff = false; }
    }

    /// <summary>Chiamata dal timer principale: aggiorna barra e punteggio. Non tocca mai l'audio.</summary>
    private void TickApplause()
    {
        if (!ApplauseVisible) return;
        double elapsed = (DateTime.UtcNow - _applauseStart).TotalSeconds;
        if (_applauseMeasuring)
        {
            // stessa scala del VU del microfono, così quello che vedi sul portatile e quello che vede il pubblico coincidono
            double v = Views.LevelMeter.ToScale(Engine.Mic.Peak) * 100;
            ApplauseValue = v > ApplauseValue ? v : Math.Max(0, ApplauseValue - 120 * 0.033);   // scende piano
            if (ApplauseValue > ApplauseScore) ApplauseScore = (int)Math.Round(ApplauseValue);
            if (elapsed >= ApplauseSeconds)
            {
                _applauseMeasuring = false;
                ApplauseCaption = ApplauseVerdict(ApplauseScore);
                if (_applauseMicWasOff) { MicOn = false; _applauseMicWasOff = false; }
                StatusText = $"Applausometro: {ApplauseScore}/100 — {ApplauseCaption}";
            }
        }
        else if (elapsed >= ApplauseSeconds + ApplauseHoldSeconds) StopApplause();
    }

    private static string ApplauseVerdict(int score) => score switch
    {
        >= 90 => "DA PELLE D'OCA!",
        >= 75 => "GRANDE!",
        >= 55 => "BRAVO!",
        >= 35 => "SI PUÒ FARE MEGLIO…",
        _ => "SVEGLIA, GENTE!",
    };
}
