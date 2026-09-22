using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

/// <summary>Compilazione assistita del programma musicale SIAE (borderò) dai brani riprodotti in serata.</summary>
public partial class BorderoWindow : Window
{
    public sealed partial class Row : ObservableObject
    {
        public int N { get; set; }
        public string Ora { get; set; } = "";
        [ObservableProperty] private string _titolo = "";
        [ObservableProperty] private string _autori = "";
        [ObservableProperty] private string _interprete = "";
        public string Durata { get; set; } = "";
        [ObservableProperty] private int _volte = 1;
        public string TrackId { get; set; } = "";
        public bool MissingAuthors => string.IsNullOrWhiteSpace(Autori);
        partial void OnAutoriChanged(string value) => OnPropertyChanged(nameof(MissingAuthors));
    }

    private readonly MainViewModel _vm;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _loading;

    public BorderoWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        Grid.ItemsSource = _rows;
        var s = vm.Settings;
        Organizzatore.Text = s.BorderoOrganizzatore; Locale.Text = s.BorderoLocale; Comune.Text = s.BorderoComune;
        Esecutore.Text = s.BorderoEsecutore; Permesso.Text = s.BorderoPermesso;
        // serata di default: oggi se sono passate le 12, altrimenti ieri (serata finita dopo mezzanotte)
        var now = DateTime.Now;
        _loading = true;
        Giorno.SelectedDate = now.Hour >= 12 ? now.Date : now.Date.AddDays(-1);
        _loading = false;
        Closed += (_, _) => SaveHeader();
        Load();
    }

    private void SaveHeader()
    {
        var s = _vm.Settings;
        s.BorderoOrganizzatore = Organizzatore.Text.Trim(); s.BorderoLocale = Locale.Text.Trim(); s.BorderoComune = Comune.Text.Trim();
        s.BorderoEsecutore = Esecutore.Text.Trim(); s.BorderoPermesso = Permesso.Text.Trim();
        _vm.SaveSettings();
    }

    private (DateTime from, DateTime to) Range()
    {
        var day = Giorno.SelectedDate ?? DateTime.Today;
        TimeSpan.TryParseExact(OraDa.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var da);
        if (!TimeSpan.TryParseExact(OraA.Text.Trim(), @"h\:mm", CultureInfo.InvariantCulture, out var a)) a = TimeSpan.FromHours(6);
        var from = day + da;
        var to = day + a;
        if (to <= from) to = to.AddDays(1); // finisce il giorno dopo
        return (from, to);
    }

    private void Range_Changed(object sender, RoutedEventArgs e) { if (!_loading && IsLoaded) Load(); }
    private void Reload_Click(object sender, RoutedEventArgs e) => Load();

    private void Load()
    {
        var (from, to) = Range();
        var entries = PlayLog.Between(from, to);
        if (SoloMusica.IsChecked == true) entries = entries.Where(x => !x.Karaoke).ToList();
        _rows.Clear();
        bool group = Raggruppa.IsChecked == true;
        var seen = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in entries)
        {
            string key = (x.TrackId.Length > 0 ? x.TrackId : x.Artist + "|" + x.Title);
            if (group && seen.TryGetValue(key, out var r)) { r.Volte++; continue; }
            var lib = x.TrackId.Length > 0 ? _vm.Library.FindById(x.TrackId) : null;
            var row = new Row
            {
                N = _rows.Count + 1,
                Ora = x.Utc.ToLocalTime().ToString("HH:mm"),
                Titolo = x.Title,
                Autori = !string.IsNullOrWhiteSpace(x.Composer) ? x.Composer : (lib?.Composer ?? ""),
                Interprete = x.Artist,
                Durata = x.DurationSec > 0 ? TimeSpan.FromSeconds(x.DurationSec).ToString(@"m\:ss") : "",
                TrackId = x.TrackId,
            };
            _rows.Add(row);
            seen[key] = row;
        }
        int missing = _rows.Count(r => r.MissingAuthors);
        Summary.Text = _rows.Count == 0
            ? $"Nessuna riproduzione registrata fra {from:dd/MM HH:mm} e {to:dd/MM HH:mm}."
            : $"{_rows.Count} brani ({entries.Count} esecuzioni) fra {from:dd/MM HH:mm} e {to:dd/MM HH:mm}" + (missing > 0 ? $" · {missing} senza autori: completa le righe evidenziate" : " · autori completi ✓");
    }

    private void FillComposers_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var r in _rows.Where(r => r.MissingAuthors && r.TrackId.Length > 0))
        {
            var t = _vm.Library.FindById(r.TrackId);
            if (t != null && !string.IsNullOrWhiteSpace(t.Composer)) { r.Autori = t.Composer; n++; }
        }
        Status.Text = n > 0 ? $"Autori compilati per {n} brani" : "Nessun tag compositore disponibile per le righe mancanti";
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in Grid.SelectedItems.OfType<Row>().ToList()) _rows.Remove(r);
        Renumber();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _rows.Add(new Row { N = _rows.Count + 1, Ora = DateTime.Now.ToString("HH:mm"), Titolo = "", Autori = "", Interprete = "" });
        Grid.ScrollIntoView(_rows[^1]);
        Grid.SelectedItem = _rows[^1];
    }

    private void Renumber() { int i = 1; foreach (var r in _rows) r.N = i++; Grid.Items.Refresh(); }

    private string HeaderLine()
    {
        var (from, to) = Range();
        return $"{Locale.Text.Trim()} — {Comune.Text.Trim()} — {from:dd/MM/yyyy} ({from:HH:mm}–{to:HH:mm}) — organizzatore: {Organizzatore.Text.Trim()} — esecutore: {Esecutore.Text.Trim()} — permesso SIAE n. {Permesso.Text.Trim()}";
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(HeaderLine());
        sb.AppendLine("N.\tTitolo\tAutori/compositori\tInterprete\tDurata\tEsecuzioni");
        foreach (var r in _rows) sb.AppendLine($"{r.N}\t{r.Titolo}\t{r.Autori}\t{r.Interprete}\t{r.Durata}\t{r.Volte}");
        try { Clipboard.SetText(sb.ToString()); Status.Text = "Elenco copiato negli appunti (incolla in Excel o nel modulo online)"; }
        catch (Exception ex) { Status.Text = "Appunti non disponibili: " + ex.Message; }
    }

    private void Csv_Click(object sender, RoutedEventArgs e)
    {
        var (from, _) = Range();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Esporta programma musicale",
            Filter = "CSV (Excel)|*.csv",
            FileName = $"bordero-siae-{from:yyyy-MM-dd}.csv",
        };
        if (dlg.ShowDialog() != true) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var sb = new StringBuilder();
        sb.AppendLine("N.;Titolo;Autori/compositori;Interprete;Durata;Esecuzioni;Data;Locale;Comune;Organizzatore;Esecutore;Permesso SIAE");
        foreach (var r in _rows)
            sb.AppendLine(string.Join(";", r.N, Q(r.Titolo), Q(r.Autori), Q(r.Interprete), r.Durata, r.Volte, from.ToString("dd/MM/yyyy"),
                Q(Locale.Text.Trim()), Q(Comune.Text.Trim()), Q(Organizzatore.Text.Trim()), Q(Esecutore.Text.Trim()), Q(Permesso.Text.Trim())));
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM: Excel legge gli accenti
            Status.Text = "Esportato: " + dlg.FileName;
        }
        catch (Exception ex) { Status.Text = "Errore: " + ex.Message; }
    }

    private void Print_Click(object sender, RoutedEventArgs e)
    {
        var (from, to) = Range();
        static string H(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"it\"><head><meta charset=\"utf-8\"><title>Programma musicale</title><style>")
          .Append("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#111}h1{font-size:20px;margin:0 0 4px}h2{font-size:13px;font-weight:normal;color:#444;margin:0 0 14px}")
          .Append("table{border-collapse:collapse;width:100%;font-size:12px}th,td{border:1px solid #999;padding:4px 6px;text-align:left}th{background:#eee}td.n,td.d,td.v{text-align:right;width:1%;white-space:nowrap}")
          .Append(".meta{font-size:12px;margin:0 0 12px}.meta b{display:inline-block;min-width:130px}.sign{margin-top:36px;font-size:12px}.sign span{display:inline-block;width:45%}")
          .Append("@media print{button{display:none}}</style></head><body>");
        sb.Append("<button onclick=\"window.print()\" style=\"float:right\">Stampa / salva PDF</button>");
        sb.Append("<h1>Programma musicale (borderò SIAE)</h1><h2>Elenco delle opere eseguite — compilato con Mixfonia</h2>");
        sb.Append("<div class=\"meta\">")
          .Append($"<div><b>Data</b> {from:dd/MM/yyyy} dalle {from:HH:mm} alle {to:HH:mm}</div>")
          .Append($"<div><b>Locale / evento</b> {H(Locale.Text.Trim())}</div><div><b>Comune</b> {H(Comune.Text.Trim())}</div>")
          .Append($"<div><b>Organizzatore</b> {H(Organizzatore.Text.Trim())}</div><div><b>DJ / esecutore</b> {H(Esecutore.Text.Trim())}</div>")
          .Append($"<div><b>Permesso SIAE n.</b> {H(Permesso.Text.Trim())}</div></div>");
        sb.Append("<table><thead><tr><th>N.</th><th>Titolo</th><th>Autori / compositori</th><th>Interprete</th><th>Durata</th><th>Esec.</th></tr></thead><tbody>");
        foreach (var r in _rows)
            sb.Append($"<tr><td class=\"n\">{r.N}</td><td>{H(r.Titolo)}</td><td>{H(r.Autori)}</td><td>{H(r.Interprete)}</td><td class=\"d\">{H(r.Durata)}</td><td class=\"v\">{r.Volte}</td></tr>");
        sb.Append("</tbody></table>");
        sb.Append($"<p class=\"meta\" style=\"margin-top:10px\">Totale: {_rows.Count} opere, {_rows.Sum(r => r.Volte)} esecuzioni.</p>");
        sb.Append("<div class=\"sign\"><span>Firma dell'organizzatore ______________________</span><span>Firma dell'esecutore ______________________</span></div>");
        sb.Append("</body></html>");
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"mixfonia-bordero-{from:yyyy-MM-dd}.html");
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Status.Text = "Aperto nel browser: usa Stampa → Salva come PDF";
        }
        catch (Exception ex) { Status.Text = "Errore: " + ex.Message; }
    }
}
