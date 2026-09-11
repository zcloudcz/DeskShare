using System.Text.Json;
using DeskShare.Desktop.Shared.Models;

namespace DeskShare.Desktop.Shared.Services;

/// <summary>
/// Manages saved connections persistence and connection history.
/// Platform-independent: uses standard System.IO and System.Text.Json.
/// Shared between WPF and Avalonia desktop clients.
/// </summary>
public class ConnectionManager
{
    private readonly string _connectionsFilePath;
    private readonly string _historyFilePath;
    private const int MaxHistoryEntries = 100; // Keep last 100 connections

    public ConnectionManager()
    {
        // Store connection data in the user's AppData folder
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RemoteDesktop.NET");

        Directory.CreateDirectory(appDataPath);
        _connectionsFilePath = Path.Combine(appDataPath, "connections.json");
        _historyFilePath = Path.Combine(appDataPath, "connection_history.json");
    }

    /// <summary>
    /// Loads all saved connections from disk.
    /// </summary>
    public async Task<List<SavedConnection>> LoadConnectionsAsync()
    {
        try
        {
            if (!File.Exists(_connectionsFilePath))
            {
                return new List<SavedConnection>();
            }

            var json = await File.ReadAllTextAsync(_connectionsFilePath);
            var connections = JsonSerializer.Deserialize<List<SavedConnection>>(json);
            return connections ?? new List<SavedConnection>();
        }
        catch (Exception)
        {
            // If file is corrupted, return empty list
            return new List<SavedConnection>();
        }
    }

    /// <summary>
    /// Saves or updates a connection entry.
    /// </summary>
    public async Task SaveConnectionAsync(SavedConnection connection)
    {
        var connections = await LoadConnectionsAsync();

        // Update existing or add new
        var existing = connections.FirstOrDefault(c => c.Id == connection.Id);
        if (existing != null)
        {
            existing.Name = connection.Name;
            existing.ServerId = connection.ServerId;
            existing.HasPassword = connection.HasPassword;
            existing.LastConnected = connection.LastConnected;
        }
        else
        {
            connections.Add(connection);
        }

        await SaveAllConnectionsAsync(connections);
    }

    /// <summary>
    /// Removes a saved connection by its ID.
    /// </summary>
    public async Task RemoveConnectionAsync(string connectionId)
    {
        var connections = await LoadConnectionsAsync();
        connections.RemoveAll(c => c.Id == connectionId);
        await SaveAllConnectionsAsync(connections);
    }

    private async Task SaveAllConnectionsAsync(List<SavedConnection> connections)
    {
        var json = JsonSerializer.Serialize(connections, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_connectionsFilePath, json);
    }

    #region Connection History Management

    /// <summary>
    /// Loads connection history from disk.
    /// </summary>
    public async Task<List<ConnectionHistoryEntry>> LoadHistoryAsync()
    {
        try
        {
            if (!File.Exists(_historyFilePath))
            {
                return new List<ConnectionHistoryEntry>();
            }

            var json = await File.ReadAllTextAsync(_historyFilePath);
            var history = JsonSerializer.Deserialize<List<ConnectionHistoryEntry>>(json);
            return history ?? new List<ConnectionHistoryEntry>();
        }
        catch (Exception)
        {
            return new List<ConnectionHistoryEntry>();
        }
    }

    /// <summary>
    /// Adds a new connection history entry.
    /// </summary>
    public async Task AddHistoryEntryAsync(ConnectionHistoryEntry entry)
    {
        var history = await LoadHistoryAsync();

        // Add new entry at the beginning (most recent first)
        history.Insert(0, entry);

        // Limit history size
        if (history.Count > MaxHistoryEntries)
        {
            history = history.Take(MaxHistoryEntries).ToList();
        }

        await SaveHistoryAsync(history);
    }

    /// <summary>
    /// Updates an existing history entry (e.g., when connection closes).
    /// </summary>
    public async Task UpdateHistoryEntryAsync(string entryId, Action<ConnectionHistoryEntry> updateAction)
    {
        var history = await LoadHistoryAsync();
        var entry = history.FirstOrDefault(h => h.Id == entryId);

        if (entry != null)
        {
            updateAction(entry);
            await SaveHistoryAsync(history);
        }
    }

    /// <summary>
    /// Marks a connection as trusted.
    /// </summary>
    public async Task MarkAsTrustedAsync(string serverId)
    {
        var history = await LoadHistoryAsync();
        foreach (var entry in history.Where(h => h.ServerId == serverId))
        {
            entry.IsTrusted = true;
        }
        await SaveHistoryAsync(history);
    }

    /// <summary>
    /// Removes trust from a connection.
    /// </summary>
    public async Task RemoveTrustAsync(string serverId)
    {
        var history = await LoadHistoryAsync();
        foreach (var entry in history.Where(h => h.ServerId == serverId))
        {
            entry.IsTrusted = false;
        }
        await SaveHistoryAsync(history);
    }

    /// <summary>
    /// Checks if a server is trusted.
    /// </summary>
    public async Task<bool> IsTrustedAsync(string serverId)
    {
        var history = await LoadHistoryAsync();
        return history.Any(h => h.ServerId == serverId && h.IsTrusted);
    }

    /// <summary>
    /// Gets all trusted connections.
    /// </summary>
    public async Task<List<ConnectionHistoryEntry>> GetTrustedConnectionsAsync()
    {
        var history = await LoadHistoryAsync();
        return history
            .Where(h => h.IsTrusted)
            .GroupBy(h => h.ServerId)
            .Select(g => g.OrderByDescending(h => h.ConnectedAt).First())
            .ToList();
    }

    /// <summary>
    /// Clears old history entries (keeps last N days).
    /// </summary>
    public async Task CleanupOldHistoryAsync(int daysToKeep = 30)
    {
        var history = await LoadHistoryAsync();
        var cutoffDate = DateTime.Now.AddDays(-daysToKeep);

        // Keep trusted connections and recent connections
        history = history
            .Where(h => h.IsTrusted || h.ConnectedAt > cutoffDate)
            .ToList();

        await SaveHistoryAsync(history);
    }

    private async Task SaveHistoryAsync(List<ConnectionHistoryEntry> history)
    {
        var json = JsonSerializer.Serialize(history, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(_historyFilePath, json);
    }

    #endregion
}
