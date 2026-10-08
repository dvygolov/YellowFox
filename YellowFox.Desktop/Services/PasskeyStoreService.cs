using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

/// <summary>
/// Reads and writes the per-profile software passkey store that the Camoufox
/// autoconfig keeps in <c>&lt;profile&gt;/yellowfox-passkeys.json</c>.
/// </summary>
public sealed class PasskeyStoreService
{
    public const string StoreFileName = "yellowfox-passkeys.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public string GetStorePath(string profileDirectory) =>
        Path.Combine(profileDirectory, StoreFileName);

    public IReadOnlyList<ProfilePasskey> GetPasskeys(string profileDirectory) =>
        LoadStore(profileDirectory)
            .Credentials
            .OrderBy(passkey => passkey.RpId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(passkey => passkey.CredentialId, StringComparer.Ordinal)
            .ToList();

    public int CountPasskeys(string profileDirectory)
    {
        try
        {
            return LoadStore(profileDirectory).Credentials.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Loads the store. A running profile rewrites the file on a timer, so the
    /// read is retried a couple of times before giving up on a partial write.
    /// </summary>
    public PasskeyStoreFile LoadStore(string profileDirectory)
    {
        var path = GetStorePath(profileDirectory);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                    return new PasskeyStoreFile();

                var json = File.ReadAllText(path);
                var store = JsonSerializer.Deserialize<PasskeyStoreFile>(json, SerializerOptions);
                store ??= new PasskeyStoreFile();
                store.Credentials = (store.Credentials ?? new List<ProfilePasskey>())
                    .Where(passkey => passkey != null && !string.IsNullOrWhiteSpace(passkey.CredentialId))
                    .ToList();
                return store;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Sleep(80);
            }
            catch (Exception)
            {
                return new PasskeyStoreFile();
            }
        }

        return new PasskeyStoreFile();
    }

    public void SaveStore(string profileDirectory, PasskeyStoreFile store)
    {
        Directory.CreateDirectory(profileDirectory);
        var json = JsonSerializer.Serialize(store, SerializerOptions);
        File.WriteAllText(GetStorePath(profileDirectory), json);
    }

    public bool DeletePasskey(string profileDirectory, string credentialId)
    {
        if (string.IsNullOrWhiteSpace(credentialId))
            return false;

        var store = LoadStore(profileDirectory);
        var removed = store.Credentials.RemoveAll(passkey =>
            string.Equals(passkey.CredentialId, credentialId, StringComparison.Ordinal));
        if (removed == 0)
            return false;

        SaveStore(profileDirectory, store);
        return true;
    }

    public string SerializeForExport(PasskeyStoreFile store) =>
        JsonSerializer.Serialize(store, SerializerOptions);
}
