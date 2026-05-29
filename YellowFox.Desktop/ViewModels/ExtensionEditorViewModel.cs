using CommunityToolkit.Mvvm.ComponentModel;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class ExtensionEditorViewModel : ViewModelBase
{
    private readonly ExtensionStorageService _extensionStorageService;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _extensionPath = string.Empty;

    [ObservableProperty]
    private bool _isExtensionEnabled = true;

    public string Title { get; }

    public ExtensionEditorViewModel(ExtensionStorageService extensionStorageService, ExtensionItem? extension = null)
    {
        _extensionStorageService = extensionStorageService;
        Title = extension == null ? "New Extension" : "Edit Extension";

        if (extension == null)
            return;

        Name = extension.Name;
        ExtensionPath = extension.Path;
        IsExtensionEnabled = extension.IsEnabled;
    }

    public bool TryValidate(out string validationError)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            validationError = "Name is required.";
            return false;
        }

        var path = ExtensionPath.Trim();
        if (!BrowserService.IsExtensionPathUsable(path) && !_extensionStorageService.IsArchivePath(path))
        {
            validationError = "Path must point to an unpacked extension folder with manifest.json, or to a .zip/.xpi archive.";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    public ExtensionItem BuildExtension(ExtensionItem extension)
    {
        extension.Name = Name.Trim();
        extension.Path = _extensionStorageService.IsArchivePath(ExtensionPath.Trim())
            ? _extensionStorageService.StoreArchive(ExtensionPath.Trim(), extension.Id)
            : ExtensionPath.Trim();
        extension.IsEnabled = IsExtensionEnabled;
        return extension;
    }
}
