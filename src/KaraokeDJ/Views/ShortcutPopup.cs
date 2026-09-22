using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KaraokeDJ.Services;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

/// <summary>
/// Proprietà associata <c>v:Shortcut.Action="play"</c>: col tasto destro sul comando si apre la finestrella
/// che mostra e assegna la scorciatoia da tastiera e il controllo MIDI. Sui deck l'id viene prefissato con "a."/"b."
/// in base al DataContext.
/// </summary>
public static class Shortcut
{
    public static readonly DependencyProperty ActionProperty = DependencyProperty.RegisterAttached(
        "Action", typeof(string), typeof(Shortcut), new PropertyMetadata(null, OnActionChanged));

    public static string? GetAction(DependencyObject d) => (string?)d.GetValue(ActionProperty);
    public static void SetAction(DependencyObject d, string? v) => d.SetValue(ActionProperty, v);

    private static void OnActionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.PreviewMouseRightButtonUp -= OnRight;
        if (e.NewValue is string s && s.Length > 0)
        {
            el.PreviewMouseRightButtonUp += OnRight;
            var tip = el.ToolTip as string;
            if (tip != null && !tip.Contains("tasto destro", StringComparison.OrdinalIgnoreCase))
                el.ToolTip = tip + (el is Knob ? "\nCtrl+tasto destro: scorciatoia tastiera / MIDI" : "\nTasto destro: scorciatoia tastiera / MIDI");
        }
    }

    /// <summary>Id completo dell'azione per l'elemento (prefisso del deck se serve).</summary>
    public static string? ResolveAction(FrameworkElement el)
    {
        var a = GetAction(el);
        if (string.IsNullOrEmpty(a)) return null;
        if (a.Contains('.') || AppActions.Find(a) != null) return a;
        // cerca il deck risalendo il DataContext
        DependencyObject? cur = el;
        while (cur != null)
        {
            if (cur is FrameworkElement fe && fe.DataContext is DeckViewModel dv) return dv.Name.ToLowerInvariant() + "." + a;
            cur = VisualTreeHelper.GetParent(cur) ?? (cur as FrameworkElement)?.Parent;
        }
        return a;
    }

    private static void OnRight(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        // sulle manopole col kill (EQ) il tasto destro spegne la banda: il menù scorciatoie si apre con Ctrl+destro
        if (el is Knob k && k.HasRightAction && (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        var id = ResolveAction(el);
        if (id == null || App.Vm == null) return;
        e.Handled = true;
        new ShortcutPopup(App.Vm, id) { Owner = Window.GetWindow(el) }.ShowDialog();
    }
}

/// <summary>Finestrella: scorciatoia tastiera e MIDI di un'azione, con "impara" per entrambe.</summary>
public sealed class ShortcutPopup : Window
{
    private readonly MainViewModel _vm;
    private readonly string _action;
    private readonly TextBlock _keyText = new() { FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _midiText = new() { FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _keyLearn = new() { Content = "Premi un tasto…", MinWidth = 150 };
    private readonly Button _midiLearn = new() { Content = "Muovi un controllo…", MinWidth = 150 };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Opacity = 0.75 };
    private bool _learningKey;

    public ShortcutPopup(MainViewModel vm, string action)
    {
        _vm = vm; _action = action;
        Title = "Scorciatoia";
        Width = 520; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("WindowBgBrush");
        var def = AppActions.Find(action);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = def?.Label ?? action, FontSize = 17, FontWeight = FontWeights.Black, Margin = new Thickness(0, 0, 0, 12) });

        root.Children.Add(Row("⌨ Tastiera", _keyText, _keyLearn, "✕", () => { _vm.Keys.Clear(_action); Refresh(); }));
        root.Children.Add(Row("🎛 MIDI", _midiText, _midiLearn, "✕", () => { _vm.Midi.ClearMapping(_action); Refresh(); }));
        root.Children.Add(_hint);

        var close = new Button { Content = "Chiudi", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        root.Children.Add(close);
        Content = root;

        _keyLearn.Click += (_, _) => { _learningKey = true; _keyLearn.Content = "…premi ora (Esc annulla)"; _vm.Midi.CancelLearn(); _midiLearn.Content = "Muovi un controllo…"; };
        _midiLearn.Click += (_, _) =>
        {
            if (!_vm.Midi.IsOpen) { _hint.Text = "Nessun controller MIDI collegato: aprilo da Impostazioni → MIDI."; return; }
            _learningKey = false; _keyLearn.Content = "Premi un tasto…";
            _midiLearn.Content = "…muovi ora";
            _vm.Midi.BeginLearn(key => { _vm.Midi.SetMapping(_action, key); Refresh(); });
        };
        PreviewKeyDown += OnKey;
        Closed += (_, _) => { _vm.Midi.CancelLearn(); _vm.SaveSettings(); };
        Refresh();
    }

    private static UIElement Row(string label, TextBlock value, Button learn, string clearText, Action clear)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
        Grid.SetColumn(l, 0); g.Children.Add(l);
        Grid.SetColumn(value, 1); g.Children.Add(value);
        learn.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(learn, 2); g.Children.Add(learn);
        var c = new Button { Content = clearText, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Rimuovi" };
        c.Click += (_, _) => clear();
        Grid.SetColumn(c, 3); g.Children.Add(c);
        return g;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_learningKey) return;
        e.Handled = true;
        if (e.Key == Key.Escape) { _learningKey = false; _keyLearn.Content = "Premi un tasto…"; return; }
        var g = KeyboardService.GestureText(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
        if (g == null) return; // solo modificatore: aspetto il tasto
        var prev = _vm.Keys.ActionFor(g);
        _vm.Keys.Set(_action, g);
        _learningKey = false;
        _keyLearn.Content = "Premi un tasto…";
        Refresh();
        if (prev != null && prev != _action) _hint.Text = $"{KeyboardService.Pretty(g)} era assegnato a \"{AppActions.LabelOf(prev)}\": ora fa questa azione.";
    }

    private void Refresh()
    {
        _keyText.Text = KeyboardService.Pretty(_vm.Keys.GestureFor(_action));
        _midiText.Text = _vm.Midi.KeyFor(_action)?.ToString() ?? "—";
        _midiLearn.Content = "Muovi un controllo…";
        var def = AppActions.Find(_action);
        _hint.Text = def?.IsHold == true
            ? "Azione \"tieni premuto\": parte quando premi il tasto/pulsante e finisce quando lo rilasci."
            : def?.IsContinuous == true
                ? "Controllo continuo: assegna un fader o una manopola MIDI (da tastiera non è regolabile)."
                : "I tasti senza Ctrl/Alt non agiscono mentre scrivi in una casella di testo. Tutte le scorciatoie sono anche in Impostazioni → MIDI e tastiera.";
    }
}
