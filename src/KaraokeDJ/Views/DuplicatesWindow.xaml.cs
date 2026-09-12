using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Models;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class DuplicatesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly ObservableCollection<GroupRow> _rows = new();

    public sealed partial class ItemRow : ObservableObject
    {
        [ObservableProperty] private bool _selected = true;
        public Track Track { get; init; } = new();
        public string Text => $"{Track.Display}  ·  {Track.FilePath}  ({Track.FileSize / 1048576.0:0.0} MB, {Track.DurationLabel})";
    }

    public sealed class GroupRow
    {
        public DuplicateGroup Group { get; init; } = new();
        public string Reason => Group.Reason.ToUpperInvariant();
        public string KeepText => $"{Group.Keep.Display}  ·  {Group.Keep.FilePath}  ({Group.Keep.FileSize / 1048576.0:0.0} MB)";
        public string Saved => $"{Group.BytesSaved / 1048576.0:0.0} MB recuperabili";
        public List<ItemRow> Items { get; init; } = new();
    }

    public DuplicatesWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        List.ItemsSource = _rows;
    }

    private CancellationTokenSource? _cts;

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanBtn.IsEnabled = false; DeleteBtn.IsEnabled = false; CancelBtn.Visibility = Visibility.Visible;
        Bar.Visibility = Visibility.Visible; Bar.IsIndeterminate = true; Bar.Value = 0;
        _rows.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<DuplicateProgress>(p => { Status.Text = p.Message; if (p.Percent > 0) { Bar.IsIndeterminate = false; Bar.Value = p.Percent; } });
        var tracks = _vm.Tracks.ToList();
        try
        {
            var groups = await Task.Run(() => DuplicateFinder.Find(tracks, progress, _cts.Token));
            foreach (var g in groups)
                _rows.Add(new GroupRow { Group = g, Items = g.Remove.Select(t => new ItemRow { Track = t }).ToList() });
            long bytes = groups.Sum(g => g.BytesSaved);
            Summary.Text = groups.Count == 0 ? "Nessun doppione trovato."
                : $"{groups.Count} gruppi, {groups.Sum(g => g.Remove.Count)} file eliminabili, {bytes / 1048576.0:0} MB recuperabili. Controlla le spunte e conferma.";
            Status.Text = "";
            DeleteBtn.IsEnabled = groups.Count > 0;
        }
        catch (OperationCanceledException) { Status.Text = "Ricerca annullata"; }
        catch (Exception ex) { Status.Text = "Errore: " + ex.Message; }
        finally { ScanBtn.IsEnabled = true; CancelBtn.Visibility = Visibility.Collapsed; Bar.Visibility = Visibility.Collapsed; }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _rows.SelectMany(r => r.Items.Where(i => i.Selected).Select(i => (row: r, item: i))).ToList();
        if (selected.Count == 0) return;
        if (MessageBox.Show($"Spostare nel Cestino {selected.Count} file? (recuperabili dal Cestino di Windows)", "Pulizia doppioni",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        int ok = 0, fail = 0;
        foreach (var (row, item) in selected)
        {
            var t = item.Track;
            if (_vm.DeckA.Track == t || _vm.DeckB.Track == t) { fail++; continue; } // in uso su un deck
            if (!DuplicateFinder.RecycleFile(t.FilePath)) { fail++; continue; }
            _vm.RemoveTrackFromLibrary(t, replaceWith: row.Group.Keep);
            ok++;
        }
        Status.Text = $"Spostati nel Cestino: {ok}" + (fail > 0 ? $", non eliminati: {fail} (in uso o errore)" : "");
        Scan_Click(sender, e);
    }
}
