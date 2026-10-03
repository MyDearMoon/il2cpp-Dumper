namespace Il2CppDumper.Core.Metadata;

public enum MetadataVersionStatus
{
    Recognized,
    StructurallyPlausible,
    Unsupported,
    Invalid
}

public sealed class MetadataVersionProfile
{
    public int Version { get; init; }
    public int HeaderSize { get; init; }
    public IReadOnlyList<int> RequiredSectionOffsets { get; init; } = Array.Empty<int>();
    public IReadOnlyDictionary<int, int> SectionElementSizes { get; init; } = new Dictionary<int, int>();

    private static readonly Dictionary<int, MetadataVersionProfile> Profiles = new();

    static MetadataVersionProfile()
    {
        // Standard IL2CPP v16-v23
        Register(new MetadataVersionProfile
        {
            Version = 16,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });
        Register(new MetadataVersionProfile
        {
            Version = 19,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });
        Register(new MetadataVersionProfile
        {
            Version = 20,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });
        Register(new MetadataVersionProfile
        {
            Version = 21,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });
        Register(new MetadataVersionProfile
        {
            Version = 22,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });
        Register(new MetadataVersionProfile
        {
            Version = 23,
            HeaderSize = 252,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });

        // IL2CPP v24
        Register(new MetadataVersionProfile
        {
            Version = 24,
            HeaderSize = 256,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });

        // IL2CPP v27
        Register(new MetadataVersionProfile
        {
            Version = 27,
            HeaderSize = 264,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });

        // IL2CPP v29
        Register(new MetadataVersionProfile
        {
            Version = 29,
            HeaderSize = 288,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });

        // IL2CPP v31
        Register(new MetadataVersionProfile
        {
            Version = 31,
            HeaderSize = 288,
            RequiredSectionOffsets = new[] { 8, 16, 24, 48, 88, 96, 160 },
            SectionElementSizes = new Dictionary<int, int> { [96] = 12, [88] = 12 }
        });

        // Moonton / MLBB Partitioned Metadata (v1024)
        Register(new MetadataVersionProfile
        {
            Version = 1024,
            HeaderSize = 264,
            RequiredSectionOffsets = new[] { 12, 20, 28, 52, 92, 100, 164 },
            SectionElementSizes = new Dictionary<int, int> { [100] = 12, [92] = 12 }
        });
    }

    private static void Register(MetadataVersionProfile profile)
    {
        Profiles[profile.Version] = profile;
    }

    public static bool TryGetProfile(int version, out MetadataVersionProfile? profile)
    {
        return Profiles.TryGetValue(version, out profile);
    }

    public static MetadataVersionStatus ClassifyVersion(int version, out string diagnostic)
    {
        if (version <= 0 || (version > 100 && version != 1024))
        {
            diagnostic = $"Impossible IL2CPP metadata version: {version}.";
            return MetadataVersionStatus.Invalid;
        }

        if (Profiles.ContainsKey(version))
        {
            diagnostic = $"Recognized IL2CPP metadata version {version}.";
            return MetadataVersionStatus.Recognized;
        }

        if (version is >= 1 and <= 100)
        {
            diagnostic = $"Valid IL2CPP metadata structure detected. Metadata version {version} is currently unsupported.";
            return MetadataVersionStatus.StructurallyPlausible;
        }

        diagnostic = $"Unsupported metadata version: {version}.";
        return MetadataVersionStatus.Unsupported;
    }
}
