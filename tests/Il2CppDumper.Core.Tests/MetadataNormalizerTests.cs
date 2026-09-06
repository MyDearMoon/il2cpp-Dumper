using Il2CppDumper.Core.Metadata;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class MetadataNormalizerTests
{
    private static readonly byte[] StandardMagicAndVersion = { 0xAF, 0x1B, 0xB1, 0xFA, 0x1D, 0x00, 0x00, 0x00 };

    [Fact]
    public void Normalize_StandardMetadata_ReturnsSamePath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[64];
            Array.Copy(StandardMagicAndVersion, data, StandardMagicAndVersion.Length);
            File.WriteAllBytes(tempFile, data);

            var normalized = MetadataNormalizer.Normalize(tempFile);
            Assert.Equal(tempFile, normalized);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Normalize_WithPreHeaderPrefix_UnwrapsAndReturnsNewPath()
    {
        var tempFile = Path.GetTempFileName();
        string? normalizedPath = null;
        try
        {
            // 8-byte prefix (similar to Honor of Kings envelope)
            var prefix = new byte[] { 0x8C, 0x0C, 0x00, 0xCD, 0xC8, 0xA8, 0x67, 0x43 };
            var data = new byte[prefix.Length + StandardMagicAndVersion.Length + 16];
            Array.Copy(prefix, 0, data, 0, prefix.Length);
            Array.Copy(StandardMagicAndVersion, 0, data, prefix.Length, StandardMagicAndVersion.Length);

            File.WriteAllBytes(tempFile, data);

            normalizedPath = MetadataNormalizer.Normalize(tempFile);
            Assert.NotEqual(tempFile, normalizedPath);
            Assert.True(File.Exists(normalizedPath));

            var normalizedBytes = File.ReadAllBytes(normalizedPath);
            Assert.Equal(0xAF, normalizedBytes[0]);
            Assert.Equal(0x1B, normalizedBytes[1]);
            Assert.Equal(0xB1, normalizedBytes[2]);
            Assert.Equal(0xFA, normalizedBytes[3]);
            Assert.Equal(0x1D, normalizedBytes[4]);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normalizedPath) && File.Exists(normalizedPath))
                File.Delete(normalizedPath);
        }
    }

    [Fact]
    public void Normalize_NoMagicFound_ReturnsOriginalPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[64];
            File.WriteAllBytes(tempFile, data);

            var result = MetadataNormalizer.Normalize(tempFile);
            Assert.Equal(tempFile, result);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
