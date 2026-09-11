using System.Windows;
using KaraokeDJ.ViewModels;

namespace KaraokeDJ.Views;

public partial class AnimationWindow : Window
{
    public AnimationWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Closed += (_, _) => vm.SaveCelebration();
    }
}
