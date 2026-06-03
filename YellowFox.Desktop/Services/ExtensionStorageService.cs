using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class ExtensionStorageService
{
    private static readonly HttpClient HttpClient = CreateHttpClient();
    private readonly DatabaseService _databaseService;

    public ExtensionStorageService(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public ExtensionItem ImportArchive(string sourceFilePath, string extensionName)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Extension archive not found.", sourceFilePath);

        var extension = new ExtensionItem
        {
            Name = extensionName.Trim(),
            IsEnabled = true
        };

        extension.Path = StoreArchive(sourceFilePath, extension.Id);

        _databaseService.CreateExtension(extension);
        return extension;
    }

    public async Task<ExtensionItem> ImportFromUrlAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        var download = await ResolveDownloadAsync(sourceUrl, cancellationToken);
        var tempFile = await DownloadArchiveAsync(download.Url, download.FileName, cancellationToken);

        try
        {
            return ImportArchive(tempFile, download.ExtensionName);
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch
            {
                // Temp cleanup failure should not undo a successful import.
            }
        }
    }

    public string StoreArchive(string sourceFilePath, string extensionId)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Extension archive not found.", sourceFilePath);

        var extensionLower = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (extensionLower != ".zip" && extensionLower != ".xpi")
            throw new InvalidOperationException("Only .zip or .xpi archives are supported.");

        var extensionsRoot = _databaseService.GetExtensionsDataDirectory();
        var extensionFolder = Path.Combine(extensionsRoot, extensionId);
        if (Directory.Exists(extensionFolder))
            Directory.Delete(extensionFolder, recursive: true);
        Directory.CreateDirectory(extensionFolder);

        ZipFile.ExtractToDirectory(sourceFilePath, extensionFolder, overwriteFiles: true);
        return extensionFolder;
    }

    public bool IsArchivePath(string path)
    {
        if (!File.Exists(path))
            return false;

        var extensionLower = Path.GetExtension(path).ToLowerInvariant();
        return extensionLower == ".zip" || extensionLower == ".xpi";
    }

    public void DeleteExtensionWithFiles(ExtensionItem extension)
    {
        _databaseService.DeleteExtension(extension.Id);

        var directory = extension.Path;

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static bool TryBuildAmoApiUrl(string sourceUrl, out string apiUrl)
    {
        apiUrl = string.Empty;

        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Host, "addons.mozilla.org", StringComparison.OrdinalIgnoreCase))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        var addonIndex = Array.FindIndex(segments, segment => string.Equals(segment, "addon", StringComparison.OrdinalIgnoreCase));
        if (addonIndex < 0 || addonIndex + 1 >= segments.Length)
            return false;

        var slug = segments[addonIndex + 1];
        if (string.IsNullOrWhiteSpace(slug))
            return false;

        apiUrl = $"https://addons.mozilla.org/api/v5/addons/addon/{Uri.EscapeDataString(slug)}/?app=firefox";
        return true;
    }

    private static async Task<ExtensionDownload> ResolveDownloadAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(sourceUrl.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Enter a valid extension URL.");

        if (IsSupportedArchiveUrl(uri))
        {
            var directFileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
            return new ExtensionDownload(uri.ToString(), NameFromFileName(directFileName), directFileName);
        }

        if (!TryBuildAmoApiUrl(sourceUrl, out var apiUrl))
            throw new InvalidOperationException("Use an addons.mozilla.org add-on page URL, or a direct .xpi/.zip download link.");

        using var response = await HttpClient.GetAsync(apiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var name = root.TryGetProperty("name", out var nameElement)
            ? PickLocalizedString(nameElement)
            : null;
        var fileElement = FindAmoFile(root);

        if (!fileElement.HasValue || !fileElement.Value.TryGetProperty("url", out var urlElement))
            throw new InvalidOperationException("Could not find an XPI download URL in the AMO response.");

        var downloadUrl = urlElement.GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("AMO returned an empty XPI download URL.");

        var fileName = fileElement.Value.TryGetProperty("filename", out var filenameElement)
            ? filenameElement.GetString()
            : null;
        fileName = string.IsNullOrWhiteSpace(fileName)
            ? Path.GetFileName(Uri.UnescapeDataString(new Uri(downloadUrl).AbsolutePath))
            : fileName;

        return new ExtensionDownload(downloadUrl, string.IsNullOrWhiteSpace(name) ? NameFromFileName(fileName) : name, fileName);
    }

    private static JsonElement? FindAmoFile(JsonElement root)
    {
        if (root.TryGetProperty("current_version", out var currentVersion))
        {
            if (currentVersion.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
                return file;

            if (currentVersion.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var fileItem in files.EnumerateArray())
                {
                    if (fileItem.ValueKind == JsonValueKind.Object)
                        return fileItem;
                }
            }
        }

        return null;
    }

    private static async Task<string> DownloadArchiveAsync(string downloadUrl, string? fileName, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? fileName;
        fileName = NormalizeArchiveFileName(fileName);

        var tempDirectory = Path.Combine(Path.GetTempPath(), "YellowFox", "extension-downloads");
        Directory.CreateDirectory(tempDirectory);
        var tempFile = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}-{fileName}");

        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = File.Create(tempFile))
        {
            await input.CopyToAsync(output, cancellationToken);
        }

        if (new FileInfo(tempFile).Length == 0)
            throw new InvalidOperationException("Downloaded extension archive is empty.");

        return tempFile;
    }

    private static string NormalizeArchiveFileName(string? fileName)
    {
        fileName = string.IsNullOrWhiteSpace(fileName)
            ? "extension.xpi"
            : fileName.Trim().Trim('"');

        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension != ".xpi" && extension != ".zip")
            fileName += ".xpi";

        return fileName;
    }

    private static bool IsSupportedArchiveUrl(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension == ".xpi" || extension == ".zip";
    }

    private static string NameFromFileName(string? fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(name) ? "Extension" : name.Trim();
    }

    private static string? PickLocalizedString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString();

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (element.TryGetProperty("en-US", out var enUs) && enUs.ValueKind == JsonValueKind.String)
            return enUs.GetString();

        if (element.TryGetProperty("en-GB", out var enGb) && enGb.ValueKind == JsonValueKind.String)
            return enGb.GetString();

        return element.EnumerateObject()
            .Select(property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YellowFox/1.0");
        return client;
    }

    private sealed record ExtensionDownload(string Url, string ExtensionName, string? FileName);
}
