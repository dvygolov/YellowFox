using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.ViewModels;

public partial class BookmarksViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private BookmarkItemViewModel? _selectedBookmark;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private string _folder = string.Empty;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<BookmarkItemViewModel> Bookmarks { get; } = new();
    public bool IsEditMode => SelectedBookmark != null;
    public string FormTitle => IsEditMode ? "Edit Bookmark" : "New Bookmark";

    public BookmarksViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        Load();
    }

    partial void OnSelectedBookmarkChanged(BookmarkItemViewModel? value)
    {
        if (value == null)
        {
            ResetForm();
        }
        else
        {
            Title = value.Bookmark.Title;
            Url = value.Bookmark.Url;
            Folder = value.Bookmark.Folder ?? string.Empty;
        }

        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(FormTitle));
    }

    [RelayCommand]
    private void NewBookmark()
    {
        SelectedBookmark = null;
        ResetForm();
    }

    [RelayCommand]
    private void Refresh()
    {
        Load();
    }

    [RelayCommand]
    private async Task Save()
    {
        if (string.IsNullOrWhiteSpace(Title) || string.IsNullOrWhiteSpace(Url))
        {
            await ShowMessage("Validation", "Title and URL are required.");
            return;
        }

        if (!Uri.TryCreate(Url, UriKind.Absolute, out _))
        {
            await ShowMessage("Validation", "URL is invalid.");
            return;
        }

        if (SelectedBookmark == null)
        {
            var bookmark = new BookmarkItem
            {
                Title = Title.Trim(),
                Url = Url.Trim(),
                Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim()
            };
            _databaseService.CreateBookmark(bookmark);
            StatusMessage = $"Created: {bookmark.Title}";
        }
        else
        {
            var bookmark = SelectedBookmark.Bookmark;
            bookmark.Title = Title.Trim();
            bookmark.Url = Url.Trim();
            bookmark.Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim();
            _databaseService.UpdateBookmark(bookmark);
            StatusMessage = $"Updated: {bookmark.Title}";
        }

        Load();
        SelectedBookmark = null;
        ResetForm();
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedBookmark == null)
            return;

        var confirmed = await ConfirmDelete(SelectedBookmark.Bookmark.Title);
        if (!confirmed)
            return;

        _databaseService.DeleteBookmark(SelectedBookmark.Bookmark.Id);
        StatusMessage = $"Deleted: {SelectedBookmark.Bookmark.Title}";
        Load();
        SelectedBookmark = null;
        ResetForm();
    }

    private void Load()
    {
        Bookmarks.Clear();
        foreach (var bookmark in _databaseService.GetAllBookmarks())
        {
            Bookmarks.Add(new BookmarkItemViewModel(bookmark));
        }
    }

    private void ResetForm()
    {
        Title = string.Empty;
        Url = string.Empty;
        Folder = string.Empty;
    }

    private async Task<bool> ConfirmDelete(string title)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Delete Bookmark",
                ContentMessage = $"Delete bookmark '{title}'?",
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Yes", IsDefault = true },
                    new ButtonDefinition { Name = "No", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        var result = await box.ShowWindowDialogAsync(mainWindow!);
        return result == "Yes";
    }

    private async Task ShowMessage(string title, string message)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = title,
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK", IsDefault = true } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 360,
                MaxWidth = 560,
                SizeToContent = SizeToContent.WidthAndHeight
            });

        await box.ShowWindowDialogAsync(mainWindow!);
    }

    private Window GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow!
            : throw new InvalidOperationException("Main window not found");
    }
}

public class BookmarkItemViewModel : ViewModelBase
{
    public BookmarkItem Bookmark { get; }
    public string Title => Bookmark.Title;
    public string Url => Bookmark.Url;
    public string Folder => string.IsNullOrWhiteSpace(Bookmark.Folder) ? "-" : Bookmark.Folder!;

    public BookmarkItemViewModel(BookmarkItem bookmark)
    {
        Bookmark = bookmark;
    }
}
