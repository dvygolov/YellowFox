using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace YellowFox.Desktop.Models;

/// <summary>
/// A WebAuthn credential stored for a profile in <c>yellowfox-passkeys.json</c>.
/// The private key is kept so the entry can be written back / exported, but the
/// UI only ever binds to the display helpers below.
/// </summary>
public class ProfilePasskey
{
    public string CredentialId { get; set; } = string.Empty;
    public bool IsResidentCredential { get; set; }
    public string RpId { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string UserHandle { get; set; } = string.Empty;
    public int SignCount { get; set; }

    [JsonIgnore]
    public string RpIdDisplay => string.IsNullOrWhiteSpace(RpId) ? "(unknown RP)" : RpId;

    [JsonIgnore]
    public string UserHandleDisplay => string.IsNullOrWhiteSpace(UserHandle) ? "—" : UserHandle;

    [JsonIgnore]
    public string CredentialIdShort =>
        string.IsNullOrWhiteSpace(CredentialId)
            ? "—"
            : CredentialId.Length <= 18
                ? CredentialId
                : $"{CredentialId[..10]}…{CredentialId[^6..]}";

    [JsonIgnore]
    public string TypeDisplay => IsResidentCredential ? "Passkey (resident)" : "Server-side";

    [JsonIgnore]
    public bool HasPrivateKey => !string.IsNullOrWhiteSpace(PrivateKey);
}

public class PasskeyStoreFile
{
    public int Version { get; set; } = 1;
    public List<ProfilePasskey> Credentials { get; set; } = new();
}
