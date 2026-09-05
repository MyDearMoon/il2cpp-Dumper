using System.Text;
using Il2CppDumper.Core.Metadata.Moonton;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class MoontonMetadataTests
{
    private static byte[] CreateMockPartitionData(int partitionId, string sampleString)
    {
        var data = new byte[512];
        
        // Magic 0xFAB11BAF
        BitConverter.GetBytes(0xFAB11BAFu).CopyTo(data, 0);
        // Version 1024
        BitConverter.GetBytes(1024).CopyTo(data, 4);
        // PartitionId
        BitConverter.GetBytes(partitionId).CopyTo(data, 8);

        // Sections
        int strOffset = 256;
        var strBytes = Encoding.UTF8.GetBytes(sampleString + "\0");
        Array.Copy(strBytes, 0, data, strOffset, strBytes.Length);

        // string section at offset 28 in header: offset=256, size=strBytes.Length
        BitConverter.GetBytes(strOffset).CopyTo(data, 28);
        BitConverter.GetBytes(strBytes.Length).CopyTo(data, 32);

        return data;
    }

    [Fact]
    public void IsMoontonMetadata_ReturnsTrueForValidHeader()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = CreateMockPartitionData(3, "TestString");
            File.WriteAllBytes(tempFile, data);

            Assert.True(MoontonDumper.IsMoontonMetadata(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void IsMoontonMetadata_ReturnsFalseForStandardHeader()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var data = new byte[64];
            BitConverter.GetBytes(0xFAB11BAFu).CopyTo(data, 0);
            BitConverter.GetBytes(29).CopyTo(data, 4); // Standard v29
            File.WriteAllBytes(tempFile, data);

            Assert.False(MoontonDumper.IsMoontonMetadata(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveString_ResolvesAcrossMultiplePartitions()
    {
        var ctx = new MoontonMetadataContext();

        var p1Data = CreateMockPartitionData(1, "EngineMethod");
        var p3Data = CreateMockPartitionData(3, "GamePlayerController");

        var p1 = new MoontonPartition
        {
            PartitionId = 1,
            Data = p1Data,
            StringOffset = 256
        };

        var p3 = new MoontonPartition
        {
            PartitionId = 3,
            Data = p3Data,
            StringOffset = 256
        };

        ctx.AddPartition(p1);
        ctx.AddPartition(p3);

        // Token 0x01000000: Partition 1, offset 0 -> "EngineMethod"
        var str1 = ctx.ResolveString(0x01000000, defaultPartition: 3);
        Assert.Equal("EngineMethod", str1);

        // Token 0x03000000: Partition 3, offset 0 -> "GamePlayerController"
        var str3 = ctx.ResolveString(0x03000000, defaultPartition: 3);
        Assert.Equal("GamePlayerController", str3);

        // Default partition fallback
        var strDefault = ctx.ResolveString(0x00000000, defaultPartition: 1);
        Assert.Empty(strDefault);
    }
}
