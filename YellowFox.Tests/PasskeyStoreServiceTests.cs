using System.IO;
using YellowFox.Desktop.Models;
using YellowFox.Desktop.Services;

namespace YellowFox.Tests;

public class PasskeyStoreServiceTests : IDisposable
{
    private readonly string _profileDir;
    private readonly PasskeyStoreService _service = new();

    public PasskeyStoreServiceTests()
    {
        _profileDir = Path.Combine(Path.GetTempPath(), "yellowfox-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_profileDir);
    }

    [Fact]
    public void GetPasskeys_WithoutStoreFile_ReturnsEmpty()
    {
        Assert.Empty(_service.GetPasskeys(_profileDir));
        Assert.Equal(0, _service.CountPasskeys(_profileDir));
    }

    [Fact]
    public void SaveAndLoadStore_RoundTripsCredentials()
    {
        var store = new PasskeyStoreFile
        {
            Credentials =
            {
                new ProfilePasskey
                {
                    CredentialId = "cred-b",
                    RpId = "facebook.com",
                    UserHandle = "user-b",
                    PrivateKey = "pk-b",
                    IsResidentCredential = true,
                    SignCount = 3
                },
                new ProfilePasskey
                {
                    CredentialId = "cred-a",
                    RpId = "google.com",
                    UserHandle = "user-a",
                    PrivateKey = "pk-a",
                    IsResidentCredential = false,
                    SignCount = 0
                }
            }
        };

        _service.SaveStore(_profileDir, store);

        var loaded = _service.GetPasskeys(_profileDir);

        Assert.Equal(2, loaded.Count);
        // Sorted by RP id.
        Assert.Equal("facebook.com", loaded[0].RpId);
        Assert.Equal("google.com", loaded[1].RpId);
        Assert.Equal("pk-a", loaded[1].PrivateKey);
        Assert.True(loaded[0].IsResidentCredential);
        Assert.False(loaded[1].IsResidentCredential);
        Assert.Equal(3, loaded[0].SignCount);
    }

    [Fact]
    public void LoadStore_WritesCamelCaseJsonConsumedByAutoconfig()
    {
        _service.SaveStore(_profileDir, new PasskeyStoreFile
        {
            Credentials =
            {
                new ProfilePasskey
                {
                    CredentialId = "cred-1",
                    RpId = "localhost",
                    UserHandle = "handle",
                    PrivateKey = "key",
                    IsResidentCredential = true
                }
            }
        });

        var json = File.ReadAllText(_service.GetStorePath(_profileDir));

        Assert.Contains("\"credentialId\": \"cred-1\"", json);
        Assert.Contains("\"rpId\": \"localhost\"", json);
        Assert.Contains("\"privateKey\": \"key\"", json);
        Assert.Contains("\"isResidentCredential\": true", json);
    }

    [Fact]
    public void DeletePasskey_RemovesOnlyMatchingCredential()
    {
        _service.SaveStore(_profileDir, new PasskeyStoreFile
        {
            Credentials =
            {
                new ProfilePasskey { CredentialId = "keep", RpId = "a.com", PrivateKey = "k1" },
                new ProfilePasskey { CredentialId = "drop", RpId = "b.com", PrivateKey = "k2" }
            }
        });

        var deleted = _service.DeletePasskey(_profileDir, "drop");

        Assert.True(deleted);
        var remaining = _service.GetPasskeys(_profileDir);
        Assert.Single(remaining);
        Assert.Equal("keep", remaining[0].CredentialId);
    }

    [Fact]
    public void DeletePasskey_UnknownCredential_ReturnsFalse()
    {
        _service.SaveStore(_profileDir, new PasskeyStoreFile
        {
            Credentials = { new ProfilePasskey { CredentialId = "keep", RpId = "a.com" } }
        });

        Assert.False(_service.DeletePasskey(_profileDir, "missing"));
        Assert.Single(_service.GetPasskeys(_profileDir));
    }

    [Fact]
    public void LoadStore_WithMalformedJson_ReturnsEmpty()
    {
        File.WriteAllText(_service.GetStorePath(_profileDir), "{ this is not json");

        Assert.Empty(_service.GetPasskeys(_profileDir));
    }

    [Fact]
    public void SerializeForExport_ContainsCredentials()
    {
        var store = new PasskeyStoreFile();
        store.Credentials.Add(new ProfilePasskey { CredentialId = "cred", RpId = "x.com", PrivateKey = "pk" });

        var json = _service.SerializeForExport(store);

        Assert.Contains("\"credentialId\": \"cred\"", json);
        Assert.Contains("\"privateKey\": \"pk\"", json);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_profileDir))
                Directory.Delete(_profileDir, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
