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
}
