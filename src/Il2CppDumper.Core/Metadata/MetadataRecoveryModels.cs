namespace Il2CppDumper.Core.Metadata;

public enum MetadataRecoveryMethod
{
    None,
    Standard,
    TamperedMagic,
    Xor1Byte,
    Xor4Byte,
    PrefixedPayload,
    ByteSwapped
}

public sealed class MetadataRecoveryOptions
{
    public bool IgnoreMagic { get; set; }
    public uint? CustomMagic { get; set; }
    public int ScanDepth { get; set; } = 4096;
}

public sealed class MetadataConfidence
{
    public int Score { get; set; }
    public bool IsRecognizedVersion { get; set; }
    public bool IsPlausibleVersion { get; set; }
    public bool OffsetsValid { get; set; }
    public bool MonotonicOffsets { get; set; }
    public bool StringTableValid { get; set; }
    public string? Diagnostic { get; set; }

    public bool IsAcceptable => Score >= 70;
}

public sealed class MetadataRecoveryResult
{
    public bool Success { get; set; }
    public string ResultPath { get; set; } = string.Empty;
    public MetadataRecoveryMethod Method { get; set; } = MetadataRecoveryMethod.None;
    public int ConfidenceScore { get; set; }
    public long Offset { get; set; }
    public byte[]? XorKey { get; set; }
    public int Version { get; set; }
    public string? Diagnostic { get; set; }
    public bool WasNormalized { get; set; }
}
