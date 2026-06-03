using System;
using System.Collections;
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
    private ProfileItemViewModel? _selectionAnchor;

    public ProfilesView()
    {
        InitializeComponent();
        ProfilesDataGrid.AddHandler(
            InputElement.PointerPressedEvent,
            ProfilesDataGrid_PointerPressed,
            RoutingStrategies.Tunnel);
    }

    private void ProfilesDataGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TryOpenProfileContextMenu(e))
            return;

        var checkBox = FindSourceCheckBox(e.Source);
        if (checkBox?.DataContext is not ProfileItemViewModel current)
            return;

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

    private bool TryOpenProfileContextMenu(PointerPressedEventArgs e)
    {
        var pointer = e.GetCurrentPoint(ProfilesDataGrid);
        if (!pointer.Properties.IsRightButtonPressed)
            return false;

        if (e.Source is not Visual visual)
            return false;

        var row = visual.FindAncestorOfType<DataGridRow>(includeSelf: true);
        if (row?.DataContext is not ProfileItemViewModel profile)
            return false;

        ProfilesDataGrid.SelectedItem = profile;
        CreateProfileActionsFlyout(profile).ShowAt(row, showAtPointer: true);
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

    private static CheckBox? FindSourceCheckBox(object? source)
    {
        return source switch
        {
            CheckBox checkBox => checkBox,
            Visual visual => visual.FindAncestorOfType<CheckBox>(includeSelf: true),
            _ => null
        };
    }

    private void SelectProfileRange(ProfileItemViewModel anchor, ProfileItemViewModel current, bool isSelected)
    {
        var visibleProfiles = GetVisibleProfiles().ToList();
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

    private IEnumerable<ProfileItemViewModel> GetVisibleProfiles()
    {
        if (ProfilesDataGrid.CollectionView is IEnumerable collectionView)
            return collectionView.Cast<object>().OfType<ProfileItemViewModel>();

        return ProfilesDataGrid.ItemsSource is IEnumerable itemsSource
            ? itemsSource.Cast<object>().OfType<ProfileItemViewModel>()
            : Enumerable.Empty<ProfileItemViewModel>();
    }
}
