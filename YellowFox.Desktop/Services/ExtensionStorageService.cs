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
            Name = extensionName.Trim()
        };

        var extensionsRoot = _databaseService.GetExtensionsDataDirectory();
        var extensionFolder = Path.Combine(extensionsRoot, extension.Id);
        Directory.CreateDirectory(extensionFolder);

        var extensionLower = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (extensionLower == ".zip")
        {
            ZipFile.ExtractToDirectory(sourceFilePath, extensionFolder, overwriteFiles: true);
            extension.Path = extensionFolder;
        }
        else if (extensionLower == ".xpi")
        {
            var targetFile = Path.Combine(extensionFolder, Path.GetFileName(sourceFilePath));
            File.Copy(sourceFilePath, targetFile, overwrite: true);
            extension.Path = targetFile;
        }
        else
        {
            throw new InvalidOperationException("Only .zip or .xpi archives are supported.");
        }

        _databaseService.CreateExtension(extension);
        return extension;
    }

    public void DeleteExtensionWithFiles(ExtensionItem extension)
    {
        _databaseService.DeleteExtension(extension.Id);

        var directory = File.Exists(extension.Path)
            ? Path.GetDirectoryName(extension.Path)
            : extension.Path;

        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
