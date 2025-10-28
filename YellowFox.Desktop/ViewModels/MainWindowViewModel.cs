using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public ProfilesViewModel ProfilesViewModel { get; }
    
    public MainWindowViewModel(DatabaseService databaseService, BrowserService browserService)
    {
        ProfilesViewModel = new ProfilesViewModel(databaseService, browserService);
    }
}
