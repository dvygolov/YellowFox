using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using YellowFox.Desktop.Models;

namespace YellowFox.Desktop.Services;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly string _dataDirectory;
    
    public DatabaseService()
    {
        // Setup data directory relative to application
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        _dataDirectory = Path.Combine(appDir, "data");
        Directory.CreateDirectory(_dataDirectory);
        
        var dbPath = Path.Combine(_dataDirectory, "yellowfox.db");
        _connectionString = $"Data Source={dbPath}";
        
        InitializeDatabase();
    }
    
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS profiles (
                id TEXT PRIMARY KEY,
                name TEXT NOT NULL UNIQUE,
                notes TEXT,
                fingerprint_config TEXT NOT NULL
            )";
        command.ExecuteNonQuery();
    }
    
    public List<Profile> GetAllProfiles()
    {
        var profiles = new List<Profile>();
        
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, notes, fingerprint_config FROM profiles ORDER BY name";
        
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var profile = new Profile
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
                FingerprintConfig = JsonSerializer.Deserialize<FingerprintConfig>(reader.GetString(3))
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
        command.CommandText = "SELECT id, name, notes, fingerprint_config FROM profiles WHERE id = @id";
        command.Parameters.AddWithValue("@id", id);
        
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return new Profile
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Notes = reader.IsDBNull(2) ? null : reader.GetString(2),
                FingerprintConfig = JsonSerializer.Deserialize<FingerprintConfig>(reader.GetString(3))
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
            INSERT INTO profiles (id, name, notes, fingerprint_config)
            VALUES (@id, @name, @notes, @config)";
        
        command.Parameters.AddWithValue("@id", profile.Id);
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@notes", (object?)profile.Notes ?? DBNull.Value);
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
            SET name = @name, notes = @notes, fingerprint_config = @config
            WHERE id = @id";
        
        command.Parameters.AddWithValue("@id", profile.Id);
        command.Parameters.AddWithValue("@name", profile.Name);
        command.Parameters.AddWithValue("@notes", (object?)profile.Notes ?? DBNull.Value);
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
}
