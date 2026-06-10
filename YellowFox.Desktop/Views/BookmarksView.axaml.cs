using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class BookmarksView : UserControl
{
    private static readonly DataFormat<string> BookmarkDragDataFormat = DataFormat.CreateStringApplicationFormat("yellowfox.bookmark-id");
    private const double DragStartThreshold = 6;
    private BookmarkNodeViewModel? _pendingDragNode;
    private Point _dragStartPoint;
    private bool _isDragging;

    public BookmarksView()
    {
        InitializeComponent();
        BookmarksTree.AddHandler(InputElement.PointerPressedEvent, BookmarksTree_PointerPressed, handledEventsToo: true);
        BookmarksTree.AddHandler(InputElement.PointerMovedEvent, BookmarksTree_PointerMoved, handledEventsToo: true);
        BookmarksTree.AddHandler(InputElement.PointerReleasedEvent, BookmarksTree_PointerReleased, handledEventsToo: true);
        BookmarksTree.AddHandler(DragDrop.DragOverEvent, BookmarksTree_DragOver);
        BookmarksTree.AddHandler(DragDrop.DropEvent, BookmarksTree_Drop);
    }

    private void BookmarksTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(BookmarksTree).Properties;
        if (!properties.IsLeftButtonPressed)
            return;

        _pendingDragNode = FindBookmarkNode(e.Source);
        _dragStartPoint = e.GetPosition(BookmarksTree);
    }

    private async void BookmarksTree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingDragNode == null || _isDragging)
            return;

        var properties = e.GetCurrentPoint(BookmarksTree).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            ClearDragState();
            return;
        }

        var currentPoint = e.GetPosition(BookmarksTree);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < DragStartThreshold
            && Math.Abs(currentPoint.Y - _dragStartPoint.Y) < DragStartThreshold)
        {
            return;
        }

        _isDragging = true;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(BookmarkDragDataFormat, _pendingDragNode.Bookmark.Id));

        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    private void BookmarksTree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ClearDragState();
    }

    private void BookmarksTree_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (DataContext is not BookmarksViewModel viewModel
            || !TryGetDraggedBookmarkId(e, out var draggedId))
        {
            e.Handled = true;
            return;
        }

        var targetNode = FindBookmarkNode(e.Source);
        var dropPosition = ResolveDropPosition(e, targetNode);
        if (viewModel.CanMoveBookmark(draggedId, targetNode?.Bookmark.Id, dropPosition))
            e.DragEffects = DragDropEffects.Move;

        e.Handled = true;
    }

    private void BookmarksTree_Drop(object? sender, DragEventArgs e)
    {
        if (DataContext is not BookmarksViewModel viewModel
            || !TryGetDraggedBookmarkId(e, out var draggedId))
        {
            e.Handled = true;
            return;
        }

        var targetNode = FindBookmarkNode(e.Source);
        var dropPosition = ResolveDropPosition(e, targetNode);
        viewModel.MoveBookmark(draggedId, targetNode?.Bookmark.Id, dropPosition);
        e.Handled = true;
    }

    private static bool TryGetDraggedBookmarkId(DragEventArgs e, out string draggedId)
    {
        draggedId = string.Empty;
        if (!e.DataTransfer.Contains(BookmarkDragDataFormat))
            return false;

        draggedId = e.DataTransfer.TryGetValue(BookmarkDragDataFormat) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(draggedId);
    }

    private BookmarkDropPosition ResolveDropPosition(DragEventArgs e, BookmarkNodeViewModel? targetNode)
    {
        if (targetNode == null)
            return BookmarkDropPosition.RootEnd;

        var targetItem = FindTreeViewItem(e.Source);
        if (targetItem == null)
            return targetNode.Bookmark.IsFolder ? BookmarkDropPosition.Inside : BookmarkDropPosition.After;

        var y = e.GetPosition(targetItem).Y;
        var height = Math.Max(targetItem.Bounds.Height, 1);
        if (targetNode.Bookmark.IsFolder && y >= height * 0.25 && y <= height * 0.75)
            return BookmarkDropPosition.Inside;

        return y < height / 2
            ? BookmarkDropPosition.Before
            : BookmarkDropPosition.After;
    }

    private static BookmarkNodeViewModel? FindBookmarkNode(object? source)
    {
        return FindTreeViewItem(source)?.DataContext as BookmarkNodeViewModel;
    }

    private static TreeViewItem? FindTreeViewItem(object? source)
    {
        if (source is TreeViewItem treeViewItem)
            return treeViewItem;

        return (source as Visual)?.FindAncestorOfType<TreeViewItem>();
    }

    private void ClearDragState()
    {
        _pendingDragNode = null;
        _isDragging = false;
    }
}
