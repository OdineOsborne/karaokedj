using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
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
        [ObservableProperty] private string _learnLabel = "Impara";
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
        OffsetLabel.Text = vm.Settings.CdgOffsetMs + " ms";
        FoldersList.ItemsSource = _folders;
        InfoLabel.Text = "Dati in: " + AppPaths.Root;
        ApiKeyBox.Password = Secret.Unprotect(vm.Settings.AnthropicApiKeyProtected) ?? "";

        // MIDI
        var midiDevices = new List<string> { "(nessuno)" };
        midiDevices.AddRange(MidiService.ListDevices());
        MidiCombo.ItemsSource = midiDevices;
        MidiCombo.SelectedItem = midiDevices.Contains(vm.Settings.MidiDeviceName ?? "") ? vm.Settings.MidiDeviceName : "(nessuno)";
        foreach (var (id, label, _) in MidiActions.All)
            _midiRows.Add(new MidiRow { Id = id, Label = label, Binding = vm.Midi.KeyFor(id)?.ToString() ?? "—" });
        MidiList.ItemsSource = _midiRows;
        vm.Midi.MessageReceived += OnMidiMessage;
        Closed += (_, _) => { vm.Midi.MessageReceived -= OnMidiMessage; vm.Midi.CancelLearn(); };
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
        if (_learning != null) _learning.LearnLabel = "Impara";
        _learning = row;
        row.LearnLabel = "Muovi…";
        _vm.Midi.BeginLearn(key =>
        {
            _vm.Midi.SetMapping(row.Id, key);
            row.Binding = key.ToString();
            row.LearnLabel = "Impara";
            _learning = null;
            // se lo stesso controllo era assegnato altrove, aggiorna la riga
            foreach (var r in _midiRows.Where(r => r != row && r.Binding == key.ToString())) r.Binding = "—";
        });
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

    private async void UpdateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        InfoLabel.Text = "Aggiornamento yt-dlp…";
        try
        {
            await _vm.Downloader.UpdateYtDlpAsync(new Progress<DownloadStatus>(s => InfoLabel.Text = s.Message), CancellationToken.None);
            InfoLabel.Text = "yt-dlp aggiornato";
        }
        catch (Exception ex) { InfoLabel.Text = "Errore: " + ex.Message; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        bool foldersChanged = !_folders.SequenceEqual(_vm.Settings.LibraryFolders);
        _vm.Settings.LibraryFolders = _folders;
        _vm.Settings.ProjectorScreenIndex = Math.Max(0, ScreenCombo.SelectedIndex);
        _vm.IdleTitle = IdleTitleBox.Text;
        _vm.IdleSubtitle = IdleSubtitleBox.Text;
        _vm.ApplyCdgOffset((int)OffsetSlider.Value);

        var newDevice = DeviceCombo.SelectedValue as string;
        if (string.IsNullOrEmpty(newDevice)) newDevice = null;
        if (newDevice != _vm.Settings.OutputDeviceId) _vm.ApplyAudioDevice(newDevice);

        var midi = MidiCombo.SelectedItem as string;
        if (midi == "(nessuno)") midi = null;
        if (midi != _vm.Settings.MidiDeviceName) _vm.ApplyMidiDevice(midi);

        _vm.SetAnthropicApiKey(ApiKeyBox.Password);
        _vm.SaveSettings();
        if (foldersChanged) _vm.RescanCommand.Execute(null);
        DialogResult = true;
        Close();
    }
}
