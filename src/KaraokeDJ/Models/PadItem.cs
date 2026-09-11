using CommunityToolkit.Mvvm.ComponentModel;

namespace KaraokeDJ.Models;

public sealed partial class PadItem : ObservableObject
{
    public int Index { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isPlaying;

    public string Hotkey => $"F{Index + 1}";
    public bool HasFile => !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath);

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(HasFile));
}

public sealed class PadDto
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public string? FilePath { get; set; }
}
