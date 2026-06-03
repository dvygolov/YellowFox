using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using YellowFox.Desktop.Services;

namespace YellowFox.Desktop.Behaviors;

public static class DataGridColumnWidthPersistence
{
    public static readonly AttachedProperty<string?> KeyProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, string?>(
            "Key",
            typeof(DataGridColumnWidthPersistence));

    public static string? GetKey(DataGrid dataGrid)
    {
        return dataGrid.GetValue(KeyProperty);
    }

    public static void SetKey(DataGrid dataGrid, string? value)
    {
        dataGrid.SetValue(KeyProperty, value);
    }

    static DataGridColumnWidthPersistence()
    {
        KeyProperty.Changed.AddClassHandler<DataGrid>((dataGrid, args) =>
        {
            if (args.NewValue is string key && !string.IsNullOrWhiteSpace(key))
                Attach(dataGrid, key);
        });
    }

    private static void Attach(DataGrid dataGrid, string key)
    {
        dataGrid.Loaded += (_, _) =>
        {
            dataGrid.CanUserResizeColumns = true;
            ApplySavedWidths(dataGrid, key);
            SubscribeToWidthChanges(dataGrid, key);
        };
    }

    private static void ApplySavedWidths(DataGrid dataGrid, string key)
    {
        var settingsService = new SettingsService();
        var settings = settingsService.GetSettings();
        if (!settings.DataGridColumnWidths.TryGetValue(key, out var widths))
            return;

        for (var index = 0; index < dataGrid.Columns.Count; index++)
        {
            var column = dataGrid.Columns[index];
            var columnKey = GetColumnKey(column, index);
            if (widths.TryGetValue(columnKey, out var width) && width > 0)
                column.Width = new DataGridLength(width);
        }
    }

    private static void SubscribeToWidthChanges(DataGrid dataGrid, string key)
    {
        foreach (var column in dataGrid.Columns)
        {
            column.PropertyChanged += (_, args) =>
            {
                if (args.Property != DataGridColumn.WidthProperty)
                    return;

                SaveWidths(dataGrid, key);
            };
        }
    }

    private static void SaveWidths(DataGrid dataGrid, string key)
    {
        var settingsService = new SettingsService();
        var settings = settingsService.GetSettings();
        var widths = settings.DataGridColumnWidths.TryGetValue(key, out var existing)
            ? existing
            : settings.DataGridColumnWidths[key] = new();

        for (var index = 0; index < dataGrid.Columns.Count; index++)
        {
            var column = dataGrid.Columns[index];
            if (column.Width.IsAbsolute && column.Width.Value > 0)
                widths[GetColumnKey(column, index)] = column.Width.Value;
        }

        foreach (var staleKey in widths.Keys.Where(columnKey => !ColumnExists(dataGrid, columnKey)).ToArray())
            widths.Remove(staleKey);

        settingsService.SaveSettings(settings);
    }

    private static bool ColumnExists(DataGrid dataGrid, string columnKey)
    {
        for (var index = 0; index < dataGrid.Columns.Count; index++)
        {
            if (GetColumnKey(dataGrid.Columns[index], index) == columnKey)
                return true;
        }

        return false;
    }

    private static string GetColumnKey(DataGridColumn column, int index)
    {
        var header = column.Header?.ToString();
        return string.IsNullOrWhiteSpace(header)
            ? index.ToString("D2")
            : $"{index:D2}:{header}";
    }
}
