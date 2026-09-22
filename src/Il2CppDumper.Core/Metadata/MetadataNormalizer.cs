namespace Il2CppDumper.Core.Metadata;

public static class MetadataNormalizer
{
    public static string Normalize(string metadataPath, string? tempDir = null, Action<string>? logger = null)
    {
        var result = Normalize(metadataPath, new MetadataRecoveryOptions(), tempDir, logger);
        return result.ResultPath;
    }

    public static MetadataRecoveryResult Normalize(
        string metadataPath,
        MetadataRecoveryOptions options,
        string? tempDir = null,
        Action<string>? logger = null)
    {
        return MetadataRecoveryEngine.Recover(metadataPath, options, tempDir, logger);
    }
}
