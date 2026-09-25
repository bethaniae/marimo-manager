using System;

namespace MarimoLauncher.Storage;

public sealed class NotebookEntry
{
    public string Path { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastUsedAt { get; set; } = DateTime.MinValue;
}
