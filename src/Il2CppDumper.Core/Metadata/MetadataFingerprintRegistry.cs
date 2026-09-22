namespace Il2CppDumper.Core.Metadata;

public static class MetadataFingerprintRegistry
{
    public const uint CanonicalMagic = 0xFAB11BAF;
    public const uint BigEndianMagic = 0xAF1BB1FA;
    public const uint HoyoMagic = 0x0059484D; // "MHY\0" in little endian
    public const uint ZeroMagic = 0x00000000;
    public const uint ObfuscatedMagic1 = 0x7E7D8417;

    public static bool TryGetDiagnostic(uint magic, out string? diagnostic)
    {
        if (magic == HoyoMagic)
        {
            diagnostic = "Detected HoYoverse encrypted metadata (starts with 'MHY\\0')!\n" +
                         "HoYoverse games (Zenless Zone Zero, Genshin Impact, Honkai: Star Rail) encrypt global-metadata.dat on disk.\n" +
                         "Static dumpers cannot read disk files directly. Dump the decrypted global-metadata.dat from RAM at runtime.";
            return true;
        }

        if (magic == BigEndianMagic)
        {
            diagnostic = "Detected byte-swapped (big-endian) IL2CPP metadata magic (FA B1 1B AF).";
            return true;
        }

        if (magic == ZeroMagic)
        {
            diagnostic = "Detected zeroed IL2CPP metadata magic (00 00 00 00). Anti-dump protection zeroed the magic word to evade signature scanners.";
            return true;
        }

        if (magic == ObfuscatedMagic1)
        {
            diagnostic = "Detected known obfuscated metadata magic signature (0x7E7D8417).";
            return true;
        }

        diagnostic = null;
        return false;
    }
}
