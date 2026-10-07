using System.Text.Json.Serialization;
using System.Collections.Generic;

namespace YellowFox.Desktop.Models;

public class AppSettings
{
    [JsonPropertyName("PythonScriptsPath")]
    public string PythonScriptsPath { get; set; } = "python";

    [JsonPropertyName("DataGridColumnWidths")]
    public Dictionary<string, Dictionary<string, double>> DataGridColumnWidths { get; set; } = new();

    [JsonPropertyName("ProfileTreeShowProxy")]
    public bool ProfileTreeShowProxy { get; set; } = true;

    [JsonPropertyName("ProfileTreeShowNotes")]
    public bool ProfileTreeShowNotes { get; set; } = true;

    [JsonPropertyName("ProfileTreeShowTags")]
    public bool ProfileTreeShowTags { get; set; } = true;
}
