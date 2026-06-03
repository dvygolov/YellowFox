using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace YellowFox.Desktop.Models;

public class AppSettings
{
    [JsonPropertyName("PythonScriptsPath")]
    public string PythonScriptsPath { get; set; } = "python";

    [JsonPropertyName("DataGridColumnWidths")]
    public Dictionary<string, Dictionary<string, double>> DataGridColumnWidths { get; set; } = new();
}
