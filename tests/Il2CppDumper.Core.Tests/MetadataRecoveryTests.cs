using System.Buffers.Binary;
using System.Text;
using Il2CppDumper.Core.Metadata;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class MetadataRecoveryTests
{
    private static byte[] CreateValidMetadataHeader(uint magic = 0xFAB11BAF, int version = 29, int totalSize = 1024)
    {
        var data = new byte[totalSize];

        // Magic (0..4) and Version (4..8)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), version);

        // stringLiteral (8, 12)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), 256);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 16);

        // stringLiteralData (16, 20)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 272);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), 16);

        // string (24, 28)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), 288);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), 128);

        // methods (48, 52)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(48, 4), 416);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(52, 4), 64);

        // parameters (88, 92)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88, 4), 480);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(92, 4), 32);

        // fields (96, 100)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(96, 4), 512);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(100, 4), 32);

        // typeDefs (160, 164)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(160, 4), 544);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(164, 4), 92);

        // Populate string table at 288 with printable ASCII strings
        var strBytes = Encoding.UTF8.GetBytes("mscorlib.dll\0System\0Object\0Main\0Test\0");
        Array.Copy(strBytes, 0, data, 288, strBytes.Length);

        return data;
    }

    [Fact]
    public void Recover_StandardCanonicalMetadata_FastPathReturnsOriginalPath()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader());

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.Standard, result.Method);
            Assert.Equal(tempFile, result.ResultPath);
            Assert.False(result.WasNormalized);
            Assert.True(result.ConfidenceScore >= 70);
            Assert.Equal(29, result.Version);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_TamperedMagic_RestoresCanonicalMagicAndSucceeds()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            // Tampered magic: 0x12345678
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader(magic: 0x12345678));

            var result = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { IgnoreMagic = true });
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.TamperedMagic, result.Method);
            Assert.NotEqual(tempFile, result.ResultPath);
            Assert.True(result.WasNormalized);
            Assert.True(result.ConfidenceScore >= 70);

            // Verify canonical magic is restored in normalized file
            var restoredBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(restoredBytes.AsSpan(0, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_ZeroedMagic_IdentifiesAntiDumpAndRecovers()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            // Zeroed magic
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader(magic: 0x00000000));

            var result = MetadataRecoveryEngine.Recover(tempFile);
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.TamperedMagic, result.Method);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("zeroed", result.Diagnostic, StringComparison.OrdinalIgnoreCase);

            var restoredBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(restoredBytes.AsSpan(0, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_CustomMagicFlag_MatchesSpecifiedMagic()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader(magic: 0x7E7D8417));

            var result = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { CustomMagic = 0x7E7D8417 });
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.TamperedMagic, result.Method);
            Assert.True(result.ConfidenceScore >= 70);

            var restoredBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(restoredBytes.AsSpan(0, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_Xor1Byte_AutoDetectsKeyAndDecrypts()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            var data = CreateValidMetadataHeader();
            const byte xorKey = 0x5A;
            for (var i = 0; i < data.Length; i++) data[i] ^= xorKey;

            File.WriteAllBytes(tempFile, data);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.Xor1Byte, result.Method);
            Assert.NotNull(result.XorKey);
            Assert.Equal(xorKey, result.XorKey[0]);
            Assert.Equal(29, result.Version);

            var restoredBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(restoredBytes.AsSpan(0, 4)));
            Assert.Equal(29, BinaryPrimitives.ReadInt32LittleEndian(restoredBytes.AsSpan(4, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_Xor4Byte_AutoDetectsKeyAndDecrypts()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            var data = CreateValidMetadataHeader();
            const uint xorKey = 0xDEADBEEF;
            var keyBytes = BitConverter.GetBytes(xorKey);

            for (var i = 0; i < data.Length; i++) data[i] ^= keyBytes[i % 4];

            File.WriteAllBytes(tempFile, data);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.Xor4Byte, result.Method);
            Assert.NotNull(result.XorKey);
            Assert.Equal(xorKey, BinaryPrimitives.ReadUInt32LittleEndian(result.XorKey));
            Assert.Equal(29, result.Version);

            var restoredBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(restoredBytes.AsSpan(0, 4)));
            Assert.Equal(29, BinaryPrimitives.ReadInt32LittleEndian(restoredBytes.AsSpan(4, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_InvalidXor_FailsGracefullyWithDiagnostic()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var randomData = new byte[1024];
            new Random(42).NextBytes(randomData);
            File.WriteAllBytes(tempFile, randomData);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
            Assert.Equal(MetadataRecoveryMethod.None, result.Method);
            Assert.NotNull(result.Diagnostic);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_InvalidVersion_RejectsMalformedHeader()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Impossible version: -5
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader(version: -5));

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_OutOfRangeOffsets_RejectsHeader()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = CreateValidMetadataHeader();
            // Corrupt stringOffset to 999999 (exceeds file size 1024)
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), 999999);
            File.WriteAllBytes(tempFile, data);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_IntegerOverflowOffsets_SafelyRejectsWithoutCrash()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = CreateValidMetadataHeader();
            // Set offset and size to large values that would overflow a 32-bit signed integer
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24, 4), 0x7FFFFFF0);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28, 4), 0x7FFFFFF0);
            File.WriteAllBytes(tempFile, data);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_TruncatedFile_RejectsSafely()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, new byte[100]); // Less than 252 bytes

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
            Assert.Contains("truncated", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_PrefixedEnvelope_UnwrapsWithDeepScan()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            var prefix = new byte[128];
            new Random(123).NextBytes(prefix);
            var validData = CreateValidMetadataHeader();

            var combined = new byte[prefix.Length + validData.Length];
            Array.Copy(prefix, 0, combined, 0, prefix.Length);
            Array.Copy(validData, 0, combined, prefix.Length, validData.Length);

            File.WriteAllBytes(tempFile, combined);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            normPath = result.ResultPath;

            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.PrefixedPayload, result.Method);
            Assert.Equal(128, result.Offset);
            Assert.True(result.WasNormalized);

            var unwrapped = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(unwrapped.AsSpan(0, 4)));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_DeepScanConfigurableDepth_HonorsLimits()
    {
        var tempFile = Path.GetTempFileName();
        string? normPath = null;
        try
        {
            // Prefix of 8192 bytes
            var prefix = new byte[8192];
            var validData = CreateValidMetadataHeader();

            var combined = new byte[prefix.Length + validData.Length];
            Array.Copy(validData, 0, combined, prefix.Length, validData.Length);
            File.WriteAllBytes(tempFile, combined);

            // Default scan depth is 4096, so 8192 should NOT be found
            var failResult = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 4096 });
            Assert.False(failResult.Success);

            // With ScanDepth = 16384, it should be found and recovered
            var successResult = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 16384 });
            normPath = successResult.ResultPath;

            Assert.True(successResult.Success);
            Assert.Equal(MetadataRecoveryMethod.PrefixedPayload, successResult.Method);
            Assert.Equal(8192, successResult.Offset);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
            if (!string.IsNullOrEmpty(normPath) && File.Exists(normPath)) File.Delete(normPath);
        }
    }

    [Fact]
    public void Recover_ByteSwappedMagic_DetectsEndiannessIssue()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Big-endian magic 0xAF1BB1FA (FA B1 1B AF)
            File.WriteAllBytes(tempFile, CreateValidMetadataHeader(magic: MetadataFingerprintRegistry.BigEndianMagic));

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.False(result.Success);
            Assert.Equal(MetadataRecoveryMethod.ByteSwapped, result.Method);
            Assert.NotNull(result.Diagnostic);
            Assert.Contains("byte-swapped", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Recover_MalformedFuzzInputs_NeverThrowsUnhandledException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var rng = new Random(999);
            for (var iter = 0; iter < 20; iter++)
            {
                var size = rng.Next(10, 2048);
                var fuzzData = new byte[size];
                rng.NextBytes(fuzzData);

                File.WriteAllBytes(tempFile, fuzzData);

                // Must never throw
                var result = MetadataRecoveryEngine.Recover(tempFile);
                Assert.NotNull(result);
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
