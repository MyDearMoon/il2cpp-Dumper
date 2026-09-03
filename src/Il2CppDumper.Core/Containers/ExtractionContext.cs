namespace Il2CppDumper.Core.Containers;

public sealed class ExtractionContext : IDisposable
{
    public string BinaryPath { get; set; } = string.Empty;
    public string MetadataPath { get; set; } = string.Empty;
    public Architecture Architecture { get; set; } = Architecture.Unknown;
    public BinaryFormat Format { get; set; } = BinaryFormat.Unknown;
    public string OriginalInput { get; set; } = string.Empty;
    public List<DiscoveredBinary> DiscoveredBinaries { get; set; } = new();
    public string? TempDirectory { get; set; }
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (!string.IsNullOrEmpty(TempDirectory) && Directory.Exists(TempDirectory))
        {
            try
            {
                Directory.Delete(TempDirectory, true);
            }
            catch
            {
                // Ignore cleanup errors on exit
            }
        }
    }
}

public sealed class DiscoveredBinary
{
    public string Name { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public Architecture Architecture { get; set; } = Architecture.Unknown;
    public BinaryFormat Format { get; set; } = BinaryFormat.Unknown;
    public long Size { get; set; }
    public string? ArchiveEntryName { get; set; }
    public string? NestedArchiveEntryName { get; set; }

    public override string ToString() => $"{Name} ({Architecture}, {Format}) - {Size / 1024 / 1024:F1} MB";
}
