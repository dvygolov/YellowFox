using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

        ProfilesTree.AddHandler(InputElement.PointerPressedEvent, ProfilesTree_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.PointerMovedEvent, ProfilesTree_PointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.KeyDownEvent, ProfilesTree_KeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.PointerReleasedEvent, ProfilesTree_PointerReleased, handledEventsToo: true);
        ProfilesTree.AddHandler(InputElement.DoubleTappedEvent, ProfilesTree_DoubleTapped, handledEventsToo: true);
        ProfilesTree.AddHandler(DragDrop.DragOverEvent, ProfilesTree_DragOver);
        ProfilesTree.AddHandler(DragDrop.DropEvent, ProfilesTree_Drop);

        MoveSelectedToFolderButton.Click += MoveSelectedToFolderButton_Click;
    }

    private void MoveSelectedToFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ProfilesViewModel viewModel)
            return;

        var nodes = viewModel.GetSelection();
        if (nodes.Count == 0)
            return;

        ShowMoveTargetsFlyout(MoveSelectedToFolderButton, viewModel.BuildMoveTargets(nodes));
    }

    private static void ShowMoveTargetsFlyout(Control anchor, IReadOnlyList<ProfileMoveTarget> targets)
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var target in targets)
            flyout.Items.Add(new MenuItem { Header = target.Display, Command = target.Command });

        if (flyout.Items.Count == 0)
            flyout.Items.Add(new MenuItem { Header = "No folders", IsEnabled = false });

        flyout.ShowAt(anchor);
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
        if (checkBox?.DataContext is ProfileNodeViewModel current)
            HandleCheckBoxSelection(e, checkBox, current);

        if (IsInteractiveSource(e.Source))
        {
            _pendingDragNode = null;
            return;
        }

        if (TryHandleRowModifierClick(e, pressedNode))
        {
            _pendingDragNode = null;
            return;
        }

        _pendingDragNode = pressedNode;
        _dragStartPoint = e.GetPosition(ProfilesTree);
    }

    /// <summary>
    /// Ctrl+click toggles a single row, Shift+click extends the checked range, so the
    /// whole tree can be selected without aiming at the small checkboxes.
    /// </summary>
    private bool TryHandleRowModifierClick(PointerPressedEventArgs e, ProfileNodeViewModel? pressedNode)
    {
        if (pressedNode == null)
            return false;

        var isShiftClick = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var isControlClick = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!isShiftClick && !isControlClick)
            return false;

        ProfilesTree.SelectedItem = pressedNode;

        if (isControlClick)
        {
            pressedNode.IsSelected = !pressedNode.IsSelected;
            if (pressedNode is ProfileItemViewModel toggled)
                _selectionAnchor = toggled;

            return true;
        }

        if (_selectionAnchor != null && pressedNode is ProfileItemViewModel current)
        {
            SelectProfileRange(_selectionAnchor, current, true);
            return true;
        }

        pressedNode.SetSelectedRecursive(true);
        if (pressedNode is ProfileItemViewModel anchor)
            _selectionAnchor = anchor;

        return true;
    }

    private void HandleCheckBoxSelection(PointerPressedEventArgs e, CheckBox checkBox, ProfileNodeViewModel current)
    {
        ProfilesTree.SelectedItem = current;
        var isShiftClick = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var newSelectionState = checkBox.IsChecked != true;
        if (current is ProfileFolderNodeViewModel folder)
        {
            folder.SetSelectedRecursive(newSelectionState);
            e.Handled = true;
            return;
        }

        if (!isShiftClick || _selectionAnchor == null)
        {
            _selectionAnchor = (ProfileItemViewModel)current;
            return;
        }

        SelectProfileRange(_selectionAnchor, (ProfileItemViewModel)current, newSelectionState);
        e.Handled = true;
    }

    private async void ProfilesTree_KeyDown(object? sender, KeyEventArgs e)
    {
        // Ignore keys typed into row controls (notes editor, buttons, checkboxes):
        // Space must keep activating them instead of toggling selection.
        if (e.Source is TextBox or Button or CheckBox)
            return;

        if (DataContext is not ProfilesViewModel viewModel)
            return;

        switch (e.Key)
        {
            case Key.Delete:
                await viewModel.DeleteSelectedNodes();
                e.Handled = true;
                break;
            case Key.F2:
                await RenameSelectedNode(viewModel);
                e.Handled = true;
                break;
            case Key.A when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                viewModel.SelectAll();
                e.Handled = true;
                break;
            case Key.Space:
                e.Handled = ToggleSelectedNode();
                break;
        }
    }

    private async Task RenameSelectedNode(ProfilesViewModel viewModel)
    {
        switch (ProfilesTree.SelectedItem)
        {
            case ProfileFolderNodeViewModel folder:
                await viewModel.RenameFolder(folder);
                break;
            case ProfileItemViewModel profile:
                await viewModel.RenameProfile(profile);
                break;
        }
    }

    private bool ToggleSelectedNode()
    {
        switch (ProfilesTree.SelectedItem)
        {
            case ProfileFolderNodeViewModel folder:
                folder.SetSelectedRecursive(!folder.IsSelected);
                return true;
            case ProfileItemViewModel profile:
                profile.IsSelected = !profile.IsSelected;
                _selectionAnchor = profile;
                return true;
            default:
                return false;
        }
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
        var draggedNodes = ResolveDragNodes(_pendingDragNode);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ProfileNodeDragDataFormat, string.Join("\n", draggedNodes.Select(ToDragToken))));

        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    /// <summary>
    /// Dragging a row that is part of the checked selection moves the whole selection,
    /// dragging any other row moves just that row.
    /// </summary>
    private List<ProfileNodeViewModel> ResolveDragNodes(ProfileNodeViewModel dragged)
    {
        if (DataContext is not ProfilesViewModel viewModel || !dragged.IsSelected)
            return new List<ProfileNodeViewModel> { dragged };

        var selected = viewModel.GetSelection();
        return selected.Count > 1 && selected.Contains(dragged)
            ? selected.ToList()
            : new List<ProfileNodeViewModel> { dragged };
    }

    private static string ToDragToken(ProfileNodeViewModel node)
    {
        return node switch
        {
            ProfileFolderNodeViewModel folder => $"F:{folder.Folder.Id}",
            ProfileItemViewModel profile => $"P:{profile.Profile.Id}",
            _ => string.Empty
        };
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

        if (!TryGetDraggedNodes(e, out var dragged))
        {
            e.Handled = true;
            return;
        }

        var (target, row) = ResolveDropTarget(e);
        var dropPosition = ResolveDropPosition(e, target, row);

        if (DataContext is ProfilesViewModel viewModel
            && viewModel.CanMoveNodes(dragged, target, dropPosition))
        {
            e.DragEffects = DragDropEffects.Move;
        }

        e.Handled = true;
    }

    private void ProfilesTree_Drop(object? sender, DragEventArgs e)
    {
        if (!TryGetDraggedNodes(e, out var dragged))
        {
            e.Handled = true;
            return;
        }

        var (target, row) = ResolveDropTarget(e);
        var dropPosition = ResolveDropPosition(e, target, row);

        if (DataContext is ProfilesViewModel viewModel)
            viewModel.MoveNodes(dragged, target, dropPosition);

        e.Handled = true;
    }

    /// <summary>
    /// Finds the row under the pointer. Drag events may be routed with the tree itself as
    /// the source (the drag root is the only control with AllowDrop), so the pointer
    /// position is hit-tested when the event source does not carry a node.
    /// </summary>
    private (ProfileNodeViewModel? Node, TreeViewItem? Row) ResolveDropTarget(DragEventArgs e)
    {
        var sourceRow = FindTreeViewItem(e.Source);
        if (sourceRow?.DataContext is ProfileNodeViewModel sourceNode)
            return (sourceNode, sourceRow);

        foreach (var visual in ProfilesTree.GetVisualsAt(e.GetPosition(ProfilesTree)))
        {
            var row = FindTreeViewItem(visual);
            if (row?.DataContext is ProfileNodeViewModel node)
                return (node, row);
        }

        return (null, null);
    }

    private bool TryGetDraggedNodes(DragEventArgs e, out List<ProfileNodeViewModel> dragged)
    {
        dragged = new List<ProfileNodeViewModel>();

        if (DataContext is not ProfilesViewModel viewModel)
            return false;

        if (!e.DataTransfer.Contains(ProfileNodeDragDataFormat))
            return false;

        var token = e.DataTransfer.TryGetValue(ProfileNodeDragDataFormat);
        if (string.IsNullOrWhiteSpace(token))
            return false;

        foreach (var part in token.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var node = viewModel.FindNode(part.Trim());
            if (node != null && !dragged.Contains(node))
                dragged.Add(node);
        }

        return dragged.Count > 0;
    }

    private ProfileDropPosition ResolveDropPosition(DragEventArgs e, ProfileNodeViewModel? target, TreeViewItem? row)
    {
        if (target == null)
            return ProfileDropPosition.RootEnd;

        if (row == null)
            return target.IsFolder ? ProfileDropPosition.Inside : ProfileDropPosition.After;

        var y = e.GetPosition(row).Y;
        var height = Math.Max(row.Bounds.Height, 1);

        if (target.IsFolder && y >= height * 0.15 && y <= height * 0.85)
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

    private MenuFlyout CreateProfileActionsFlyout(ProfileItemViewModel profile)
    {
        var nodes = (DataContext as ProfilesViewModel)?.GetMoveSelection(profile)
                    ?? new List<ProfileNodeViewModel> { profile };
        var moveTargets = (DataContext as ProfilesViewModel)?.BuildMoveTargets(nodes)
                          ?? new List<ProfileMoveTarget>();

        var flyout = new MenuFlyout
        {
            Placement = PlacementMode.Pointer,
            Items =
            {
                new MenuItem { Header = "Edit", Command = profile.EditCommand },
                new MenuItem { Header = "Rename", InputGesture = new KeyGesture(Key.F2), Command = profile.RenameCommand },
                new MenuItem { Header = "Clone", Command = profile.CloneCommand },
                new MenuItem { Header = "Export cookies", Command = profile.ExportCookiesCommand },
                new MenuItem { Header = "Import cookies", Command = profile.ImportCookiesCommand },
                new MenuItem { Header = "Open log", Command = profile.ViewLogCommand },
                new MenuItem { Header = "Passkeys", Command = profile.PasskeysCommand },
                BuildMoveToFolderMenuItem(moveTargets, nodes.Count),
                new MenuItem { Header = "Delete", Command = profile.DeleteCommand, Foreground = Avalonia.Media.Brushes.Red }
            }
        };

        return flyout;
    }

    private MenuFlyout CreateFolderActionsFlyout(ProfileFolderNodeViewModel folder)
    {
        var nodes = (DataContext as ProfilesViewModel)?.GetMoveSelection(folder)
                    ?? new List<ProfileNodeViewModel> { folder };
        var moveTargets = (DataContext as ProfilesViewModel)?.BuildMoveTargets(nodes)
                          ?? new List<ProfileMoveTarget>();

        return new MenuFlyout
        {
            Placement = PlacementMode.Pointer,
            Items =
            {
                new MenuItem { Header = "Rename", InputGesture = new KeyGesture(Key.F2), Command = folder.RenameCommand },
                new MenuItem { Header = "New subfolder", Command = folder.NewSubfolderCommand },
                BuildMoveToFolderMenuItem(moveTargets, nodes.Count),
                new MenuItem { Header = "Delete", Command = folder.DeleteCommand, Foreground = Avalonia.Media.Brushes.Red }
            }
        };
    }

    private static MenuItem BuildMoveToFolderMenuItem(IReadOnlyList<ProfileMoveTarget> moveTargets, int nodeCount)
    {
        var header = nodeCount > 1 ? $"Move {nodeCount} items to folder" : "Move to folder";
        var menuItem = new MenuItem { Header = header };

        if (moveTargets.Count == 0)
        {
            menuItem.Items.Add(new MenuItem { Header = "No folders", IsEnabled = false });
            return menuItem;
        }

        foreach (var target in moveTargets)
            menuItem.Items.Add(new MenuItem { Header = target.Display, Command = target.Command });

        return menuItem;
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
