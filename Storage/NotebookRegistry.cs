using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MarimoLauncher.Storage;

/// <summary>
/// Remembers which notebooks the user has opened. Persisted as JSON under
/// the user's app-data folder; the app never moves or modifies the
/// notebook files themselves.
/// </summary>
public sealed class NotebookRegistry
{
    private readonly List<NotebookEntry> _entries = new();
    private readonly object _lock = new();

    public NotebookRegistry(string? storePath = null)
    {
        StorePath = storePath ?? DefaultStorePath();
        Load();
    }

    public string StorePath { get; }

    public static string DefaultStorePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MarimoLauncher");
        return Path.Combine(dir, "notebooks.json");
    }

    public static bool StoreExists() => File.Exists(DefaultStorePath());

    public IReadOnlyList<NotebookEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries
                    .OrderByDescending(e => e.LastUsedAt)
                    .ThenByDescending(e => e.AddedAt)
                    .ToList();
            }
        }
    }

    public NotebookEntry? FindByPath(string path)
    {
        var key = Normalize(path);
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => Normalize(e.Path) == key);
        }
    }

    /// <summary>Returns the existing entry for the path, or creates one.</summary>
    public NotebookEntry AddOrUpdate(string path, string? displayName = null)
    {
        var normalized = Normalize(path);

        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => Normalize(e.Path) == normalized);
            if (existing is not null)
            {
                if (!string.IsNullOrWhiteSpace(displayName))
                {
                    existing.DisplayName = displayName!;
                }

                SaveNoLock();
                return existing;
            }
        }

        var entry = new NotebookEntry
        {
            Path = path,
            DisplayName = displayName ?? Path.GetFileNameWithoutExtension(path),
            AddedAt = DateTime.UtcNow,
            LastUsedAt = DateTime.UtcNow,
        };

        lock (_lock)
        {
            _entries.Add(entry);
            SaveNoLock();
        }

        return entry;
    }

    public void Touch(NotebookEntry entry)
    {
        entry.LastUsedAt = DateTime.UtcNow;
        Save();
    }

    public void Rename(NotebookEntry entry, string displayName)
    {
        entry.DisplayName = displayName;
        Save();
    }

    public void Remove(NotebookEntry entry)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            SaveNoLock();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            SaveNoLock();
        }
    }

    private void SaveNoLock()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_entries, options));
        }
        catch (IOException)
        {
            // Persistence is best-effort; the app keeps running.
        }
    }

    private void Load()
    {
        if (!File.Exists(StorePath))
        {
            return;
        }

        try
        {
            var items = JsonSerializer.Deserialize<List<NotebookEntry>>(File.ReadAllText(StorePath)) ?? new();
            lock (_lock)
            {
                _entries.Clear();
                _entries.AddRange(items);
            }
        }
        catch (Exception)
        {
            // Corrupt store: start fresh rather than crash the daemon.
            lock (_lock)
            {
                _entries.Clear();
            }
        }
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();
}
