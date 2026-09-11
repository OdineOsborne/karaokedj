using System.Windows;
using System.Windows.Input;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class RhythmWindow : Window
{
    private readonly RhythmViewModel _vm;

    public RhythmWindow(RhythmViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        PreviewKeyDown += OnKey;
        Closed += (_, _) => vm.Save();
    }

    // Spazio = tap tempo (a meno che si stia scrivendo in una casella di testo)
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            _vm.Tap();
            e.Handled = true;
        }
    }
}
