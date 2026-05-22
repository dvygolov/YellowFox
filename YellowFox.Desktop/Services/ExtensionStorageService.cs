using System;
using System.IO;
using System.IO.Compression;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class ExtensionStorageService
{
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
}
