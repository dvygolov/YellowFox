using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dataDirectory;
    
    public DatabaseService(string? dataDirectory = null, bool disablePooling = false)
    {
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            _dataDirectory = dataDirectory;
        }
        else
        {
            // Setup data directory relative to application
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            _dataDirectory = Path.Combine(appDir, "data");
        }

        Directory.CreateDirectory(_dataDirectory);
        
        var dbPath = Path.Combine(_dataDirectory, "yellowfox.db");
        _connectionString = disablePooling
            ? $"Data Source={dbPath};Pooling=False"
            : $"Data Source={dbPath}";
        
        InitializeDatabase();
    }
    
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();

        var profilesCommand = connection.CreateCommand();
        profilesCommand.Transaction = transaction;
        profilesCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                notes TEXT,
                proxy_id TEXT,
                dolphin_profile_id TEXT,
                fingerprint_config TEXT NOT NULL
            )";
        profilesCommand.ExecuteNonQuery();

        var proxiesCommand = connection.CreateCommand();
        proxiesCommand.Transaction = transaction;
        proxiesCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS proxies (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                type TEXT NOT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                username TEXT,
                password TEXT,
                dolphin_proxy_id TEXT,
                is_enabled INTEGER NOT NULL DEFAULT 1
            )";
        proxiesCommand.ExecuteNonQuery();

        var extensionsCommand = connection.CreateCommand();
        extensionsCommand.Transaction = transaction;
        extensionsCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS extensions (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                path TEXT NOT NULL,
                is_enabled INTEGER NOT NULL DEFAULT 1
            )";
        extensionsCommand.ExecuteNonQuery();

        var bookmarksCommand = connection.CreateCommand();
        bookmarksCommand.Transaction = transaction;
        bookmarksCommand.CommandText = @"
            CREATE TABLE IF NOT EXISTS bookmarks (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                url TEXT NOT NULL,
                folder TEXT
            )";
        bookmarksCommand.ExecuteNonQuery();

        if (!ColumnExists(connection, transaction, "profiles", "proxy_id"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE profiles ADD COLUMN proxy_id TEXT";
            alterTableCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, transaction, "profiles", "dolphin_profile_id"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE profiles ADD COLUMN dolphin_profile_id TEXT";
            alterTableCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, transaction, "proxies", "dolphin_proxy_id"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE proxies ADD COLUMN dolphin_proxy_id TEXT";
            alterTableCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, transaction, "bookmarks", "parent_id"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE bookmarks ADD COLUMN parent_id TEXT";
            alterTableCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, transaction, "bookmarks", "is_folder"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE bookmarks ADD COLUMN is_folder INTEGER NOT NULL DEFAULT 0";
            alterTableCommand.ExecuteNonQuery();
        }

        if (!ColumnExists(connection, transaction, "bookmarks", "sort_order"))
        {
            var alterTableCommand = connection.CreateCommand();
            alterTableCommand.Transaction = transaction;
            alterTableCommand.CommandText = "ALTER TABLE bookmarks ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0";
            alterTableCommand.ExecuteNonQuery();
        }

        MigrateBookmarkFolders(connection, transaction);

        var indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_profiles_proxy_id ON profiles(proxy_id)";
        indexCommand.ExecuteNonQuery();

        var dolphinProfileIndexCommand = connection.CreateCommand();
        dolphinProfileIndexCommand.Transaction = transaction;
        dolphinProfileIndexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_profiles_dolphin_profile_id ON profiles(dolphin_profile_id) WHERE dolphin_profile_id IS NOT NULL";
        dolphinProfileIndexCommand.ExecuteNonQuery();

        var dolphinProxyIndexCommand = connection.CreateCommand();
        dolphinProxyIndexCommand.Transaction = transaction;
        dolphinProxyIndexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_proxies_dolphin_proxy_id ON proxies(dolphin_proxy_id) WHERE dolphin_proxy_id IS NOT NULL";
        dolphinProxyIndexCommand.ExecuteNonQuery();

        var bookmarksParentIndexCommand = connection.CreateCommand();
        bookmarksParentIndexCommand.Transaction = transaction;
        bookmarksParentIndexCommand.CommandText = "CREATE INDEX IF NOT EXISTS idx_bookmarks_parent_id ON bookmarks(parent_id)";
        bookmarksParentIndexCommand.ExecuteNonQuery();

        transaction.Commit();
    }

    private static void MigrateBookmarkFolders(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string Folder)>();
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = @"
                SELECT id, folder
                FROM bookmarks
                WHERE parent_id IS NULL
                  AND is_folder = 0
                  AND folder IS NOT NULL
                  AND TRIM(folder) <> ''";

            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        var folderCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var parentId = EnsureBookmarkFolderPath(connection, transaction, row.Folder, folderCache);
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE bookmarks SET parent_id = @parent_id WHERE id = @id";
            update.Parameters.AddWithValue("@parent_id", string.IsNullOrWhiteSpace(parentId) ? DBNull.Value : parentId);
            update.Parameters.AddWithValue("@id", row.Id);
            update.ExecuteNonQuery();
        }
    }

    private static string EnsureBookmarkFolderPath(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string folderPath,
        Dictionary<string, string> folderCache)
    {
        string? parentId = null;
        var pathParts = folderPath
            .Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        foreach (var part in pathParts)
        {
            var cacheKey = $"{parentId ?? string.Empty}/{part}";
            if (!folderCache.TryGetValue(cacheKey, out var folderId))
            {
                folderId = FindBookmarkFolder(connection, transaction, parentId, part)
                    ?? CreateBookmarkFolder(connection, transaction, parentId, part);
                folderCache[cacheKey] = folderId;
            }

            parentId = folderId;
        }

        return parentId ?? string.Empty;
    }

    private static string? FindBookmarkFolder(SqliteConnection connection, SqliteTransaction transaction, string? parentId, string title)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = parentId == null
            ? "SELECT id FROM bookmarks WHERE parent_id IS NULL AND is_folder = 1 AND title = @title LIMIT 1"
            : "SELECT id FROM bookmarks WHERE parent_id = @parent_id AND is_folder = 1 AND title = @title LIMIT 1";
        if (parentId != null)
            command.Parameters.AddWithValue("@parent_id", parentId);
        command.Parameters.AddWithValue("@title", title.Trim());
        return command.ExecuteScalar() as string;
    }

    private static string CreateBookmarkFolder(SqliteConnection connection, SqliteTransaction transaction, string? parentId, string title)
    {
        var id = Guid.NewGuid().ToString();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO bookmarks (id, title, url, folder, parent_id, is_folder, sort_order)
            VALUES (@id, @title, '', NULL, @parent_id, 1, @sort_order)";
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@title", title.Trim());
        command.Parameters.AddWithValue("@parent_id", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@sort_order", NextBookmarkSortOrder(connection, transaction, parentId));
        command.ExecuteNonQuery();
        return id;
    }

    private static int NextBookmarkSortOrder(SqliteConnection connection, SqliteTransaction? transaction, string? parentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = parentId == null
            ? "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM bookmarks WHERE parent_id IS NULL"
            : "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM bookmarks WHERE parent_id = @parent_id";
        if (parentId != null)
            command.Parameters.AddWithValue("@parent_id", parentId);
        return Convert.ToInt32(command.ExecuteScalar());
    }
    
    public List<Profile> GetAllProfiles()
    {
        var profiles = new List<Profile>();
        
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, notes, proxy_id, dolphin_profile_id, fingerprint_config FROM profiles ORDER BY name";
        
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profile = new Profile
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Notes = reader.IsDBNull(2) ? null : TextSanitizer.HtmlToPlainText(reader.GetString(2)),
                ProxyId = reader.IsDBNull(3) ? null : reader.GetString(3),
                DolphinProfileId = reader.IsDBNull(4) ? null : reader.GetString(4),
                FingerprintConfig = JsonSerializer.Deserialize<FingerprintConfig>(reader.GetString(5))
                    ?? new FingerprintConfig()
            };
            profiles.Add(profile);
        }
        
        return profiles;
    }
    
    public Profile? GetProfile(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, notes, proxy_id, dolphin_profile_id, fingerprint_config FROM profiles WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new Profile
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Notes = reader.IsDBNull(2) ? null : TextSanitizer.HtmlToPlainText(reader.GetString(2)),
                ProxyId = reader.IsDBNull(3) ? null : reader.GetString(3),
                DolphinProfileId = reader.IsDBNull(4) ? null : reader.GetString(4),
                FingerprintConfig = JsonSerializer.Deserialize<FingerprintConfig>(reader.GetString(5))
                    ?? new FingerprintConfig()
            };
        }
        
        return null;
    }
    
    public void CreateProfile(Profile profile)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO profiles (id, name, notes, proxy_id, dolphin_profile_id, fingerprint_config)
            VALUES (@id, @name, @notes, @proxy_id, @dolphin_profile_id, @config)";
        
        command.Parameters.AddWithValue("@id", profile.Id);
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@notes", ToDbNotesValue(profile.Notes));
        command.Parameters.AddWithValue("@proxy_id", (object?)profile.ProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@dolphin_profile_id", (object?)profile.DolphinProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@config", JsonSerializer.Serialize(profile.FingerprintConfig));
        
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            throw new InvalidOperationException($"A profile with the name '{profile.Name}' already exists.", ex);
        }
    }
    
    public void UpdateProfile(Profile profile)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE profiles
            SET name = @name, notes = @notes, proxy_id = @proxy_id, dolphin_profile_id = @dolphin_profile_id, fingerprint_config = @config
            WHERE id = @id";
        
        command.Parameters.AddWithValue("@id", profile.Id);
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@notes", ToDbNotesValue(profile.Notes));
        command.Parameters.AddWithValue("@proxy_id", (object?)profile.ProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@dolphin_profile_id", (object?)profile.DolphinProfileId ?? DBNull.Value);
        command.Parameters.AddWithValue("@config", JsonSerializer.Serialize(profile.FingerprintConfig));
        
        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE constraint
        {
            throw new InvalidOperationException($"A profile with the name '{profile.Name}' already exists.", ex);
        }
    }
    
    public void DeleteProfile(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM profiles WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        
        command.ExecuteNonQuery();
    }
    
    public Profile CloneProfile(string sourceId, string newName)
    {
        var source = GetProfile(sourceId);
        if (source == null)
            throw new InvalidOperationException($"Profile {sourceId} not found");
        
        var clone = new Profile
        {
            Id = Guid.NewGuid().ToString(),
            Name = newName,
            Notes = source.Notes,
            ProxyId = source.ProxyId,
            DolphinProfileId = null,
            FingerprintConfig = new FingerprintConfig
            {
                Os = source.FingerprintConfig.Os,
                Screen = new ScreenConfig
                {
                    MaxWidth = source.FingerprintConfig.Screen.MaxWidth,
                    MaxHeight = source.FingerprintConfig.Screen.MaxHeight
                }
            }
        };
        
        CreateProfile(clone);
        return clone;
    }
    
    public string GetProfileDataDirectory(string profileId)
    {
        var profileDir = Path.Combine(_dataDirectory, "profiles", profileId);
        Directory.CreateDirectory(profileDir);
        return profileDir;
    }

    public string GetProfileLogsDirectory(string profileId)
    {
        var logsDir = Path.Combine(GetProfileDataDirectory(profileId), "logs");
        Directory.CreateDirectory(logsDir);
        return logsDir;
    }

    public string GetProfileLogFilePath(string profileId, string? profileName = null)
    {
        var logsDir = GetProfileLogsDirectory(profileId);
        var baseName = string.IsNullOrWhiteSpace(profileName) ? profileId : profileName;
        var safeName = SanitizeFileName(baseName);
        return Path.Combine(logsDir, $"{safeName}.log");
    }

    public string GetProfileTabsStateFilePath(string profileId)
    {
        var profileDir = GetProfileDataDirectory(profileId);
        return Path.Combine(profileDir, "tabs-state.json");
    }

    public string GetProfileImportedCookiesFilePath(string profileId)
    {
        var profileDir = GetProfileDataDirectory(profileId);
        return Path.Combine(profileDir, "imported-cookies.json");
    }

    public string GetProfileImportedLocalStorageFilePath(string profileId)
    {
        var profileDir = GetProfileDataDirectory(profileId);
        return Path.Combine(profileDir, "imported-local-storage.json");
    }

    public string GetExtensionsDataDirectory()
    {
        var extensionsDir = Path.Combine(_dataDirectory, "extensions");
        Directory.CreateDirectory(extensionsDir);
        return extensionsDir;
    }

    public List<Proxy> GetAllProxies()
    {
        var proxies = new List<Proxy>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, type, host, port, username, password, dolphin_proxy_id, is_enabled
            FROM proxies
            ORDER BY name";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            proxies.Add(new Proxy
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Type = reader.GetString(2),
                Host = reader.GetString(3),
                Port = reader.GetInt32(4),
                Username = reader.IsDBNull(5) ? null : reader.GetString(5),
                Password = reader.IsDBNull(6) ? null : reader.GetString(6),
                DolphinProxyId = reader.IsDBNull(7) ? null : reader.GetString(7),
                IsEnabled = reader.GetInt32(8) == 1
            });
        }

        return proxies;
    }

    public Proxy? GetProxy(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, type, host, port, username, password, dolphin_proxy_id, is_enabled
            FROM proxies
            WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new Proxy
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Type = reader.GetString(2),
            Host = reader.GetString(3),
            Port = reader.GetInt32(4),
            Username = reader.IsDBNull(5) ? null : reader.GetString(5),
            Password = reader.IsDBNull(6) ? null : reader.GetString(6),
            DolphinProxyId = reader.IsDBNull(7) ? null : reader.GetString(7),
            IsEnabled = reader.GetInt32(8) == 1
        };
    }

    public Profile? GetProfileByDolphinProfileId(string dolphinProfileId)
    {
        return GetAllProfiles().Find(p => string.Equals(p.DolphinProfileId, dolphinProfileId, StringComparison.Ordinal));
    }

    public Proxy? GetProxyByDolphinProxyId(string dolphinProxyId)
    {
        return GetAllProxies().Find(p => string.Equals(p.DolphinProxyId, dolphinProxyId, StringComparison.Ordinal));
    }

    public void CreateProxy(Proxy proxy)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO proxies (id, name, type, host, port, username, password, dolphin_proxy_id, is_enabled)
            VALUES (@id, @name, @type, @host, @port, @username, @password, @dolphin_proxy_id, @is_enabled)";

        command.Parameters.AddWithValue("@id", proxy.Id);
        command.Parameters.AddWithValue("@name", proxy.Name.Trim());
        command.Parameters.AddWithValue("@type", Proxy.NormalizeType(proxy.Type));
        command.Parameters.AddWithValue("@host", proxy.Host.Trim());
        command.Parameters.AddWithValue("@port", proxy.Port);
        command.Parameters.AddWithValue("@username", (object?)proxy.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("@password", (object?)proxy.Password ?? DBNull.Value);
        command.Parameters.AddWithValue("@dolphin_proxy_id", (object?)proxy.DolphinProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@is_enabled", proxy.IsEnabled ? 1 : 0);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A proxy with the name '{proxy.Name}' already exists.", ex);
        }
    }

    public void UpdateProxy(Proxy proxy)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE proxies
            SET name = @name,
                type = @type,
                host = @host,
                port = @port,
                username = @username,
                password = @password,
                dolphin_proxy_id = @dolphin_proxy_id,
                is_enabled = @is_enabled
            WHERE id = @id";

        command.Parameters.AddWithValue("@id", proxy.Id);
        command.Parameters.AddWithValue("@name", proxy.Name.Trim());
        command.Parameters.AddWithValue("@type", Proxy.NormalizeType(proxy.Type));
        command.Parameters.AddWithValue("@host", proxy.Host.Trim());
        command.Parameters.AddWithValue("@port", proxy.Port);
        command.Parameters.AddWithValue("@username", (object?)proxy.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("@password", (object?)proxy.Password ?? DBNull.Value);
        command.Parameters.AddWithValue("@dolphin_proxy_id", (object?)proxy.DolphinProxyId ?? DBNull.Value);
        command.Parameters.AddWithValue("@is_enabled", proxy.IsEnabled ? 1 : 0);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"A proxy with the name '{proxy.Name}' already exists.", ex);
        }
    }

    public void DeleteProxy(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var clearProfilesCommand = connection.CreateCommand();
        clearProfilesCommand.Transaction = transaction;
        clearProfilesCommand.CommandText = "UPDATE profiles SET proxy_id = NULL WHERE proxy_id = @proxy_id";
        clearProfilesCommand.Parameters.AddWithValue("@proxy_id", id);
        clearProfilesCommand.ExecuteNonQuery();

        var deleteProxyCommand = connection.CreateCommand();
        deleteProxyCommand.Transaction = transaction;
        deleteProxyCommand.CommandText = "DELETE FROM proxies WHERE id = @id";
        deleteProxyCommand.Parameters.AddWithValue("@id", id);
        deleteProxyCommand.ExecuteNonQuery();

        transaction.Commit();
    }

    public List<ExtensionItem> GetAllExtensions()
    {
        var extensions = new List<ExtensionItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, path, is_enabled
            FROM extensions
            ORDER BY name";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            extensions.Add(new ExtensionItem
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                IsEnabled = reader.GetInt32(3) == 1
            });
        }

        return extensions;
    }

    public List<ExtensionItem> GetEnabledExtensions()
    {
        var extensions = new List<ExtensionItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, name, path, is_enabled
            FROM extensions
            WHERE is_enabled = 1
            ORDER BY name";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            extensions.Add(new ExtensionItem
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Path = reader.GetString(2),
                IsEnabled = reader.GetInt32(3) == 1
            });
        }

        return extensions;
    }

    public void CreateExtension(ExtensionItem extension)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO extensions (id, name, path, is_enabled)
            VALUES (@id, @name, @path, @is_enabled)";

        command.Parameters.AddWithValue("@id", extension.Id);
        command.Parameters.AddWithValue("@name", extension.Name.Trim());
        command.Parameters.AddWithValue("@path", extension.Path.Trim());
        command.Parameters.AddWithValue("@is_enabled", extension.IsEnabled ? 1 : 0);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"An extension with the name '{extension.Name}' already exists.", ex);
        }
    }

    public void UpdateExtension(ExtensionItem extension)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE extensions
            SET name = @name, path = @path, is_enabled = @is_enabled
            WHERE id = @id";

        command.Parameters.AddWithValue("@id", extension.Id);
        command.Parameters.AddWithValue("@name", extension.Name.Trim());
        command.Parameters.AddWithValue("@path", extension.Path.Trim());
        command.Parameters.AddWithValue("@is_enabled", extension.IsEnabled ? 1 : 0);

        try
        {
            command.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException($"An extension with the name '{extension.Name}' already exists.", ex);
        }
    }

    public void DeleteExtension(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM extensions WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        command.ExecuteNonQuery();
    }

    public List<BookmarkItem> GetAllBookmarks()
    {
        var bookmarks = new List<BookmarkItem>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT id, title, url, folder, parent_id, is_folder, sort_order
            FROM bookmarks
            ORDER BY COALESCE(parent_id, ''), is_folder DESC, sort_order, title";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bookmarks.Add(new BookmarkItem
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                Url = reader.GetString(2),
                Folder = reader.IsDBNull(3) ? null : reader.GetString(3),
                ParentId = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsFolder = reader.GetInt32(5) == 1,
                SortOrder = reader.GetInt32(6)
            });
        }

        return bookmarks;
    }

    public void CreateBookmark(BookmarkItem bookmark)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO bookmarks (id, title, url, folder, parent_id, is_folder, sort_order)
            VALUES (@id, @title, @url, @folder, @parent_id, @is_folder, @sort_order)";

        command.Parameters.AddWithValue("@id", bookmark.Id);
        command.Parameters.AddWithValue("@title", bookmark.Title.Trim());
        command.Parameters.AddWithValue("@url", bookmark.IsFolder ? string.Empty : bookmark.Url.Trim());
        command.Parameters.AddWithValue("@folder", (object?)BookmarkFolderPath(bookmark, GetAllBookmarks()) ?? DBNull.Value);
        command.Parameters.AddWithValue("@parent_id", (object?)bookmark.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@is_folder", bookmark.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("@sort_order", bookmark.SortOrder > 0 ? bookmark.SortOrder : NextBookmarkSortOrder(connection, null, bookmark.ParentId));
        command.ExecuteNonQuery();
    }

    public void UpdateBookmark(BookmarkItem bookmark)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE bookmarks
            SET title = @title,
                url = @url,
                folder = @folder,
                parent_id = @parent_id,
                is_folder = @is_folder,
                sort_order = @sort_order
            WHERE id = @id";

        command.Parameters.AddWithValue("@id", bookmark.Id);
        command.Parameters.AddWithValue("@title", bookmark.Title.Trim());
        command.Parameters.AddWithValue("@url", bookmark.IsFolder ? string.Empty : bookmark.Url.Trim());
        command.Parameters.AddWithValue("@folder", (object?)BookmarkFolderPath(bookmark, GetAllBookmarks()) ?? DBNull.Value);
        command.Parameters.AddWithValue("@parent_id", (object?)bookmark.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@is_folder", bookmark.IsFolder ? 1 : 0);
        command.Parameters.AddWithValue("@sort_order", bookmark.SortOrder);
        command.ExecuteNonQuery();
    }

    public void DeleteBookmark(string id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();

        DeleteBookmarkTree(connection, transaction, id);
        transaction.Commit();
    }

    private static void DeleteBookmarkTree(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        var childIds = new List<string>();
        using (var children = connection.CreateCommand())
        {
            children.Transaction = transaction;
            children.CommandText = "SELECT id FROM bookmarks WHERE parent_id = @id";
            children.Parameters.AddWithValue("@id", id);
            using var reader = children.ExecuteReader();
            while (reader.Read())
                childIds.Add(reader.GetString(0));
        }

        foreach (var childId in childIds)
            DeleteBookmarkTree(connection, transaction, childId);

        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM bookmarks WHERE id = @id";
        delete.Parameters.AddWithValue("@id", id);
        delete.ExecuteNonQuery();
    }

    private static string? BookmarkFolderPath(BookmarkItem bookmark, IReadOnlyCollection<BookmarkItem> all)
    {
        if (bookmark.IsFolder)
            return null;

        if (string.IsNullOrWhiteSpace(bookmark.ParentId))
            return string.IsNullOrWhiteSpace(bookmark.Folder) ? null : bookmark.Folder.Trim();

        var byId = all.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var parts = new Stack<string>();
        var currentId = bookmark.ParentId;
        while (!string.IsNullOrWhiteSpace(currentId) && byId.TryGetValue(currentId, out var current))
        {
            if (current.IsFolder && !string.IsNullOrWhiteSpace(current.Title))
                parts.Push(current.Title.Trim());
            currentId = current.ParentId;
        }

        return parts.Count == 0 ? null : string.Join("/", parts);
    }

    private static bool ColumnExists(SqliteConnection connection, SqliteTransaction transaction, string tableName, string columnName)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var currentColumn = reader.GetString(1);
            if (string.Equals(currentColumn, columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(Array.IndexOf(invalidChars, ch) >= 0 ? '_' : ch);
        }

        var result = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "profile" : result;
    }

    private static object ToDbNotesValue(string? notes)
    {
        var plainText = TextSanitizer.HtmlToPlainText(notes);
        return string.IsNullOrWhiteSpace(plainText) ? DBNull.Value : plainText;
    }
}
