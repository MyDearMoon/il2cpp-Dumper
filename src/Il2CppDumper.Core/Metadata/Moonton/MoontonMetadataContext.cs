using System.Text;

namespace Il2CppDumper.Core.Metadata.Moonton;

public sealed class MoontonMetadataContext
{
    private readonly Dictionary<int, MoontonPartition> _partitions = new();

    public IReadOnlyDictionary<int, MoontonPartition> Partitions => _partitions;

    public void AddPartition(MoontonPartition partition)
    {
        _partitions[partition.PartitionId] = partition;
    }

    public MoontonPartition? GetPartition(int partitionId)
    {
        _partitions.TryGetValue(partitionId, out var part);
        return part;
    }

    public string ResolveString(int token, int defaultPartition = 3)
    {
        if (token <= 0) return string.Empty;

        var partId = (token >> 24) & 0xFF;
        var offset = token & 0x00FFFFFF;

        if (partId == 0)
        {
            partId = defaultPartition;
        }

        if (_partitions.TryGetValue(partId, out var partition))
        {
            return partition.ReadString(offset);
        }

        // Fallback: search any partition if not found in requested
        foreach (var p in _partitions.Values)
        {
            var str = p.ReadString(offset);
            if (!string.IsNullOrEmpty(str))
            {
                return str;
            }
        }

        return string.Empty;
    }

    public List<string> GetAllStringLiterals()
    {
        var result = new List<string>();
        foreach (var p in _partitions.Values.OrderBy(x => x.PartitionId))
        {
            if (p.StringLiteralOffset <= 0 || p.StringLiteralCount <= 0) continue;

            // In Unity IL2CPP, stringLiteral is an array of Il2CppStringLiteral:
            // uint32_t length; int32_t dataIndex; (8 bytes)
            var count = p.StringLiteralCount / 8;
            for (int i = 0; i < count; i++)
            {
                var entryOffset = p.StringLiteralOffset + i * 8;
                if (entryOffset + 8 > p.Data.Length) break;

                var len = BitConverter.ToInt32(p.Data, entryOffset);
                var dataIndex = BitConverter.ToInt32(p.Data, entryOffset + 4);

                if (len <= 0 || len > 10000) continue;
                var dataOffset = p.StringLiteralDataOffset + dataIndex;
                if (dataOffset < 0 || dataOffset + len > p.Data.Length) continue;

                var str = Encoding.UTF8.GetString(p.Data, dataOffset, len);
                if (!string.IsNullOrEmpty(str))
                {
                    result.Add(str);
                }
            }
        }
        return result;
    }

    public static MoontonMetadataContext LoadFromDirectoryOrFile(string targetPath, Action<string>? logger = null)
    {
        var ctx = new MoontonMetadataContext();

        var dir = File.Exists(targetPath) ? Path.GetDirectoryName(targetPath) ?? "." : targetPath;

        // Known Moonton partition filenames
        var knownFiles = new[]
        {
            "global-metadata.dat",
            "global-first-metadata.dat",
            "global-csharp-metadata.dat"
        };

        foreach (var fileName in knownFiles)
        {
            var fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
            {
                var part = MoontonPartition.FromFile(fullPath);
                if (part != null)
                {
                    ctx.AddPartition(part);
                    logger?.Invoke($"Loaded Moonton partition {part.PartitionId}: {Path.GetFileName(fullPath)} ({part.TypeCount} types, {part.MethodCount} methods)");
                }
            }
        }

        // If targetPath is a direct file that wasn't in knownFiles
        if (File.Exists(targetPath) && ctx.Partitions.Count == 0)
        {
            var directPart = MoontonPartition.FromFile(targetPath);
            if (directPart != null)
            {
                ctx.AddPartition(directPart);
                logger?.Invoke($"Loaded direct Moonton partition {directPart.PartitionId}: {Path.GetFileName(targetPath)}");
            }
        }

        return ctx;
    }
}
