using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Audio;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly List<string> _folders;
    private readonly ObservableCollection<MidiRow> _midiRows = new();
    private MidiRow? _learning;

    private sealed partial class MidiRow : ObservableObject
    {
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        [ObservableProperty] private string _binding = "—";
        [ObservableProperty] private string _learnLabel = "Impara MIDI";
        [ObservableProperty] private string _keyBinding = "—";
        [ObservableProperty] private string _keyLearnLabel = "Impara tasto";
        public bool IsContinuous { get; init; }
        [ObservableProperty] private bool _invert;
        [ObservableProperty] private bool _relative;
        /// <summary>inverti/encoder hanno senso solo per fader e manopole già mappati.</summary>
        public Visibility FlagsVisible => IsContinuous && Binding != "—" ? Visibility.Visible : Visibility.Collapsed;
        partial void OnBindingChanged(string value) => OnPropertyChanged(nameof(FlagsVisible));
    }

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        _folders = new List<string>(vm.Settings.LibraryFolders);

        var devices = AudioEngine.ListOutputDevices();
        DeviceCombo.ItemsSource = devices;
        DeviceCombo.SelectedValue = devices.Any(d => d.Id == (vm.Settings.OutputDeviceId ?? "")) ? (vm.Settings.OutputDeviceId ?? "") : "";
        var cueDevices = new List<AudioDevice> { new("", "(nessuna: niente pre-ascolto)") };
        cueDevices.Add(new(AudioEngine.CueOnMainId, "Canali 3-4 della scheda principale (console con scheda audio integrata)"));
        cueDevices.AddRange(devices.Skip(1));
        CueCombo.ItemsSource = cueDevices;
        CueCombo.SelectedValue = cueDevices.Any(d => d.Id == (vm.Settings.CueDeviceId ?? "")) ? (vm.Settings.CueDeviceId ?? "") : "";
        UsageBox.IsChecked = vm.Settings.UsageStatsOptIn;
                ScaleCombo.ItemsSource = ScaleOptions;
        ScaleCombo.SelectedItem = ScaleOptions.FirstOrDefault(o => Math.Abs(o.Value - vm.Settings.UiScale) < 0.001) ?? ScaleOptions[0];
        ScaleNow.Text = vm.UiScaleLabel;
                var mics = MicInput.ListInputDevices();
        MicCombo.ItemsSource = mics;
        MicCombo.SelectedValue = mics.Any(d => d.Id == (vm.Settings.MicDeviceId ?? "")) ? (vm.Settings.MicDeviceId ?? "") : "";

        var screens = System.Windows.Forms.Screen.AllScreens;
        ScreenCombo.ItemsSource = screens.Select((s, i) => $"Schermo {i + 1}: {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (principale)" : "")).ToList();
        ScreenCombo.SelectedIndex = vm.Settings.ProjectorScreenIndex < 0
            ? Math.Max(0, Array.FindIndex(screens, s => !s.Primary))
            : Math.Clamp(vm.Settings.ProjectorScreenIndex, 0, screens.Length - 1);

        IdleTitleBox.Text = vm.IdleTitle;
        IdleSubtitleBox.Text = vm.IdleSubtitle;
        TickerBox.IsChecked = vm.TickerOn;
        TickerTextBox.Text = vm.TickerText;
        OffsetSlider.Value = vm.Settings.CdgOffsetMs;
        GlassBox.IsChecked = vm.Settings.GlassEffect;
        OffsetLabel.Text = vm.Settings.CdgOffsetMs + " ms";
        FoldersList.ItemsSource = _folders;
        InfoLabel.Text = "Dati in: " + AppPaths.Root;
        ApiKeyBox.Password = Secret.Unprotect(vm.Settings.AnthropicApiKeyProtected) ?? "";
        DownloadFolderBox.Text = AppPaths.DownloadsDir;

        // MIDI
        var midiDevices = new List<string> { "(nessuno)" };
        midiDevices.AddRange(MidiService.ListDevices());
        MidiCombo.ItemsSource = midiDevices;
        MidiCombo.SelectedItem = midiDevices.Contains(vm.Settings.MidiDeviceName ?? "") ? vm.Settings.MidiDeviceName : "(nessuno)";
        foreach (var (id, label, cont) in MidiActions.All)
            _midiRows.Add(new MidiRow { Id = id, Label = label, IsContinuous = cont, Binding = BindingLabel(vm, id), Invert = vm.Midi.IsInverted(id), Relative = vm.Midi.IsRelative(id), KeyBinding = KeyboardService.Pretty(vm.Keys.GestureFor(id)) });
        MidiList.ItemsSource = _midiRows;
        SupportedList.Text = "Console riconosciute da sole (plug & play): " + MainViewModel.SupportedControllers + ". Altre console: scegli la porta qui sopra e usa Impara.";
        vm.Midi.MessageReceived += OnMidiMessage;
        JamendoBox.Text = vm.Settings.JamendoClientId ?? "";
        FillPlugins();
        Closed += (_, _) => { vm.Midi.MessageReceived -= OnMidiMessage; vm.Midi.CancelLearn(); vm.SaveSettings(); };
        PreviewKeyDown += KeyLearn_PreviewKeyDown;
    }

    private sealed record ScaleOption(string Name, double Value) { public override string ToString() => Name; }
    private static readonly ScaleOption[] ScaleOptions =
    {
        new("Automatica (si adatta alla finestra)", 0), new("100 %", 1), new("90 %", 0.9), new("80 %", 0.8), new("70 %", 0.7), new("110 %", 1.1), new("125 %", 1.25),
    };

    private void ScaleCombo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScaleCombo.SelectedItem is not ScaleOption o || _vm == null) return;
        _vm.Settings.UiScale = o.Value;
        if (Owner is MainWindow mw) { mw.ApplyUiScale(); ScaleNow.Text = _vm.UiScaleLabel; }
        else if (Application.Current?.MainWindow is MainWindow mw2) { mw2.ApplyUiScale(); ScaleNow.Text = _vm.UiScaleLabel; }
    }

    private void UsageBox_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.UsageStatsOptIn = UsageBox.IsChecked == true;
        _vm.Settings.UsageStatsAsked = true;
        _vm.SaveSettings();
        if (!_vm.Settings.UsageStatsOptIn) UsageStats.Clear();
    }

    /// <summary>Trasparenza: mostra esattamente le righe che partiranno.</summary>
    private void UsageShow_Click(object sender, RoutedEventArgs e)
    {
        var rows = UsageStats.Peek();
        var text = rows.Count == 0
            ? "Non c'è niente in attesa."
            : string.Join("\n", rows.TakeLast(40).Select(r => $"{r.Day}  {r.FromArtist} - {r.FromTitle}  →  {r.ToArtist} - {r.ToTitle}  ({r.Kind}{(r.Completed ? ", fino in fondo" : "")})"));
        MessageBox.Show(this,
            (rows.Count > 40 ? $"(ultimi 40 di {rows.Count})\n\n" : "") + text +
            "\n\nOltre a queste righe parte solo: versione dell'app e un codice casuale di questa installazione.",
            "Dati in attesa di invio", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UsageClear_Click(object sender, RoutedEventArgs e)
    {
        UsageStats.Clear();
        MessageBox.Show(this, "Dati in attesa cancellati.", "Statistiche d'uso", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnMidiMessage(MidiKey key, int value)
    {
        // dice anche cosa fa quel controllo: così si capisce al volo cosa è mappato male
        var a = _vm.Midi.ActionFor(key);
        MidiActivity.Text = $"Ricevuto: {key} = {value}" + (a != null ? " → " + AppActions.LabelOf(a) : " → (niente)");
    }

    private void MidiFlags_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.Tag is not MidiRow row) return;
        _vm.Midi.SetFlags(row.Id, row.Invert, row.Relative);
    }

    private void MidiImport_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Importa mappatura console", Filter = "Mappature (*.json;*.xml;*.djayMidiMapping)|*.json;*.xml;*.djayMidiMapping|Tutti i file|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        ControllerPreset preset; string report;
        try { (preset, report) = _vm.ImportControllerFile(dlg.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Importazione non riuscita", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        // conferma: nome, e la stringa che riconosce la porta (proposta: la porta collegata adesso)
        var connected = _vm.Midi.DeviceName;
        var win = new Window { Title = "Importa mappatura", Owner = this, Width = 520, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, ResizeMode = ResizeMode.NoResize };
        var name = new TextBox { Text = preset.Name, Margin = new Thickness(0, 2, 0, 10) };
        var match = new TextBox { Text = connected ?? string.Join("; ", preset.Match), Margin = new Thickness(0, 2, 0, 4) };
        var ok = new Button { Content = "Salva e usa", IsDefault = true, MinWidth = 110, Margin = new Thickness(0, 12, 6, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = report + " · " + preset.Source, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Opacity = 0.8 });
        panel.Children.Add(new TextBlock { Text = "Nome della console" });
        panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Si attiva quando il nome della porta MIDI contiene (più valori separati da ;)" });
        panel.Children.Add(match);
        panel.Children.Add(new TextBlock { Text = connected != null ? "Porta collegata adesso: " + connected : "Nessuna console collegata: collegala e leggi il nome in «Dispositivo MIDI».", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 11 });
        panel.Children.Add(ok);
        win.Content = panel;
        ok.Click += (_, _) => { win.DialogResult = true; win.Close(); };
        if (win.ShowDialog() != true) return;
        preset.Name = name.Text.Trim();
        preset.Id = preset.Name;
        preset.Match = match.Text.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (preset.Match.Count == 0) preset.Match.Add(preset.Name);
        var file = _vm.SaveUserPreset(preset);
        SupportedList.Text = "Console riconosciute da sole (plug & play): " + MainViewModel.SupportedControllers + ". Altre console: scegli la porta qui sopra e usa Impara.";
        MidiActivity.Text = "Salvata: " + file;
        RefreshMidiRows();
    }

    private void MidiExport_Click(object sender, RoutedEventArgs e)
    {
        ControllerPreset preset;
        try { preset = _vm.ExportCurrentMapping(); }
        catch (Exception ex) { MidiActivity.Text = ex.Message; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Esporta mappatura", Filter = "Mappatura Mixfonia (*.json)|*.json", FileName = preset.Id + ".json" };
        if (dlg.ShowDialog(this) != true) return;
        var json = System.Text.Json.JsonSerializer.Serialize(preset, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault });
        File.WriteAllText(dlg.FileName, json);
        MidiActivity.Text = $"Esportata: {dlg.FileName} ({preset.Mappings.Count} controlli). Mandacela: la aggiungiamo per tutti.";
    }

    private void MidiFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(ControllerPresets.UserDir);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", ControllerPresets.UserDir) { UseShellExecute = true }); } catch { }
    }

    private void RefreshMidiRows()
    {
        foreach (var r in _midiRows) { r.Binding = BindingLabel(_vm, r.Id); r.Invert = _vm.Midi.IsInverted(r.Id); r.Relative = _vm.Midi.IsRelative(r.Id); }
    }

    private void MidiConnect_Click(object sender, RoutedEventArgs e)
    {
        var name = MidiCombo.SelectedItem as string;
        _vm.ApplyMidiDevice(name == "(nessuno)" ? null : name);
        MidiActivity.Text = _vm.Midi.IsOpen ? "Collegato: " + _vm.Midi.DeviceName : "Non collegato";
    }

    /// <summary>Etichetta del controllo assegnato, con "SHIFT +" davanti se vale solo col tasto shift.</summary>
    private static string BindingLabel(ViewModels.MainViewModel vm, string id)
    {
        var k = vm.Midi.KeyFor(id);
        return k == null ? "—" : (vm.Midi.IsShiftAction(id) ? "SHIFT + " : "") + k;
    }

    private void MidiLearn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MidiRow row) return;
        if (!_vm.Midi.IsOpen) { MidiActivity.Text = "Prima collega un dispositivo MIDI"; return; }
        if (_learning != null) _learning.LearnLabel = "Impara MIDI";
        _learning = row;
        row.LearnLabel = "Muovi…";
        _vm.Midi.BeginLearn(key =>
        {
            // imparato tenendo premuto SHIFT? allora vale solo con SHIFT (le console hanno due comandi per tasto)
            bool shift = _vm.Midi.LastLearnShifted;
            _vm.Midi.SetMapping(row.Id, key, shift);
            row.Binding = (shift ? "SHIFT + " : "") + key;
            row.LearnLabel = "Impara MIDI";
            _learning = null;
            // se lo stesso controllo era assegnato altrove, aggiorna la riga
            foreach (var r in _midiRows.Where(r => r != row && r.Binding == row.Binding && r.Id != row.Id)) r.Binding = "—";
        });
    }

    private MidiRow? _keyLearning;

    private void KeyLearn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MidiRow row) return;
        if (_keyLearning != null) _keyLearning.KeyLearnLabel = "Impara tasto";
        _keyLearning = row;
        row.KeyLearnLabel = "Premi…";
        Focus();
    }

    private void KeyClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MidiRow row) return;
        _vm.Keys.Clear(row.Id);
        row.KeyBinding = "—";
    }

    /// <summary>Durante «Impara tasto» il prossimo tasto premuto diventa la scorciatoia della riga.</summary>
    private void KeyLearn_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_keyLearning == null) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { _keyLearning.KeyLearnLabel = "Impara tasto"; _keyLearning = null; return; }
        var g = KeyboardService.GestureText(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (g == null) return;
        var row = _keyLearning; _keyLearning = null;
        _vm.Keys.Set(row.Id, g);
        row.KeyBinding = KeyboardService.Pretty(g);
        row.KeyLearnLabel = "Impara tasto";
        foreach (var r in _midiRows.Where(r => r != row && r.KeyBinding == row.KeyBinding)) r.KeyBinding = "—";
    }

    private void MidiClear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MidiRow row) return;
        _vm.Midi.ClearMapping(row.Id);
        row.Binding = "—";
    }

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OffsetLabel != null) OffsetLabel.Text = (int)e.NewValue + " ms";
    }

    private void BrowseDownload_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Cartella per i download", UseDescriptionForTitle = true, SelectedPath = DownloadFolderBox.Text };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) DownloadFolderBox.Text = dlg.SelectedPath;
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog { Description = "Cartella con musica / basi karaoke", UseDescriptionForTitle = true };
        if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!_folders.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _folders.Add(dlg.SelectedPath);
            FoldersList.Items.Refresh();
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is string f)
        {
            _folders.Remove(f);
            FoldersList.Items.Refresh();
        }
    }

    private async void InstallAi_Click(object sender, RoutedEventArgs e)
    {
        InfoLabel.Text = "Installazione motore AI in corso (vedi barra di stato)…";
        await _vm.InstallAiCommand.ExecuteAsync(null);
        InfoLabel.Text = _vm.Stems.IsReady ? "Motore AI pronto" : "Motore AI non installato";
    }

    // ------------------------------------------------------------ plugin e fonti

    private sealed class PluginRow
    {
        public string Name { get; init; } = "";
        public string Version { get; init; } = "";
        public string Description { get; init; } = "";
        public string? MaintenanceLabel { get; init; }
        public LoadedPlugin? Plugin { get; init; }
    }

    private void FillPlugins()
    {
        var rows = _vm.Plugins.Plugins.Select(p => new PluginRow
        {
            Name = p.Name, Version = p.Version, Description = p.Ok ? p.Description : "⚠ non caricato: " + p.Description,
            MaintenanceLabel = p.Plugin?.MaintenanceLabel, Plugin = p,
        }).ToList();
        if (rows.Count == 0) rows.Add(new PluginRow { Name = "Nessun plugin installato", Description = "I plugin aggiungono sorgenti di importazione o funzioni: copia la cartella del plugin in %AppData%\\KaraokeDJ\\plugins e riavvia." });
        PluginList.ItemsSource = rows;
    }

    private void Link_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
        e.Handled = true;
    }

    private void OpenPlugins_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(PluginManager.PluginsDir); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(PluginManager.PluginsDir) { UseShellExecute = true }); } catch { }
    }

    private void InstallPlugin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Plugin Mixfonia", Filter = "Plugin (zip o dll)|*.zip;*.dll" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dest = PluginManager.Install(dlg.FileName);
            InfoLabel.Text = "Plugin copiato in " + dest + " — riavvia Mixfonia per attivarlo";
        }
        catch (Exception ex) { InfoLabel.Text = "Errore: " + ex.Message; }
    }

    private async void PluginMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PluginRow row || row.Plugin?.Plugin?.Maintenance is not { } m) return;
        try
        {
            await m(new Progress<Mixfonia.Plugins.ImportProgress>(p => InfoLabel.Text = p.Message), CancellationToken.None);
            InfoLabel.Text = row.Name + ": fatto";
        }
        catch (Exception ex) { InfoLabel.Text = "Errore: " + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _vm.Settings.JamendoClientId = string.IsNullOrWhiteSpace(JamendoBox.Text) ? null : JamendoBox.Text.Trim();
        JamendoSource.ClientId = _vm.Settings.JamendoClientId;
        bool foldersChanged = !_folders.SequenceEqual(_vm.Settings.LibraryFolders);
        _vm.Settings.LibraryFolders = _folders;
        _vm.Settings.ProjectorScreenIndex = Math.Max(0, ScreenCombo.SelectedIndex);
        _vm.IdleTitle = IdleTitleBox.Text;
        _vm.IdleSubtitle = IdleSubtitleBox.Text;
        _vm.TickerOn = TickerBox.IsChecked == true;
        _vm.TickerText = TickerTextBox.Text;
        _vm.ApplyCdgOffset((int)OffsetSlider.Value);
        _vm.Settings.GlassEffect = GlassBox.IsChecked == true;

        var newDevice = DeviceCombo.SelectedValue as string;
        if (string.IsNullOrEmpty(newDevice)) newDevice = null;
        if (newDevice != _vm.Settings.OutputDeviceId) _vm.ApplyAudioDevice(newDevice);
        var cueDev = CueCombo.SelectedValue as string; if (string.IsNullOrEmpty(cueDev)) cueDev = null;
        if (cueDev != _vm.Settings.CueDeviceId) _vm.ApplyCueDevice(cueDev);
        var micDev = MicCombo.SelectedValue as string; if (string.IsNullOrEmpty(micDev)) micDev = null;
        if (micDev != _vm.Settings.MicDeviceId) { _vm.Settings.MicDeviceId = micDev; if (_vm.MicOn) _vm.ApplyMic(); }

        var midi = MidiCombo.SelectedItem as string;
        if (midi == "(nessuno)") midi = null;
        if (midi != _vm.Settings.MidiDeviceName) _vm.ApplyMidiDevice(midi);

        _vm.SetAnthropicApiKey(ApiKeyBox.Password);
        var dl = DownloadFolderBox.Text.Trim();
        if (dl.Length > 0 && !string.Equals(dl, AppPaths.DownloadsDir, StringComparison.OrdinalIgnoreCase))
        {
            try { Directory.CreateDirectory(dl); } catch { }
            _vm.Settings.DownloadFolder = dl;
            AppPaths.DownloadsDir = dl;
            if (!_folders.Contains(dl, StringComparer.OrdinalIgnoreCase)) { _folders.Add(dl); foldersChanged = true; }
            InfoLabel.Text = "Cartella download aggiornata (la cartella Suno monitorata cambia al prossimo avvio)";
        }
        _vm.SaveSettings();
        if (foldersChanged) _vm.RescanCommand.Execute(null);
        DialogResult = true;
        Close();
    }
}
