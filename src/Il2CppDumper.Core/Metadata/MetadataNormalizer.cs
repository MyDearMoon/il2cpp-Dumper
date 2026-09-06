namespace Il2CppDumper.Core.Metadata;

public static class MetadataNormalizer
{
    private static readonly byte[] MagicBytes = { 0xAF, 0x1B, 0xB1, 0xFA }; // 0xFAB11BAF in little-endian

    public static string Normalize(string metadataPath, string? tempDir = null, Action<string>? logger = null)
    {
        if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
            return metadataPath;

        try
        {
            using var fs = File.OpenRead(metadataPath);
            var searchLen = (int)Math.Min(4096, fs.Length);
            if (searchLen < 4) return metadataPath;

            var buffer = new byte[searchLen];
            var read = fs.Read(buffer, 0, searchLen);

            // Fast path: already at offset 0
            if (buffer[0] == MagicBytes[0] && buffer[1] == MagicBytes[1] &&
                buffer[2] == MagicBytes[2] && buffer[3] == MagicBytes[3])
            {
                return metadataPath;
            }

            // Search for magic in the first searchLen - 4 bytes
            int magicOffset = -1;
            for (int i = 1; i <= read - 4; i++)
            {
                if (buffer[i] == MagicBytes[0] && buffer[i + 1] == MagicBytes[1] &&
                    buffer[i + 2] == MagicBytes[2] && buffer[i + 3] == MagicBytes[3])
                {
                    magicOffset = i;
                    break;
                }
            }

            if (magicOffset <= 0)
            {
                return metadataPath;
            }

            logger?.Invoke($"Found IL2CPP metadata signature (0xFAB11BAF) at offset 0x{magicOffset:X} ({magicOffset} bytes prefix). Unwrapping pre-header envelope...");

            var targetDir = !string.IsNullOrEmpty(tempDir) && Directory.Exists(tempDir)
                ? tempDir
                : Path.GetTempPath();

            var normalizedPath = Path.Combine(targetDir, $"normalized_metadata_{Guid.NewGuid():N}.dat");

            fs.Position = magicOffset;
            using (var outFs = File.Create(normalizedPath))
            {
                fs.CopyTo(outFs);
            }

            logger?.Invoke($"Unwrapped metadata saved to: {normalizedPath} ({new FileInfo(normalizedPath).Length} bytes)");
            return normalizedPath;
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[Warning] Metadata envelope scanning encountered an error: {ex.Message}");
            return metadataPath;
        }
    }
}
