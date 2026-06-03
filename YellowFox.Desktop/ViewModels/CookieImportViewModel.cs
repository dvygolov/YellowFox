using CommunityToolkit.Mvvm.ComponentModel;

namespace YellowFox.Desktop.ViewModels;

public partial class CookieImportViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _profileName = string.Empty;

    [ObservableProperty]
    private string _cookieText = string.Empty;

    [ObservableProperty]
    private string _domain = string.Empty;

    public string Title => $"Import cookies: {ProfileName}";

    public CookieImportViewModel(string profileName)
    {
        ProfileName = profileName;
    }
}
