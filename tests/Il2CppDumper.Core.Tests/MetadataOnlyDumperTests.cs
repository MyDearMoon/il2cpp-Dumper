using AssetRipper.Primitives;
using Il2CppDumper.Core.Metadata;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class MetadataOnlyDumperTests
{
    [Fact]
    public void MetadataOnlyDumper_ThrowsFileNotFound_ForMissingMetadata()
    {
        Assert.Throws<FileNotFoundException>(() =>
            MetadataOnlyDumper.Dump("non_existent_metadata.dat", null, new UnityVersion(2022, 3, 0)));
    }
}
