using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Dto;
using MsBox.Avalonia.Enums;
using MsBox.Avalonia.Models;
using YellowFox.Desktop.ViewModels;

namespace YellowFox.Desktop.Views;

public partial class ProfileEditorWindow : Window
{
    public ProfileEditorWindow()
    {
        InitializeComponent();
    }
    
    private async void SaveButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ProfileEditorViewModel vm)
        {
            // Validate
            if (string.IsNullOrWhiteSpace(vm.Name))
            {
                await ShowError("Please enter a profile name.");
                return;
            }
            
            if (vm.SelectedScreenPreset == null)
            {
                await ShowError("Please select a screen resolution.");
                return;
            }
            
            // Execute save command
            if (vm.SaveCommand.CanExecute(null))
            {
                try
                {
                    vm.SaveCommand.Execute(null);
                    Close(true); // Return true on success
                }
                catch (InvalidOperationException ex)
                {
                    await ShowError(ex.Message);
                }
                catch (Exception ex)
                {
                    await ShowError($"Error saving profile: {ex.Message}");
                }
            }
        }
    }
    
    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(false); // Return false on cancel
    }
    
    private async Task ShowError(string message)
    {
        var box = MessageBoxManager.GetMessageBoxCustom(
            new MessageBoxCustomParams
            {
                ContentTitle = "Error",
                ContentMessage = message,
                ButtonDefinitions = new[] { new ButtonDefinition { Name = "OK" } },
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowIcon = this.Icon,
                MinWidth = 400,
                MaxWidth = 600,
                SizeToContent = SizeToContent.WidthAndHeight
            });
        
        await box.ShowWindowDialogAsync(this);
    }
}
