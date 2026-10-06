using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class ProfilesView : UserControl
{
    private static readonly DataFormat<string> ProfileNodeDragDataFormat =
        DataFormat.CreateStringApplicationFormat("yellowfox.profile-node");

    private const double DragStartThreshold = 6;

    private ProfileItemViewModel? _selectionAnchor;
    private ProfileNodeViewModel? _pendingDragNode;
    private Point _dragStartPoint;
    private bool _isDragging;

    public ProfilesView()
    {
        InitializeComponent();

        ProfilesTree.AddHandler(InputElement.PointerPressedEvent, ProfilesTree_PointerPressed, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.PointerMovedEvent, ProfilesTree_PointerMoved, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.PointerReleasedEvent, ProfilesTree_PointerReleased, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.DoubleTappedEvent, ProfilesTree_DoubleTapped, handledEventsToo: true);
        ProfilesTree.AddHandler(DragDrop.DragOverEvent, ProfilesTree_DragOver);
        ProfilesTree.AddHandler(DragDrop.DropEvent, ProfilesTree_Drop);
    }

    private void ProfilesTree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(ProfilesTree);
        if (pointer.Properties.IsRightButtonPressed)
        {
            TryOpenContextMenu(e);
            return;
        }

        if (!pointer.Properties.IsLeftButtonPressed)
            return;

        var pressedNode = FindNode(e.Source);

        var checkBox = FindSourceCheckBox(e.Source);
        if (checkBox?.DataContext is ProfileItemViewModel current)
        {
            HandleCheckBoxSelection(e, checkBox, current);
        }

        if (IsInteractiveSource(e.Source))
        {
            _pendingDragNode = null;
            return;
        }

        _pendingDragNode = pressedNode;
        _dragStartPoint = e.GetPosition(ProfilesTree);
    }

    private void HandleCheckBoxSelection(PointerPressedEventArgs e, CheckBox checkBox, ProfileItemViewModel current)
    {
        var isShiftClick = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (!isShiftClick || _selectionAnchor == null)
        {
            _selectionAnchor = current;
            return;
        }

        var newSelectionState = checkBox.IsChecked != true;
        SelectProfileRange(_selectionAnchor, current, newSelectionState);
        e.Handled = true;
    }

    private async void ProfilesTree_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingDragNode == null || _isDragging)
            return;

        if (!e.GetCurrentPoint(ProfilesTree).Properties.IsLeftButtonPressed)
        {
            ClearDragState();
            return;
        }

        var currentPoint = e.GetPosition(ProfilesTree);
        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < DragStartThreshold
            && Math.Abs(currentPoint.Y - _dragStartPoint.Y) < DragStartThreshold)
        {
            return;
        }

        _isDragging = true;
        var dragged = _pendingDragNode;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ProfileNodeDragDataFormat, dragged is ProfileFolderNodeViewModel folder
            ? $"F:{folder.Folder.Id}"
            : $"P:{((ProfileItemViewModel)dragged).Profile.Id}"));

        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    private void ProfilesTree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ClearDragState();
    }

    private void ProfilesTree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveSource(e.Source))
            return;

        if (FindNode(e.Source) is ProfileFolderNodeViewModel folder)
        {
            folder.IsExpanded = !folder.IsExpanded;
            e.Handled = true;
        }
    }

    private void ProfilesTree_DragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.None;

        if (!TryGetDraggedNode(e, out var dragged))
        {
            e.Handled = true;
            return;
        }

        var target = FindNode(e.Source);
        var dropPosition = ResolveDropPosition(e, target);

        if (DataContext is ProfilesViewModel viewModel
            && viewModel.CanMoveNode(dragged!, target, dropPosition))
        {
            e.DragEffects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private void ProfilesTree_Drop(object? sender, DragEventArgs e)
    {
        if (!TryGetDraggedNode(e, out var dragged))
        {
            e.Handled = true;
            return;
        }

        var target = FindNode(e.Source);
        var dropPosition = ResolveDropPosition(e, target);

        if (DataContext is ProfilesViewModel viewModel)
            viewModel.MoveNode(dragged!, target, dropPosition);

        e.Handled = true;
    }

    private bool TryGetDraggedNode(DragEventArgs e, out ProfileNodeViewModel? dragged)
    {
        dragged = null;

        if (DataContext is not ProfilesViewModel viewModel)
            return false;

        if (!e.DataTransfer.Contains(ProfileNodeDragDataFormat))
            return false;

        var token = e.DataTransfer.TryGetValue(ProfileNodeDragDataFormat);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        dragged = viewModel.FindNode(token);
        return dragged != null;
    }

    private ProfileDropPosition ResolveDropPosition(DragEventArgs e, ProfileNodeViewModel? target)
    {
        if (target == null)
            return ProfileDropPosition.RootEnd;

        var targetItem = FindTreeViewItem(e.Source);
        if (targetItem == null)
            return target.IsFolder ? ProfileDropPosition.Inside : ProfileDropPosition.After;

        var y = e.GetPosition(targetItem).Y;
        var height = Math.Max(targetItem.Bounds.Height, 1);

        if (target.IsFolder && y >= height * 0.25 && y <= height * 0.75)
            return ProfileDropPosition.Inside;

        return y < height / 2
            ? ProfileDropPosition.Before
            : ProfileDropPosition.After;
    }

    private bool TryOpenContextMenu(PointerPressedEventArgs e)
    {
        if (e.Source is not Visual visual)
            return false;

        var row = visual.FindAncestorOfType<TreeViewItem>(includeSelf: true);
        if (row?.DataContext is not ProfileNodeViewModel node)
            return false;

        ProfilesTree.SelectedItem = node;

        var flyout = node switch
        {
            ProfileFolderNodeViewModel folder => CreateFolderActionsFlyout(folder),
            ProfileItemViewModel profile => CreateProfileActionsFlyout(profile),
            _ => null
        };

        if (flyout == null)
            return false;

        flyout.ShowAt(row, showAtPointer: true);
        e.Handled = true;
        return true;
    }

    private static MenuFlyout CreateProfileActionsFlyout(ProfileItemViewModel profile)
    {
        return new MenuFlyout
        {
            Placement = PlacementMode.Pointer,
            Items =
            {
                new MenuItem { Header = "Edit", Command = profile.EditCommand },
                new MenuItem { Header = "Clone", Command = profile.CloneCommand },
                new MenuItem { Header = "Export cookies", Command = profile.ExportCookiesCommand },
                new MenuItem { Header = "Import cookies", Command = profile.ImportCookiesCommand },
                new MenuItem { Header = "Open log", Command = profile.ViewLogCommand },
                new MenuItem { Header = "Delete", Command = profile.DeleteCommand, Foreground = Avalonia.Media.Brushes.Red }
            }
        };
    }

    private static MenuFlyout CreateFolderActionsFlyout(ProfileFolderNodeViewModel folder)
    {
        return new MenuFlyout
        {
            Placement = PlacementMode.Pointer,
            Items =
            {
                new MenuItem { Header = "Rename", Command = folder.RenameCommand },
                new MenuItem { Header = "New subfolder", Command = folder.NewSubfolderCommand },
                new MenuItem { Header = "Delete", Command = folder.DeleteCommand, Foreground = Avalonia.Media.Brushes.Red }
            }
        };
    }

    private void SelectProfileRange(ProfileItemViewModel anchor, ProfileItemViewModel current, bool isSelected)
    {
        var visibleProfiles = GetVisibleProfiles();
        var anchorIndex = visibleProfiles.IndexOf(anchor);
        var currentIndex = visibleProfiles.IndexOf(current);

        if (anchorIndex < 0 || currentIndex < 0)
        {
            _selectionAnchor = current;
            return;
        }

        var start = Math.Min(anchorIndex, currentIndex);
        var end = Math.Max(anchorIndex, currentIndex);
        for (var index = start; index <= end; index++)
            visibleProfiles[index].IsSelected = isSelected;
    }

    private List<ProfileItemViewModel> GetVisibleProfiles()
    {
        var result = new List<ProfileItemViewModel>();
        if (DataContext is not ProfilesViewModel viewModel)
            return result;

        void Walk(IEnumerable<ProfileNodeViewModel> nodes)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case ProfileItemViewModel profile:
                        result.Add(profile);
                        break;
                    case { IsExpanded: true }:
                        Walk(node.Children);
                        break;
                }
            }
        }

        Walk(viewModel.RootNodes);
        return result;
    }

    private static CheckBox? FindSourceCheckBox(object? source)
    {
        return source switch
        {
            CheckBox checkBox => checkBox,
            Visual visual => visual.FindAncestorOfType<CheckBox>(includeSelf: true),
            _ => null
        };
    }

    private static bool IsInteractiveSource(object? source)
    {
        if (source is not Visual visual)
            return false;

        return visual.FindAncestorOfType<Button>(includeSelf: true) != null
               || visual.FindAncestorOfType<CheckBox>(includeSelf: true) != null
               || visual.FindAncestorOfType<TextBox>(includeSelf: true) != null;
    }

    private static ProfileNodeViewModel? FindNode(object? source)
    {
        return FindTreeViewItem(source)?.DataContext as ProfileNodeViewModel;
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
