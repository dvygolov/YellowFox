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
using YellowFox.Desktop.Views;

namespace YellowFox.Desktop.ViewModels;

public partial class TagsViewModel : ViewModelBase
{
    private readonly DatabaseService _databaseService;

    [ObservableProperty]
    private TagItemViewModel? _selectedTag;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    public ObservableCollection<TagItemViewModel> Tags { get; } = new();
    public bool HasSelection => SelectedTag != null;

    public TagsViewModel(DatabaseService databaseService)
    {
        _databaseService = databaseService;
        Load();
    }

    partial void OnSelectedTagChanged(TagItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
    }

    [RelayCommand]
    private void Refresh()
    {
        Load();
        StatusMessage = "Refreshed";
    }

    [RelayCommand]
    private async Task NewTag()
    {
        var editor = new TagEditorViewModel();
        if (!await ShowTagEditorAsync(editor))
            return;

        try
        {
            var tag = editor.BuildTag(new Tag());
            _databaseService.CreateTag(tag);
            StatusMessage = $"Created tag: {tag.Name}";
            Load();
            SelectedTag = Tags.FirstOrDefault(item => item.Tag.Id == tag.Id);
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task EditTag()
    {
        if (SelectedTag == null)
            return;

        var editor = new TagEditorViewModel(SelectedTag.Tag);
        if (!await ShowTagEditorAsync(editor))
            return;

        try
        {
            var tag = editor.BuildTag(SelectedTag.Tag);
            _databaseService.UpdateTag(tag);
            StatusMessage = $"Updated tag: {tag.Name}";
            Load();
            SelectedTag = Tags.FirstOrDefault(item => item.Tag.Id == tag.Id);
        }
        catch (Exception ex)
        {
            await ShowMessage("Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteTag()
    {
        if (SelectedTag == null)
            return;

        var confirmed = await ConfirmDelete(SelectedTag.Tag.Name);
        if (!confirmed)
            return;

        _databaseService.DeleteTag(SelectedTag.Tag.Id);
        StatusMessage = $"Deleted tag: {SelectedTag.Tag.Name}";
        Load();
        SelectedTag = null;
    }

    private void Load()
    {
        Tags.Clear();
        foreach (var tag in _databaseService.GetAllTags())
            Tags.Add(new TagItemViewModel(tag));
    }

    private async Task<bool> ShowTagEditorAsync(TagEditorViewModel editor)
    {
        var window = new TagEditorWindow
        {
            DataContext = editor
        };

        return await window.ShowDialog<bool>(GetMainWindow());
    }

    private async Task<bool> ConfirmDelete(string tagName)
    {
        var mainWindow = GetMainWindow();
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Delete Tag",
                ContentMessage = $"Delete tag '{tagName}'? It will be removed from all profiles, extensions, and bookmarks.",
                ButtonDefinitions = new[]
                {
                    new ButtonDefinition { Name = "Yes", IsDefault = true },
                    new ButtonDefinition { Name = "No", IsCancel = true }
                },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                MinWidth = 380,
                MaxWidth = 600,
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

public class TagItemViewModel
{
    public Tag Tag { get; }

    public TagItemViewModel(Tag tag)
    {
        Tag = tag;
        ColorBrush = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(tag.Color));
    }

    public string Name => Tag.Name;
    public string Glyph => TagIconCatalog.GlyphFor(Tag.Icon);
    public string Color => Tag.Color;
    public Avalonia.Media.IBrush ColorBrush { get; }
}
