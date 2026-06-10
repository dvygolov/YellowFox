using System;
using System.Collections.Generic;
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
using YellowFox.Desktop.Views;

namespace YellowFox.Desktop.ViewModels;

public partial class BookmarksViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;
    private Dictionary<string, BookmarkNodeViewModel> _nodesById = new(StringComparer.Ordinal);

    [ObservableProperty]
    private BookmarkNodeViewModel? _selectedBookmark;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<BookmarkNodeViewModel> Bookmarks { get; } = new();
    public bool IsEditMode => SelectedBookmark != null;
    public string CurrentLevel => CurrentParentId == null
        ? "Bookmarks Toolbar"
        : _nodesById.TryGetValue(CurrentParentId, out var node) ? node.Path : "Bookmarks Toolbar";

    private string? CurrentParentId => SelectedBookmark == null
        ? null
        : SelectedBookmark.Bookmark.IsFolder
            ? SelectedBookmark.Bookmark.Id
            : SelectedBookmark.Bookmark.ParentId;

    public BookmarksViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        Load();
    }

    partial void OnSelectedBookmarkChanged(BookmarkNodeViewModel? value)
    {
        OnPropertyChanged(nameof(IsEditMode));
        OnPropertyChanged(nameof(CurrentLevel));
    }

    [RelayCommand]
    private async Task NewBookmark()
    {
        await CreateItemAsync(isFolder: false);
    }

    [RelayCommand]
    private async Task NewFolder()
    {
        await CreateItemAsync(isFolder: true);
    }

    [RelayCommand]
    private async Task EditBookmark()
    {
        if (SelectedBookmark == null)
            return;

        var bookmark = SelectedBookmark.Bookmark;
        var editor = new BookmarkEditorViewModel(
            bookmark,
            bookmark.IsFolder,
            bookmark.ParentId,
            ParentDisplay(bookmark.ParentId));
        if (!await ShowBookmarkEditorAsync(editor))
            return;

        var updated = editor.BuildBookmark(bookmark);
        _databaseService.UpdateBookmark(updated);
        StatusMessage = $"Updated: {updated.Title}";
        Load(updated.Id);
    }

    [RelayCommand]
    private void Refresh()
    {
        Load(SelectedBookmark?.Bookmark.Id);
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedBookmark == null)
            return;

        var bookmark = SelectedBookmark.Bookmark;
        var confirmed = await ConfirmDelete(bookmark);
        if (!confirmed)
            return;

        _databaseService.DeleteBookmark(bookmark.Id);
        StatusMessage = bookmark.IsFolder
            ? $"Deleted folder: {bookmark.Title}"
            : $"Deleted bookmark: {bookmark.Title}";
        Load();
    }

    public bool CanMoveBookmark(string draggedId, string? targetId, BookmarkDropPosition dropPosition)
    {
        if (!_nodesById.TryGetValue(draggedId, out var draggedNode))
            return false;

        if (dropPosition == BookmarkDropPosition.RootEnd)
            return true;

        if (string.IsNullOrWhiteSpace(targetId) || !_nodesById.TryGetValue(targetId, out var targetNode))
            return false;

        if (string.Equals(draggedId, targetId, StringComparison.Ordinal))
            return false;

        if (dropPosition == BookmarkDropPosition.Inside && !targetNode.Bookmark.IsFolder)
            return false;

        return !IsDescendantOf(targetNode.Bookmark.Id, draggedNode.Bookmark.Id);
    }

    public void MoveBookmark(string draggedId, string? targetId, BookmarkDropPosition dropPosition)
    {
        if (!CanMoveBookmark(draggedId, targetId, dropPosition))
            return;

        var items = _databaseService.GetAllBookmarks();
        var byId = items.ToDictionary(item => item.Id, StringComparer.Ordinal);
        if (!byId.TryGetValue(draggedId, out var dragged))
            return;

        var oldParentId = dragged.ParentId;
        string? newParentId;
        int insertIndex;
        if (dropPosition == BookmarkDropPosition.RootEnd || string.IsNullOrWhiteSpace(targetId))
        {
            newParentId = null;
            insertIndex = OrderedSiblings(items, newParentId, draggedId).Count;
        }
        else
        {
            var target = byId[targetId];
            if (dropPosition == BookmarkDropPosition.Inside)
            {
                newParentId = target.Id;
                insertIndex = OrderedSiblings(items, newParentId, draggedId).Count;
            }
            else
            {
                newParentId = target.ParentId;
                var siblings = OrderedSiblings(items, newParentId, draggedId);
                var targetIndex = siblings.FindIndex(item => string.Equals(item.Id, target.Id, StringComparison.Ordinal));
                if (targetIndex < 0)
                    return;

                insertIndex = dropPosition == BookmarkDropPosition.Before ? targetIndex : targetIndex + 1;
            }
        }

        dragged.ParentId = newParentId;
        var newSiblings = OrderedSiblings(items, newParentId, draggedId);
        newSiblings.Insert(Math.Clamp(insertIndex, 0, newSiblings.Count), dragged);
        for (var index = 0; index < newSiblings.Count; index++)
            newSiblings[index].SortOrder = index;

        if (!SameParent(oldParentId, newParentId))
        {
            var oldSiblings = OrderedSiblings(items, oldParentId, draggedId);
            for (var index = 0; index < oldSiblings.Count; index++)
                oldSiblings[index].SortOrder = index;
        }

        _databaseService.UpdateBookmarks(items);
        StatusMessage = $"Moved: {dragged.Title}";
        Load(dragged.Id);
    }

    private async Task CreateItemAsync(bool isFolder)
    {
        var parentId = CurrentParentId;
        var editor = new BookmarkEditorViewModel(null, isFolder, parentId, ParentDisplay(parentId));
        if (!await ShowBookmarkEditorAsync(editor))
            return;

        var bookmark = editor.BuildBookmark(new BookmarkItem());
        _databaseService.CreateBookmark(bookmark);
        StatusMessage = bookmark.IsFolder
            ? $"Created folder: {bookmark.Title}"
            : $"Created bookmark: {bookmark.Title}";
        Load(bookmark.Id);
    }

    private void Load(string? selectId = null)
    {
        var items = _databaseService.GetAllBookmarks();
        _nodesById = items
            .Select(item => new BookmarkNodeViewModel(item))
            .ToDictionary(node => node.Bookmark.Id, StringComparer.Ordinal);

        foreach (var node in _nodesById.Values)
        {
            if (!string.IsNullOrWhiteSpace(node.Bookmark.ParentId)
                && _nodesById.TryGetValue(node.Bookmark.ParentId, out var parent))
            {
                parent.Children.Add(node);
            }
        }

        foreach (var node in _nodesById.Values)
            node.SortChildren();

        Bookmarks.Clear();
        foreach (var node in _nodesById.Values
                     .Where(node => string.IsNullOrWhiteSpace(node.Bookmark.ParentId)
                                    || !_nodesById.ContainsKey(node.Bookmark.ParentId))
                     .OrderBy(node => node.Bookmark.SortOrder)
                     .ThenByDescending(node => node.Bookmark.IsFolder)
                     .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase))
        {
            Bookmarks.Add(node);
        }

        SelectedBookmark = selectId != null && _nodesById.TryGetValue(selectId, out var selected)
            ? selected
            : null;
        OnPropertyChanged(nameof(CurrentLevel));
    }

    private string ParentDisplay(string? parentId)
    {
        if (string.IsNullOrWhiteSpace(parentId))
            return "Bookmarks Toolbar";

        return _nodesById.TryGetValue(parentId, out var node)
            ? node.Path
            : "Bookmarks Toolbar";
    }

    private bool IsDescendantOf(string nodeId, string possibleAncestorId)
    {
        var currentId = nodeId;
        while (_nodesById.TryGetValue(currentId, out var current))
        {
            var parentId = current.Bookmark.ParentId;
            if (string.IsNullOrWhiteSpace(parentId))
                return false;

            if (string.Equals(parentId, possibleAncestorId, StringComparison.Ordinal))
                return true;

            currentId = parentId;
        }

        return false;
    }

    private static List<BookmarkItem> OrderedSiblings(IEnumerable<BookmarkItem> items, string? parentId, string? excludeId = null)
    {
        return items
            .Where(item => SameParent(item.ParentId, parentId)
                           && !string.Equals(item.Id, excludeId, StringComparison.Ordinal))
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.IsFolder)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SameParent(string? left, string? right)
    {
        return string.Equals(
            string.IsNullOrWhiteSpace(left) ? null : left,
            string.IsNullOrWhiteSpace(right) ? null : right,
            StringComparison.Ordinal);
    }

    private async Task<bool> ConfirmDelete(BookmarkItem bookmark)
    {
        var mainWindow = GetMainWindow();
        var message = bookmark.IsFolder
            ? $"Delete folder '{bookmark.Title}' and all bookmarks inside it?"
            : $"Delete bookmark '{bookmark.Title}'?";
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = bookmark.IsFolder ? "Delete Folder" : "Delete Bookmark",
                ContentMessage = message,
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

    private async Task<bool> ShowBookmarkEditorAsync(BookmarkEditorViewModel editor)
    {
        var window = new BookmarkEditorWindow
        {
            DataContext = editor
        };

        return await window.ShowDialog<bool>(GetMainWindow());
    }

    private Window GetMainWindow()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow!
            : throw new InvalidOperationException("Main window not found");
    }
}

public class BookmarkNodeViewModel : ViewModelBase
{
    public BookmarkItem Bookmark { get; }
    public ObservableCollection<BookmarkNodeViewModel> Children { get; } = new();
    public string Title => Bookmark.Title;
    public string Url => Bookmark.IsFolder ? string.Empty : Bookmark.Url;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public string Icon => Bookmark.IsFolder ? "📁" : "🔖";
    public string Kind => Bookmark.IsFolder ? "Folder" : "Bookmark";
    public string Path { get; private set; }

    public BookmarkNodeViewModel(BookmarkItem bookmark)
    {
        Bookmark = bookmark;
        Path = bookmark.Title;
    }

    public void SortChildren()
    {
        var sorted = Children
            .OrderBy(node => node.Bookmark.SortOrder)
            .ThenByDescending(node => node.Bookmark.IsFolder)
            .ThenBy(node => node.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Children.Clear();
        foreach (var child in sorted)
        {
            child.Path = $"{Path}/{child.Title}";
            child.SortChildren();
            Children.Add(child);
        }
    }
}

public enum BookmarkDropPosition
{
    Before,
    After,
    Inside,
    RootEnd
}
