using System.Text.Json.Serialization;

namespace YellowFox.Desktop.Models;

public class AppSettings
{
    [JsonPropertyName("PythonScriptsPath")]
    public string PythonScriptsPath { get; set; } = "python";
}
