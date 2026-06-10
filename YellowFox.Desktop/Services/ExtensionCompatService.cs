using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YellowFox.Desktop.Services;

internal static class ExtensionCompatService
{
    private const string FirefoxIdSuffix = "@yellowfox.local";

    private static readonly HashSet<string> ChromeOnlyManifestKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "minimum_chrome_version",
        "update_url",
        "differential_fingerprint",
        "key",
        "nacl_modules",
        "platforms",
        "storage",
        "current_locale"
    };

    private static readonly HashSet<string> ChromeOnlyPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "debugger",
        "declarativeContent",
        "declarativeNetRequestFeedback",
        "enterprise.deviceAttributes",
        "enterprise.hardwarePlatform",
        "enterprise.networkingAttributes",
        "enterprise.platformKeys",
        "fileBrowserHandler",
        "fileSystemProvider",
        "fontSettings",
        "gcm",
        "identity",
        "identity.email",
        "documentScan",
        "loginState",
        "platformKeys",
        "printing",
        "printingMetrics",
        "processes",
        "sessions",
        "signedInDevices",
        "tabGroups",
        "ttsEngine",
        "wallpaper"
    };

    private static readonly HashSet<string> ChromeHardPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "declarativeNetRequest",
        "webRequestAuthProvider"
    };

    private static readonly Regex[] ChromeHardScriptUsagePatterns =
    {
        new(@"chrome\.declarativeNetRequest", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"browser\.declarativeNetRequest", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
        new(@"chrome\.webRequestAuthProvider", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled)
    };

    public static string? NormalizeManifestForFirefox(string extensionPath, string extensionName)
    {
        return NormalizeManifestForFirefox(extensionPath, extensionName, out _, out _);
    }

    public static string? NormalizeManifestForFirefox(string extensionPath, string extensionName, out bool isCompatible)
    {
        return NormalizeManifestForFirefox(extensionPath, extensionName, out isCompatible, out _);
    }

    public static string? NormalizeManifestForFirefox(string extensionPath, string extensionName, out bool isCompatible, out bool hasWarnings)
    {
        isCompatible = true;
        hasWarnings = false;

        if (string.IsNullOrWhiteSpace(extensionPath))
            return null;

        if (!Directory.Exists(extensionPath))
            return null;

        var manifestPath = Path.Combine(extensionPath, "manifest.json");
        if (!File.Exists(manifestPath))
            return null;

        var root = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
        if (root == null)
        {
            isCompatible = false;
            hasWarnings = false;
            return "Manifest is invalid JSON and cannot be normalized for Firefox compatibility.";
        }

        var hardIncompatibilities = new List<string>();
        DetectHardChromeIncompatibilities(root, extensionPath, hardIncompatibilities);
        if (hardIncompatibilities.Count > 0)
        {
            isCompatible = false;
            hasWarnings = true;
            return $"Blocked: unsupported Chrome-specific APIs/permissions detected: {string.Join(", ", hardIncompatibilities)}.";
        }

        var notes = new List<string>();
        var changed = false;
        var idWasGenerated = false;

        var chromeIndicators = new List<string>();
        DetectChromeIndicators(root, chromeIndicators);
        if (chromeIndicators.Count > 0)
            notes.Add($"Detected Chrome-style manifest fields: {string.Join(", ", chromeIndicators)}.");

        if (!HasFirefoxId(root, out var existingId))
        {
            var generatedId = GenerateStableFirefoxId(extensionPath, extensionName, root);
            EnsureFirefoxId(root, generatedId);
            notes.Add($"Generated a local Firefox add-on id: {generatedId}.");
            changed = true;
            idWasGenerated = true;
            existingId = generatedId;
        }

        if (TryConvertManifestV3Settings(root, notes))
            changed = true;

        if (TryConvertUnsupportedManifestKeys(root, notes))
            changed = true;

        if (TryConvertPermissions(root, notes))
            changed = true;

        if (TryConvertOptionalPermissions(root, notes))
            changed = true;

        if (TryConvertHostPermissions(root, notes))
            changed = true;

        if (TryConvertExternallyConnectable(root, notes))
            changed = true;

        if (TryConvertOptionsPage(root, notes))
            changed = true;

        if (changed)
        {
            File.WriteAllText(
                manifestPath,
                root.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }

        if (string.IsNullOrWhiteSpace(existingId) || !existingId.EndsWith(FirefoxIdSuffix, StringComparison.OrdinalIgnoreCase))
            return notes.Count == 0 ? null : string.Join(" ", notes);

        if (notes.Count == 0)
            return null;

        if (idWasGenerated && !notes.Any(note => note.Contains("Generated a local Firefox add-on id", StringComparison.OrdinalIgnoreCase)))
            return string.Join(" ", notes) + " This add-on id was generated for local import compatibility.";

        return string.Join(" ", notes);
    }

    private static void DetectChromeIndicators(JsonObject manifest, List<string> indicators)
    {
        foreach (var key in ChromeOnlyManifestKeys)
        {
            if (manifest.ContainsKey(key))
                indicators.Add(key);
        }

        if (TryGetJsonObject(manifest, "background", out var background) && background.ContainsKey("service_worker"))
            indicators.Add("background.service_worker");

        if (manifest.TryGetPropertyValue("externally_connectable", out var externallyConnectableNode)
            && externallyConnectableNode is JsonObject externallyConnectable
            && externallyConnectable.ContainsKey("accepts_tls_channel_id"))
        {
            indicators.Add("externally_connectable.accepts_tls_channel_id");
        }

        if (manifest.ContainsKey("permissions") && manifest["permissions"] is JsonArray permissions)
        {
            foreach (var permission in permissions)
            {
                var permissionName = permission?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(permissionName) && ChromeOnlyPermissions.Contains(permissionName))
                indicators.Add($"permissions.{permissionName}");
            }
        }
    }

    private static void DetectHardChromeIncompatibilities(JsonObject manifest, string extensionPath, List<string> issues)
    {
        if (TryGetJsonArray(manifest, "permissions", out var permissions))
            DetectHardPermissions(permissions, issues);

        if (TryGetJsonArray(manifest, "optional_permissions", out var optionalPermissions))
            DetectHardPermissions(optionalPermissions, issues);

        if (TryGetJsonArray(manifest, "host_permissions", out var hostPermissions))
            DetectHardPermissions(hostPermissions, issues);

        foreach (var pattern in ChromeHardScriptUsagePatterns)
        {
            if (AnyScriptContainsBlockedPattern(extensionPath, pattern))
            {
                if (!issues.Contains("chrome.declarativeNetRequest script usage"))
                    issues.Add("chrome.declarativeNetRequest script usage");
            }
        }
    }

    private static void DetectHardPermissions(JsonArray permissions, List<string> issues)
    {
        foreach (var permission in permissions.OfType<JsonValue>())
        {
            var permissionName = permission?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(permissionName))
                continue;

            if (ChromeHardPermissions.Contains(permissionName) && !issues.Contains(permissionName))
                issues.Add(permissionName);
        }
    }

    private static bool AnyScriptContainsBlockedPattern(string extensionPath, Regex pattern)
    {
        try
        {
            foreach (var scriptPath in Directory.EnumerateFiles(extensionPath, "*.js", SearchOption.AllDirectories))
            {
                string content;
                try
                {
                    content = File.ReadAllText(scriptPath);
                }
                catch
                {
                    continue;
                }

                if (pattern.IsMatch(content))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool HasFirefoxId(JsonObject manifest, out string? id)
    {
        id = null;

        if (TryGetJsonObject(manifest, "browser_specific_settings", out var browserSettings) &&
            TryGetJsonObject(browserSettings, "gecko", out var gecko) &&
            gecko.TryGetPropertyValue("id", out var rawId))
        {
            id = rawId?.GetValue<string>()?.Trim();
            return !string.IsNullOrWhiteSpace(id);
        }

        if (TryGetJsonObject(manifest, "applications", out var applications) &&
            TryGetJsonObject(applications, "gecko", out var applicationsGecko) &&
            applicationsGecko.TryGetPropertyValue("id", out var rawApplicationsId))
        {
            id = rawApplicationsId?.GetValue<string>()?.Trim();
            return !string.IsNullOrWhiteSpace(id);
        }

        return false;
    }

    private static void EnsureFirefoxId(JsonObject manifest, string generatedId)
    {
        var hasBrowserSettings = manifest.TryGetPropertyValue("browser_specific_settings", out var rawBrowserSettings)
            && rawBrowserSettings is JsonObject;
        var browserSettings = hasBrowserSettings
            ? (JsonObject)rawBrowserSettings!
            : new JsonObject();

        var hasGeckoSettings = browserSettings.TryGetPropertyValue("gecko", out var rawGecko)
            && rawGecko is JsonObject;
        var geckoSettings = hasGeckoSettings
            ? (JsonObject)rawGecko!
            : new JsonObject();

        geckoSettings["id"] = generatedId;
        if (!hasGeckoSettings)
            browserSettings["gecko"] = geckoSettings;
        if (!hasBrowserSettings)
            manifest["browser_specific_settings"] = browserSettings;
    }

    private static bool HasBrowserAction(JsonObject manifest)
    {
        return manifest.ContainsKey("browser_action") || manifest.ContainsKey("page_action");
    }

    private static int ReadManifestVersion(JsonObject manifest)
    {
        if (!manifest.TryGetPropertyValue("manifest_version", out var rawVersion))
            return 2;

        if (rawVersion is JsonValue valueNode && valueNode.TryGetValue<int>(out var version))
            return version;

        return 2;
    }

    private static bool TryConvertManifestV3Settings(JsonObject manifest, List<string> notes)
    {
        var changed = false;

        if (TryGetJsonObject(manifest, "background", out var background))
        {
            if (background.TryGetPropertyValue("service_worker", out var serviceWorker) && serviceWorker != null)
            {
                var serviceWorkerName = serviceWorker.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(serviceWorkerName))
                {
                    var scriptsNode = background.TryGetPropertyValue("scripts", out var rawScripts) && rawScripts is JsonArray rawScriptsArray
                        ? rawScriptsArray
                        : new JsonArray();

                    if (!ContainsScript(scriptsNode, serviceWorkerName))
                        scriptsNode.Add(serviceWorkerName);

                    background["scripts"] = scriptsNode;
                }

                background.Remove("service_worker");
                background.Remove("type");
                notes.Add("Converted background.service_worker to background.scripts for Firefox.");
                changed = true;
            }

            if (background.TryGetPropertyValue("persistent", out var persistentNode) &&
                persistentNode is JsonValue persistentValue &&
                persistentValue.TryGetValue<bool>(out var isPersistent) &&
                isPersistent)
            {
                background["persistent"] = false;
                notes.Add("Set background.persistent=false for Firefox compatibility.");
                changed = true;
            }
        }

        if (!HasBrowserAction(manifest) && TryGetJsonObject(manifest, "action", out var action))
        {
            manifest["browser_action"] = action.DeepClone();
            notes.Add("Copied manifest.action to manifest.browser_action.");
            changed = true;
        }

        return changed;
    }

    private static bool TryConvertUnsupportedManifestKeys(JsonObject manifest, List<string> notes)
    {
        var changed = false;
        foreach (var key in ChromeOnlyManifestKeys)
        {
            if (manifest.Remove(key))
            {
                changed = true;
                notes.Add($"Removed Chrome-only manifest key '{key}'.");
            }
        }

        return changed;
    }

    private static bool TryConvertPermissions(JsonObject manifest, List<string> notes)
    {
        if (!TryGetJsonArray(manifest, "permissions", out var permissions))
            return false;

        if (permissions.Count == 0)
            return false;

        var changed = false;
        var removedPermissions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var filtered = new JsonArray();

        foreach (var permission in permissions)
        {
            var permissionName = permission?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(permissionName))
                continue;

            if (ChromeOnlyPermissions.Contains(permissionName))
            {
                changed = true;
                removedPermissions.Add(permissionName);
                continue;
            }

            if (permission != null)
                filtered.Add(permission.DeepClone());
        }

        if (changed)
        {
            manifest["permissions"] = filtered;
            notes.Add($"Removed unsupported permissions for Firefox: {string.Join(", ", removedPermissions)}.");
        }

        return changed;
    }

    private static bool TryConvertOptionalPermissions(JsonObject manifest, List<string> notes)
    {
        if (!TryGetJsonArray(manifest, "optional_permissions", out var optionalPermissions))
            return false;

        if (optionalPermissions.Count == 0)
            return false;

        var changed = false;
        var removedPermissions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var filtered = new JsonArray();

        foreach (var permission in optionalPermissions)
        {
            var permissionName = permission?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(permissionName))
                continue;

            if (ChromeOnlyPermissions.Contains(permissionName))
            {
                changed = true;
                removedPermissions.Add(permissionName);
                continue;
            }

            if (permission != null)
                filtered.Add(permission.DeepClone());
        }

        if (changed)
        {
            manifest["optional_permissions"] = filtered;
            notes.Add($"Removed unsupported optional_permissions for Firefox: {string.Join(", ", removedPermissions)}.");
        }

        return changed;
    }

    private static bool TryConvertHostPermissions(JsonObject manifest, List<string> notes)
    {
        var manifestVersion = ReadManifestVersion(manifest);
        if (manifestVersion > 2)
            return false;

        if (!TryGetJsonArray(manifest, "host_permissions", out var hostPermissions) || hostPermissions.Count == 0)
            return false;

        var hasPermissions = manifest.TryGetPropertyValue("permissions", out var rawPermissions) && rawPermissions is JsonArray;
        var permissions = hasPermissions
            ? (JsonArray)rawPermissions!
            : new JsonArray();

        var changed = false;
        foreach (var permission in hostPermissions)
        {
            var value = permission?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!ContainsPermission(permissions, value))
            {
                permissions.Add(value);
                changed = true;
            }
        }

        if (changed)
        {
            if (!hasPermissions)
                manifest["permissions"] = permissions;
            manifest.Remove("host_permissions");
            notes.Add("Moved host_permissions to permissions for Firefox compatibility.");
        }

        return changed;
    }

    private static bool TryConvertExternallyConnectable(JsonObject manifest, List<string> notes)
    {
        if (!TryGetJsonObject(manifest, "externally_connectable", out var externalConnectable))
            return false;

        if (!externalConnectable.TryGetPropertyValue("ids", out var rawIds) || rawIds is not JsonArray ids || ids.Count == 0)
        {
            if (externalConnectable.ContainsKey("accepts_tls_channel_id"))
            {
                externalConnectable.Remove("accepts_tls_channel_id");
                notes.Add("Removed externally_connectable.accepts_tls_channel_id (Firefox unsupported).");
                return true;
            }

            return false;
        }

        var changed = false;
        var normalizedIds = new JsonArray();

        foreach (var entry in ids)
        {
            var id = entry?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(id))
                continue;

            if (id == "*")
            {
                normalizedIds.Add("*");
                continue;
            }

            var normalizedId = id;
            if (!id.Contains("@") && !id.Contains("-") && !id.Contains("."))
            {
                normalizedId = "*";
                changed = true;
            }

            normalizedIds.Add(normalizedId);
        }

        if (changed)
        {
            externalConnectable["ids"] = normalizedIds;
            notes.Add("Normalized externally_connectable.ids to Firefox-compatible values.");
            if (externalConnectable.ContainsKey("accepts_tls_channel_id"))
            {
                externalConnectable.Remove("accepts_tls_channel_id");
                notes.Add("Removed externally_connectable.accepts_tls_channel_id (Firefox unsupported).");
            }
        }
        else if (externalConnectable.ContainsKey("accepts_tls_channel_id"))
        {
            externalConnectable.Remove("accepts_tls_channel_id");
            notes.Add("Removed externally_connectable.accepts_tls_channel_id (Firefox unsupported).");
            changed = true;
        }

        return changed;
    }

    private static bool TryConvertOptionsPage(JsonObject manifest, List<string> notes)
    {
        if (manifest.ContainsKey("options_ui") || !manifest.ContainsKey("options_page"))
            return false;

        if (!manifest.TryGetPropertyValue("options_page", out var optionsPageNode) || optionsPageNode == null)
            return false;

        var optionsPage = optionsPageNode.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(optionsPage))
            return false;

        manifest.Remove("options_page");
        manifest["options_ui"] = new JsonObject
        {
            ["page"] = optionsPage,
            ["open_in_tab"] = true
        };

        notes.Add("Converted options_page to options_ui with open_in_tab=true.");
        return true;
    }

    private static bool ContainsScript(JsonArray scripts, string script)
    {
        return scripts.OfType<JsonValue>().Any(entry => string.Equals(entry.GetValue<string>(), script, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ContainsPermission(JsonArray permissions, string permission)
    {
        return permissions.OfType<JsonValue>().Any(entry => string.Equals(entry.GetValue<string>(), permission, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryGetJsonObject(JsonObject parent, string key, out JsonObject value)
    {
        value = null!;
        if (!parent.TryGetPropertyValue(key, out var rawValue) || rawValue is not JsonObject objectValue)
            return false;

        value = objectValue;
        return true;
    }

    private static bool TryGetJsonArray(JsonObject parent, string key, out JsonArray value)
    {
        value = null!;
        if (!parent.TryGetPropertyValue(key, out var rawValue) || rawValue is not JsonArray arrayValue)
            return false;

        value = arrayValue;
        return true;
    }

    private static string GenerateStableFirefoxId(string extensionPath, string extensionName, JsonObject manifest)
    {
        var name = manifest.TryGetPropertyValue("name", out var rawName)
            ? rawName?.GetValue<string>()
            : null;
        var keySeed = manifest.TryGetPropertyValue("key", out var rawKey)
            ? rawKey?.GetValue<string>()?.Trim()
            : null;

        var baseName = SanitizeIdSeed(name ?? keySeed ?? extensionName);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = SanitizeIdSeed(Path.GetFileName(extensionPath));
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "yellowfox-extension";
        }

        var seed = $"{baseName}|{extensionPath}|{name}|{keySeed}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var suffix = Convert.ToHexString(hash).ToLowerInvariant()[..12];
        return $"{baseName}-{suffix}{FirefoxIdSuffix}";
    }

    private static string SanitizeIdSeed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = Regex.Replace(name.Trim().ToLowerInvariant(), @"[^a-z0-9.\-]+", "-", RegexOptions.CultureInvariant);
        normalized = normalized.Trim('-');
        return normalized.Length > 24 ? normalized.Substring(0, 24) : normalized;
    }
}
