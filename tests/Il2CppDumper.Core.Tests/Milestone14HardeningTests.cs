using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Il2CppDumper.Cli;
using Il2CppDumper.Core;
using Il2CppDumper.Core.Common;
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Exporters;
using Il2CppDumper.Core.Metadata;
using Il2CppDumper.Core.Model;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class Milestone14HardeningTests
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

        // String table at 288
        var strBytes = Encoding.UTF8.GetBytes("mscorlib.dll\0System\0Object\0Main\0Test\0");
        Array.Copy(strBytes, 0, data, 288, strBytes.Length);

        return data;
    }

    [Fact]
    public void Regression_1_XorValidation_UsesTransformedPayloadBytes()
    {
        var rawData = CreateValidMetadataHeader();
        const byte key = 0x5A;
        var xorData = new byte[rawData.Length];
        for (var i = 0; i < rawData.Length; i++)
        {
            xorData[i] = (byte)(rawData[i] ^ key);
        }

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, xorData);

            var result = MetadataRecoveryEngine.Recover(tempFile);
            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.Xor1Byte, result.Method);
            Assert.NotNull(result.XorKey);
            Assert.Equal(key, result.XorKey[0]);
            Assert.True(result.ConfidenceScore >= 70);

            // Transformed file must contain canonical magic and printable strings
            var decBytes = File.ReadAllBytes(result.ResultPath);
            Assert.Equal(0xFAB11BAF, BinaryPrimitives.ReadUInt32LittleEndian(decBytes.AsSpan(0, 4)));
            var strSample = Encoding.UTF8.GetString(decBytes.AsSpan(288, 12));
            Assert.StartsWith("mscorlib.dll", strSample);

            if (File.Exists(result.ResultPath)) File.Delete(result.ResultPath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Regression_2_ScanDepthZero_ScansCompleteFile()
    {
        var validHeader = CreateValidMetadataHeader();
        const int prefixSize = 10000;
        var totalData = new byte[prefixSize + validHeader.Length];
        Array.Copy(validHeader, 0, totalData, prefixSize, validHeader.Length);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, totalData);

            // With default scan depth (4096), offset 10000 will not be reached
            var resultDefault = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 4096 });
            Assert.False(resultDefault.Success);

            // With scan depth 0 (complete scan), it must discover and recover it
            var resultFull = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 0 });
            Assert.True(resultFull.Success);
            Assert.Equal(MetadataRecoveryMethod.PrefixedPayload, resultFull.Method);
            Assert.Equal(prefixSize, resultFull.Offset);

            if (File.Exists(resultFull.ResultPath)) File.Delete(resultFull.ResultPath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Regression_3_ScanDepth_GreaterThan1MB_NotSilentlyCapped()
    {
        var validHeader = CreateValidMetadataHeader();
        // Place metadata at 1.2 MB (1,200,000 bytes)
        const int prefixSize = 1_200_000;
        var totalData = new byte[prefixSize + validHeader.Length];
        Array.Copy(validHeader, 0, totalData, prefixSize, validHeader.Length);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, totalData);

            // Request 1.5 MB scan depth
            var result = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 1_500_000 });
            Assert.True(result.Success);
            Assert.Equal(MetadataRecoveryMethod.PrefixedPayload, result.Method);
            Assert.Equal(prefixSize, result.Offset);

            if (File.Exists(result.ResultPath)) File.Delete(result.ResultPath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Regression_4_CandidateSelection_EvaluatesMultipleCandidates_PicksHighestConfidence()
    {
        // Candidate 1 at offset 0: tampered magic
        var header1 = CreateValidMetadataHeader(magic: 0x12345678);

        // Candidate 2 at offset 512: canonical magic with higher confidence
        var header2 = CreateValidMetadataHeader(magic: 0xFAB11BAF);

        var combined = new byte[512 + header2.Length];
        Array.Copy(header1, 0, combined, 0, 512);
        Array.Copy(header2, 0, combined, 512, header2.Length);

        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, combined);

            // Scan depth includes offset 512
            var result = MetadataRecoveryEngine.Recover(tempFile, new MetadataRecoveryOptions { ScanDepth = 2048, IgnoreMagic = true });
            Assert.True(result.Success);
            // Candidate 2 (canonical magic at 512) has higher score than tampered magic at 0
            Assert.Equal(512, result.Offset);
            Assert.Equal(MetadataRecoveryMethod.PrefixedPayload, result.Method);

            if (File.Exists(result.ResultPath)) File.Delete(result.ResultPath);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Regression_5_MetadataOnlyMode_NeverFabricatesRvasOrFieldOffsets()
    {
        var meta = new DumpContext
        {
            AnalysisMode = AnalysisMode.MetadataOnly,
            AddressConfidence = AddressConfidence.Unknown,
            Architecture = Architecture.Arm64,
            Format = BinaryFormat.Elf
        };

        var type = new TypeModel
        {
            Name = "SecretService",
            Namespace = "Game"
        };

        var field = new FieldModel
        {
            Name = "apiKey",
            TypeName = "System.String",
            Offset = -1,
            AddressConfidence = AddressConfidence.Unknown
        };
        type.Fields.Add(field);

        var method = new MethodModel
        {
            Name = "Authenticate",
            Token = 0x06000001,
            Rva = 0,
            MethodPointer = 0,
            FileOffset = -1,
            AddressConfidence = AddressConfidence.Unknown
        };
        type.Methods.Add(method);

        var img = new ImageModel { Name = "Game.dll" };
        img.Types.Add(type);
        meta.Images.Add(img);

        // Verify FieldModel.ToString()
        Assert.Contains("Offset: UNKNOWN", field.ToString());

        var outDir = Path.Combine(Path.GetTempPath(), $"metaonly_test_{Guid.NewGuid():N}");
        try
        {
            var exporter = new DumpCsExporter();
            var res = exporter.Export(meta, outDir, ExportOptions.All);
            Assert.True(res.Success);

            var dumpCsContent = File.ReadAllText(Path.Combine(outDir, "dump.cs"));
            Assert.Contains("// RVA: UNKNOWN Offset: UNKNOWN VA: UNKNOWN Token: 0x06000001", dumpCsContent);
            Assert.Contains("// Offset: UNKNOWN", dumpCsContent);
            Assert.DoesNotContain("0x00000000", dumpCsContent);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void Regression_6_MetadataOnlyMode_PreservesActualArchitecture()
    {
        // Create dummy ELF header with e_machine = 0x3E (X64)
        var elfHeader = new byte[64];
        elfHeader[0] = 0x7F; elfHeader[1] = 0x45; elfHeader[2] = 0x4C; elfHeader[3] = 0x46; // \x7FELF
        elfHeader[4] = 2; // 64-bit
        elfHeader[5] = 1; // Little endian
        BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(18, 2), 0x3E); // X64

        var id = BinaryInspector.Inspect(elfHeader);
        Assert.Equal(BinaryFormat.Elf, id.Format);
        Assert.Equal(Architecture.X64, id.Architecture);
    }

    [Fact]
    public void Regression_7_MetadataOnlyMode_PreservesActualBinaryFormat()
    {
        // Create dummy PE header
        var peHeader = new byte[512];
        peHeader[0] = 0x4D; peHeader[1] = 0x5A; // MZ
        BinaryPrimitives.WriteInt32LittleEndian(peHeader.AsSpan(0x3C, 4), 0x80); // e_lfanew
        peHeader[0x80] = 0x50; peHeader[0x81] = 0x45; // PE\0\0
        BinaryPrimitives.WriteUInt16LittleEndian(peHeader.AsSpan(0x84, 2), 0x8664); // AMD64

        var id = BinaryInspector.Inspect(peHeader);
        Assert.Equal(BinaryFormat.PE, id.Format);
        Assert.Equal(Architecture.X64, id.Architecture);
    }

    [Fact]
    public void Regression_8_MultipleMetadataCandidates_AreScoredAndRanked()
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"multi_meta_{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(tempZip, ZipArchiveMode.Create))
            {
                // Dummy binary
                var binEntry = zip.CreateEntry("lib/arm64-v8a/libil2cpp.so");
                using (var s = binEntry.Open())
                {
                    var elfHeader = new byte[64];
                    elfHeader[0] = 0x7F; elfHeader[1] = 0x45; elfHeader[2] = 0x4C; elfHeader[3] = 0x46;
                    elfHeader[4] = 2; elfHeader[5] = 1;
                    BinaryPrimitives.WriteUInt16LittleEndian(elfHeader.AsSpan(18, 2), 0xB7); // ARM64
                    s.Write(elfHeader);
                }

                // Random metadata file at root (low score)
                var randomMeta = zip.CreateEntry("temp_metadata.dat");
                using (var s = randomMeta.Open())
                {
                    s.Write(new byte[100]);
                }

                // Canonical Unity metadata (high score)
                var canonicalMeta = zip.CreateEntry("assets/bin/Data/Managed/Metadata/global-metadata.dat");
                using (var s = canonicalMeta.Open())
                {
                    s.Write(CreateValidMetadataHeader());
                }
            }

            using var ctx = PackageExtractor.Ingest(tempZip);
            Assert.NotNull(ctx.MetadataPath);
            Assert.Contains("global-metadata.dat", ctx.MetadataPath);
        }
        finally
        {
            if (File.Exists(tempZip)) File.Delete(tempZip);
        }
    }

    [Fact]
    public void Regression_9_InvalidCliArguments_FailClearly()
    {
        var code1 = Program.Main(new[] { "--magic", "test", "-i", "nonexistent.apk" });
        Assert.Equal(1, code1);

        var code2 = Program.Main(new[] { "--scan-depth", "banana", "-i", "nonexistent.apk" });
        Assert.Equal(1, code2);

        var code3 = Program.Main(new[] { "--scan-depth", "-5", "-i", "nonexistent.apk" });
        Assert.Equal(1, code3);
    }

    [Fact]
    public void Regression_10_DirectFileTemporaryRecovery_IsCleanedUpViaTempWorkspace()
    {
        string wsDir;
        string tempFile;
        using (var ws = new TempWorkspace())
        {
            wsDir = ws.WorkspaceRoot;
            Assert.True(Directory.Exists(wsDir));

            tempFile = ws.GetTempFilePath(".dat");
            File.WriteAllBytes(tempFile, new byte[] { 1, 2, 3 });
            Assert.True(File.Exists(tempFile));
        }

        // After disposal, both the file and workspace directory must be gone
        Assert.False(File.Exists(tempFile));
        Assert.False(Directory.Exists(wsDir));
    }

    [Fact]
    public void Regression_11_MachO_Detection_Identifies64BitAndFatBinaries()
    {
        // 64-bit Mach-O ARM64
        var macho64 = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(macho64.AsSpan(0, 4), 0xFEEDFACF);
        BinaryPrimitives.WriteUInt32LittleEndian(macho64.AsSpan(4, 4), 0x0100000C); // CPU_TYPE_ARM64

        var id64 = BinaryInspector.Inspect(macho64);
        Assert.Equal(BinaryFormat.MachO, id64.Format);
        Assert.Equal(Architecture.Arm64, id64.Architecture);
        Assert.True(id64.Is64Bit);
        Assert.False(id64.IsFatBinary);

        // Fat Mach-O Binary (Big Endian)
        var fat = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(0, 4), 0xCAFEBABE); // FAT_MAGIC
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(4, 4), 1); // 1 slice
        // Slice 1: ARM64 at offset 28
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(8, 4), 0x0100000C); // cputype ARM64
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(12, 4), 0); // cpusubtype
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(16, 4), 28); // offset
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(20, 4), 32); // size
        BinaryPrimitives.WriteUInt32BigEndian(fat.AsSpan(24, 4), 14); // align

        // Slice payload
        Array.Copy(macho64, 0, fat, 28, 32);

        var idFat = BinaryInspector.Inspect(fat);
        Assert.Equal(BinaryFormat.MachO, idFat.Format);
        Assert.True(idFat.IsFatBinary);
        Assert.Equal(Architecture.Arm64, idFat.Architecture);

        using var ms = new MemoryStream(fat);
        var extractedSlice = BinaryInspector.ExtractSlice(ms, Architecture.Arm64);
        Assert.NotNull(extractedSlice);
        Assert.Equal(32, extractedSlice.Length);
        Assert.Equal(0xFEEDFACF, BinaryPrimitives.ReadUInt32LittleEndian(extractedSlice.AsSpan(0, 4)));
    }

    [Fact]
    public void Regression_12_UnexpectedBinaryParserExceptions_NotSilentlyConvertedToFallback()
    {
        // Missing file should throw FileNotFoundException, not convert to metadata fallback mode
        var res = Il2CppDumperEngine.Execute(
            "C:\\nonexistent_binary_file_12345.so",
            Path.Combine(Path.GetTempPath(), "dump_out"));

        Assert.False(res.Success);
        Assert.NotNull(res.ErrorMessage);
    }
}
