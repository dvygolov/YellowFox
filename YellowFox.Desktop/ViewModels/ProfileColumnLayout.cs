using System;
using System.Collections.Generic;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace YellowFox.Desktop.ViewModels;

public enum ProfileColumn
{
    Select,
    Profile,
    Tags,
    Proxy,
    Notes,
    Actions
}

/// <summary>
/// Widths shared by the profile tree header and every profile/folder row.
/// The header drag handles update these values, so all rows follow a single layout.
/// </summary>
public sealed partial class ProfileColumnLayout : ObservableObject
{
    public const double SelectDefault = 44;
    public const double ProfileDefault = 302;
    public const double TagsDefault = 52;
    public const double ProxyDefault = 160;
    public const double NotesDefault = 480;
    public const double ActionsDefault = 152;

    public const double SelectMin = 32;
    public const double ProfileMin = 150;
    public const double TagsMin = 28;
    public const double ProxyMin = 70;
    public const double NotesMin = 100;
    public const double ActionsMin = 110;

    [ObservableProperty]
    private GridLength _selectWidth = new(SelectDefault);

    [ObservableProperty]
    private GridLength _profileWidth = new(ProfileDefault);

    [ObservableProperty]
    private GridLength _tagsWidth = new(TagsDefault);

    [ObservableProperty]
    private GridLength _proxyWidth = new(ProxyDefault);

    [ObservableProperty]
    private GridLength _notesWidth = new(NotesDefault);

    [ObservableProperty]
    private GridLength _actionsWidth = new(ActionsDefault);

    /// <summary>
    /// Width the TreeView's item presenter must reserve so the horizontal scrollbar
    /// covers the whole table. The 36px is the top-level row indent; the TreeView adds
    /// its 8px padding around this value.
    /// </summary>
    public double TreeContentWidth =>
        SelectWidth.Value
        + ProfileWidth.Value
        + TagsWidth.Value
        + ProxyWidth.Value
        + NotesWidth.Value
        + ActionsWidth.Value
        + 5 * 8
        + 36;

    partial void OnSelectWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));
    partial void OnProfileWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));
    partial void OnTagsWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));
    partial void OnProxyWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));
    partial void OnNotesWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));
    partial void OnActionsWidthChanged(GridLength value) => OnPropertyChanged(nameof(TreeContentWidth));

    public void Resize(ProfileColumn column, double delta)
    {
        switch (column)
        {
            case ProfileColumn.Select:
                SelectWidth = Resize(SelectWidth, delta, SelectMin);
                break;
            case ProfileColumn.Profile:
                ProfileWidth = Resize(ProfileWidth, delta, ProfileMin);
                break;
            case ProfileColumn.Tags:
                TagsWidth = Resize(TagsWidth, delta, TagsMin);
                break;
            case ProfileColumn.Proxy:
                ProxyWidth = Resize(ProxyWidth, delta, ProxyMin);
                break;
            case ProfileColumn.Notes:
                NotesWidth = Resize(NotesWidth, delta, NotesMin);
                break;
            case ProfileColumn.Actions:
                ActionsWidth = Resize(ActionsWidth, delta, ActionsMin);
                break;
        }
    }

    public void Load(IReadOnlyDictionary<string, double> widths)
    {
        SelectWidth = FromSettings(widths, nameof(SelectWidth), SelectDefault, SelectMin);
        ProfileWidth = FromSettings(widths, nameof(ProfileWidth), ProfileDefault, ProfileMin);
        TagsWidth = FromSettings(widths, nameof(TagsWidth), TagsDefault, TagsMin);
        ProxyWidth = FromSettings(widths, nameof(ProxyWidth), ProxyDefault, ProxyMin);
        NotesWidth = FromSettings(widths, nameof(NotesWidth), NotesDefault, NotesMin);
        ActionsWidth = FromSettings(widths, nameof(ActionsWidth), ActionsDefault, ActionsMin);
    }

    public Dictionary<string, double> ToSettings()
    {
        return new Dictionary<string, double>(StringComparer.Ordinal)
        {
            [nameof(SelectWidth)] = SelectWidth.Value,
            [nameof(ProfileWidth)] = ProfileWidth.Value,
            [nameof(TagsWidth)] = TagsWidth.Value,
            [nameof(ProxyWidth)] = ProxyWidth.Value,
            [nameof(NotesWidth)] = NotesWidth.Value,
            [nameof(ActionsWidth)] = ActionsWidth.Value
        };
    }

    private static GridLength Resize(GridLength current, double delta, double minWidth)
    {
        var width = current.IsAbsolute ? current.Value : 0;
        return new GridLength(Math.Max(minWidth, width + delta));
    }

    private static GridLength FromSettings(
        IReadOnlyDictionary<string, double> widths,
        string key,
        double fallback,
        double minWidth)
    {
        if (!widths.TryGetValue(key, out var width) || !double.IsFinite(width) || width < minWidth)
            width = fallback;

        return new GridLength(Math.Max(minWidth, width));
    }
}
