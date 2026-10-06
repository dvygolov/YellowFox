using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace YellowFox.Desktop.ViewModels;

public partial class TextInputViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _value = string.Empty;

    public string Title { get; }
    public string Prompt { get; }
    public string Watermark { get; }

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public TextInputViewModel(string title, string prompt, string initialValue = "", string watermark = "")
    {
        Title = title;
        Prompt = prompt;
        Watermark = watermark;
        Value = initialValue;
    }

    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(HasValue));
    }

    [RelayCommand]
    private void Save()
    {
        // Dialog handles closing.
    }

    [RelayCommand]
    private void Cancel()
    {
        // Dialog handles closing.
    }
}
