using System.Text;

namespace Il2CppDumper.Core.Metadata.Moonton;

public sealed class MoontonPartition
{
    public int PartitionId { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public byte[] Data { get; set; } = Array.Empty<byte>();

    public int StringOffset { get; set; }
    public int StringCount { get; set; }

    public int TypeDefsOffset { get; set; }
    public int TypeDefsSize { get; set; }

    public int MethodsOffset { get; set; }
    public int MethodsSize { get; set; }

    public int FieldsOffset { get; set; }
    public int FieldsSize { get; set; }

    public int ParametersOffset { get; set; }
    public int ParametersSize { get; set; }

    public int StringLiteralOffset { get; set; }
    public int StringLiteralCount { get; set; }
    public int StringLiteralDataOffset { get; set; }
    public int StringLiteralDataCount { get; set; }

    public int TypeCount => TypeDefsSize / 92;
    public int MethodCount => MethodsSize / 32;
    public int FieldCount => FieldsSize / 12;
    public int ParameterCount => ParametersSize / 12;

    public static MoontonPartition? FromFile(string path)
    {
        if (!File.Exists(path)) return null;

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 252) return null;

        var magic = BitConverter.ToUInt32(bytes, 0);
        if (magic != 0xFAB11BAF) return null;

        var ver = BitConverter.ToInt32(bytes, 4);
        if (ver != 1024) return null;

        var partId = BitConverter.ToInt32(bytes, 8);

        return new MoontonPartition
        {
            PartitionId = partId,
            FilePath = path,
            Data = bytes,
            StringLiteralOffset = BitConverter.ToInt32(bytes, 12),
            StringLiteralCount = BitConverter.ToInt32(bytes, 16),
            StringLiteralDataOffset = BitConverter.ToInt32(bytes, 20),
            StringLiteralDataCount = BitConverter.ToInt32(bytes, 24),
            StringOffset = BitConverter.ToInt32(bytes, 28),
            StringCount = BitConverter.ToInt32(bytes, 32),
            MethodsOffset = BitConverter.ToInt32(bytes, 52),
            MethodsSize = BitConverter.ToInt32(bytes, 56),
            ParametersOffset = BitConverter.ToInt32(bytes, 92),
            ParametersSize = BitConverter.ToInt32(bytes, 96),
            FieldsOffset = BitConverter.ToInt32(bytes, 100),
            FieldsSize = BitConverter.ToInt32(bytes, 104),
            TypeDefsOffset = BitConverter.ToInt32(bytes, 164),
            TypeDefsSize = BitConverter.ToInt32(bytes, 168)
        };
    }

    public string ReadString(int offset)
    {
        if (offset < 0 || StringOffset + offset >= Data.Length) return string.Empty;
        var start = StringOffset + offset;
        var end = Array.IndexOf(Data, (byte)0, start);
        if (end < 0) end = Data.Length;
        return Encoding.UTF8.GetString(Data, start, end - start);
    }
}
