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

        var screens = System.Windows.Forms.Screen.AllScreens;
        ScreenCombo.ItemsSource = screens.Select((s, i) => $"Schermo {i + 1}: {s.Bounds.Width}×{s.Bounds.Height}" + (s.Primary ? " (principale)" : "")).ToList();
        ScreenCombo.SelectedIndex = vm.Settings.ProjectorScreenIndex < 0
            ? Math.Max(0, Array.FindIndex(screens, s => !s.Primary))
            : Math.Clamp(vm.Settings.ProjectorScreenIndex, 0, screens.Length - 1);

        IdleTitleBox.Text = vm.IdleTitle;
        IdleSubtitleBox.Text = vm.IdleSubtitle;
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
        foreach (var (id, label, _) in MidiActions.All)
            _midiRows.Add(new MidiRow { Id = id, Label = label, Binding = vm.Midi.KeyFor(id)?.ToString() ?? "—", KeyBinding = KeyboardService.Pretty(vm.Keys.GestureFor(id)) });
        MidiList.ItemsSource = _midiRows;
        vm.Midi.MessageReceived += OnMidiMessage;
        JamendoBox.Text = vm.Settings.JamendoClientId ?? "";
        FillPlugins();
        Closed += (_, _) => { vm.Midi.MessageReceived -= OnMidiMessage; vm.Midi.CancelLearn(); vm.SaveSettings(); };
        PreviewKeyDown += KeyLearn_PreviewKeyDown;
    }

    private void OnMidiMessage(MidiKey key, int value) => MidiActivity.Text = $"Ricevuto: {key} = {value}";

    private void MidiConnect_Click(object sender, RoutedEventArgs e)
    {
        var name = MidiCombo.SelectedItem as string;
        _vm.ApplyMidiDevice(name == "(nessuno)" ? null : name);
        MidiActivity.Text = _vm.Midi.IsOpen ? "Collegato: " + _vm.Midi.DeviceName : "Non collegato";
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
            _vm.Midi.SetMapping(row.Id, key);
            row.Binding = key.ToString();
            row.LearnLabel = "Impara MIDI";
            _learning = null;
            // se lo stesso controllo era assegnato altrove, aggiorna la riga
            foreach (var r in _midiRows.Where(r => r != row && r.Binding == key.ToString())) r.Binding = "—";
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Plugin VOXA", Filter = "Plugin (zip o dll)|*.zip;*.dll" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var dest = PluginManager.Install(dlg.FileName);
            InfoLabel.Text = "Plugin copiato in " + dest + " — riavvia VOXA per attivarlo";
        }
        catch (Exception ex) { InfoLabel.Text = "Errore: " + ex.Message; }
    }

    private async void PluginMaintenance_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PluginRow row || row.Plugin?.Plugin?.Maintenance is not { } m) return;
        try
        {
            await m(new Progress<VOXA.Plugins.ImportProgress>(p => InfoLabel.Text = p.Message), CancellationToken.None);
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
        _vm.ApplyCdgOffset((int)OffsetSlider.Value);
        _vm.Settings.GlassEffect = GlassBox.IsChecked == true;

        var newDevice = DeviceCombo.SelectedValue as string;
        if (string.IsNullOrEmpty(newDevice)) newDevice = null;
        if (newDevice != _vm.Settings.OutputDeviceId) _vm.ApplyAudioDevice(newDevice);

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
