using System.IO.Compression;
using Il2CppDumper.Core.Containers;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class PackageExtractorTests
{
    [Theory]
    [InlineData("lib/arm64-v8a/libil2cpp.so", Architecture.Arm64)]
    [InlineData("lib/armeabi-v7a/libil2cpp.so", Architecture.Armv7)]
    [InlineData("lib/x86_64/libil2cpp.so", Architecture.X64)]
    [InlineData("lib/x86/libil2cpp.so", Architecture.X86)]
    [InlineData("somedir/game.wasm", Architecture.Wasm)]
    public void DetectArchitectureFromPath_IdentifiesCorrectArchitecture(string path, Architecture expected)
    {
        var arch = PackageExtractor.DetectArchitectureFromPath(path);
        Assert.Equal(expected, arch);
    }

    [Theory]
    [InlineData("libil2cpp.so", BinaryFormat.Elf)]
    [InlineData("GameAssembly.dll", BinaryFormat.PE)]
    [InlineData("game.wasm", BinaryFormat.Wasm)]
    public void DetectFormat_IdentifiesCorrectFormat(string file, BinaryFormat expected)
    {
        var fmt = PackageExtractor.DetectFormat(file);
        Assert.Equal(expected, fmt);
    }

    [Fact]
    public void Ingest_ExtractsFromMockApk()
    {
        var tempApk = Path.Combine(Path.GetTempPath(), $"test_package_{Guid.NewGuid():N}.apk");
        try
        {
            using (var zip = ZipFile.Open(tempApk, ZipArchiveMode.Create))
            {
                var binEntry = zip.CreateEntry("lib/arm64-v8a/libil2cpp.so");
                using (var s = binEntry.Open())
                {
                    s.Write(new byte[] { 0x7F, 0x45, 0x4C, 0x46 }); // \x7fELF
                }

                var metaEntry = zip.CreateEntry("assets/bin/Data/Managed/Metadata/global-metadata.dat");
                using (var s = metaEntry.Open())
                {
                    s.Write(new byte[] { 0xAF, 0x1B, 0xB1, 0xFA, 0x1D, 0x00, 0x00, 0x00 }); // Sanity 0xFAB11BAF, v29
                }
            }

            using var ctx = PackageExtractor.Ingest(tempApk, preferredArch: Architecture.Arm64);

            Assert.NotNull(ctx);
            Assert.True(File.Exists(ctx.BinaryPath));
            Assert.True(File.Exists(ctx.MetadataPath));
            Assert.Equal(Architecture.Arm64, ctx.Architecture);
            Assert.Equal(BinaryFormat.Elf, ctx.Format);
        }
        finally
        {
            if (File.Exists(tempApk)) File.Delete(tempApk);
        }
    }

    [Theory]
    [InlineData("libil2cpp.so", true)]
    [InlineData("libil2cpp_cp.so", true)]
    [InlineData("libil2cpp-arm64.so", true)]
    [InlineData("GameAssembly.dll", true)]
    [InlineData("libunity.so", false)]
    [InlineData("someother.dll", false)]
    public void IsBinaryCandidate_MatchesExpectedCandidates(string filename, bool expected)
    {
        Assert.Equal(expected, PackageExtractor.IsBinaryCandidate(filename));
    }

    [Theory]
    [InlineData("global-metadata.dat", true)]
    [InlineData("global-metadata-custom.dat", true)]
    [InlineData("metadata_assets.dat", true)]
    [InlineData("assets.dat", false)]
    [InlineData("metadata.txt", false)]
    public void IsMetadataCandidate_MatchesExpectedCandidates(string filename, bool expected)
    {
        Assert.Equal(expected, PackageExtractor.IsMetadataCandidate(filename));
    }

    [Fact]
    public void Ingest_ExtractsFromSplitApkDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"test_split_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var baseApk = Path.Combine(tempDir, "base.apk");
            var splitApk = Path.Combine(tempDir, "split_config.arm64_v8a.apk");

            // base.apk contains metadata
            using (var zip = ZipFile.Open(baseApk, ZipArchiveMode.Create))
            {
                var metaEntry = zip.CreateEntry("assets/bin/Data/Managed/Metadata/global-metadata.dat");
                using var s = metaEntry.Open();
                s.Write(new byte[] { 0xAF, 0x1B, 0xB1, 0xFA, 0x1D, 0x00, 0x00, 0x00 });
            }

            // split_config contains binary (with custom _cp naming)
            using (var zip = ZipFile.Open(splitApk, ZipArchiveMode.Create))
            {
                var binEntry = zip.CreateEntry("lib/arm64-v8a/libil2cpp_cp.so");
                using var s = binEntry.Open();
                s.Write(new byte[] { 0x7F, 0x45, 0x4C, 0x46 });
            }

            using var ctx = PackageExtractor.Ingest(tempDir, preferredArch: Architecture.Arm64);

            Assert.NotNull(ctx);
            Assert.True(File.Exists(ctx.BinaryPath));
            Assert.True(File.Exists(ctx.MetadataPath));
            Assert.Contains("libil2cpp_cp.so", ctx.BinaryPath);
            Assert.Equal(Architecture.Arm64, ctx.Architecture);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
