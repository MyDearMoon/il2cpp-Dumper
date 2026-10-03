namespace Il2CppDumper.Core.Metadata;

public interface INormalizedMetadata : IDisposable
{
    Stream OpenReadStream();
    ReadOnlyMemory<byte> Memory { get; }
    long Length { get; }
    MetadataRecoveryMethod Method { get; }
    long Offset { get; }
    int ConfidenceScore { get; }
    string? FilePath { get; }
}

public sealed class MemoryNormalizedMetadata : INormalizedMetadata
{
    private readonly byte[] _data;

    public MemoryNormalizedMetadata(
        byte[] data,
        MetadataRecoveryMethod method = MetadataRecoveryMethod.Standard,
        long offset = 0,
        int confidenceScore = 100,
        string? filePath = null)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Method = method;
        Offset = offset;
        ConfidenceScore = confidenceScore;
        FilePath = filePath;
    }

    public Stream OpenReadStream() => new MemoryStream(_data, writable: false);
    public ReadOnlyMemory<byte> Memory => _data;
    public long Length => _data.Length;
    public MetadataRecoveryMethod Method { get; }
    public long Offset { get; }
    public int ConfidenceScore { get; }
    public string? FilePath { get; }

    public void Dispose()
    {
        // No unmanaged resources for byte array
    }
}

public sealed class FileNormalizedMetadata : INormalizedMetadata
{
    private readonly bool _deleteOnDispose;

    public FileNormalizedMetadata(
        string filePath,
        MetadataRecoveryMethod method = MetadataRecoveryMethod.Standard,
        long offset = 0,
        int confidenceScore = 100,
        bool deleteOnDispose = false)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        Method = method;
        Offset = offset;
        ConfidenceScore = confidenceScore;
        _deleteOnDispose = deleteOnDispose;
        Length = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
    }

    public Stream OpenReadStream()
    {
        return new FileStream(FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    public ReadOnlyMemory<byte> Memory => File.ReadAllBytes(FilePath!);
    public long Length { get; }
    public MetadataRecoveryMethod Method { get; }
    public long Offset { get; }
    public int ConfidenceScore { get; }
    public string? FilePath { get; }

    public void Dispose()
    {
        if (_deleteOnDispose && !string.IsNullOrEmpty(FilePath) && File.Exists(FilePath))
        {
            try
            {
                File.Delete(FilePath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }
}
