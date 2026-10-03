using System.Buffers.Binary;

namespace Il2CppDumper.Core.Containers;

public sealed class BinaryIdentity
{
    public BinaryFormat Format { get; set; } = BinaryFormat.Unknown;
    public Architecture Architecture { get; set; } = Architecture.Unknown;
    public bool Is64Bit { get; set; }
    public bool IsFatBinary { get; set; }
    public string? Description { get; set; }
    public List<(Architecture Arch, uint Offset, uint Size)> FatSlices { get; set; } = new();
}

public static class BinaryInspector
{
    private static readonly byte[] ElfMagic = { 0x7F, 0x45, 0x4C, 0x46 }; // \x7FELF
    private static readonly byte[] PeMagic = { 0x4D, 0x5A }; // MZ
    private static readonly byte[] WasmMagic = { 0x00, 0x61, 0x73, 0x6D }; // \0asm

    public static BinaryIdentity Inspect(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            return new BinaryIdentity { Description = "File not found" };
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Inspect(fs);
    }

    public static BinaryIdentity Inspect(Stream stream)
    {
        var buffer = new byte[Math.Min(4096, stream.Length)];
        var originalPos = stream.Position;
        stream.Seek(0, SeekOrigin.Begin);
        var read = stream.Read(buffer, 0, buffer.Length);
        stream.Seek(originalPos, SeekOrigin.Begin);

        return Inspect(buffer.AsSpan(0, read), stream);
    }

    public static BinaryIdentity Inspect(ReadOnlySpan<byte> header, Stream? stream = null)
    {
        var identity = new BinaryIdentity();

        if (header.Length < 4)
        {
            return identity;
        }

        // 1. WASM
        if (header.Length >= 4 && header[..4].SequenceEqual(WasmMagic))
        {
            identity.Format = BinaryFormat.Wasm;
            identity.Architecture = Architecture.Wasm;
            identity.Is64Bit = false;
            identity.Description = "WebAssembly (WASM)";
            return identity;
        }

        // 2. ELF
        if (header.Length >= 4 && header[..4].SequenceEqual(ElfMagic))
        {
            identity.Format = BinaryFormat.Elf;
            if (header.Length >= 20)
            {
                var is64 = header[4] == 2;
                var isLittleEndian = header[5] == 1;
                identity.Is64Bit = is64;

                ushort e_machine = isLittleEndian
                    ? BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(18, 2))
                    : BinaryPrimitives.ReadUInt16BigEndian(header.Slice(18, 2));

                identity.Architecture = e_machine switch
                {
                    0xB7 => Architecture.Arm64,   // 183
                    0x28 => Architecture.Armv7,   // 40
                    0x3E => Architecture.X64,     // 62 (x86_64)
                    0x03 => Architecture.X86,     // 3 (i386)
                    _ => is64 ? Architecture.Arm64 : Architecture.Armv7
                };
                identity.Description = $"ELF {(is64 ? "64-bit" : "32-bit")} ({identity.Architecture})";
            }
            return identity;
        }

        // 3. PE (Portable Executable)
        if (header.Length >= 2 && header[..2].SequenceEqual(PeMagic))
        {
            identity.Format = BinaryFormat.PE;
            if (header.Length >= 0x40)
            {
                var e_lfanew = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(0x3C, 4));
                if (e_lfanew > 0 && e_lfanew + 6 <= header.Length)
                {
                    var peSig = header.Slice(e_lfanew, 4);
                    if (peSig[0] == 0x50 && peSig[1] == 0x45 && peSig[2] == 0x00 && peSig[3] == 0x00) // PE\0\0
                    {
                        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(e_lfanew + 4, 2));
                        identity.Architecture = machine switch
                        {
                            0x8664 => Architecture.X64,
                            0x014C => Architecture.X86,
                            0xAA64 => Architecture.Arm64,
                            0x01C0 or 0x01C4 => Architecture.Armv7,
                            _ => Architecture.Unknown
                        };
                        identity.Is64Bit = machine is 0x8664 or 0xAA64;
                        identity.Description = $"PE Windows Executable ({identity.Architecture})";
                        return identity;
                    }
                }
            }
            identity.Description = "PE Windows Executable";
            return identity;
        }

        // 4. Mach-O
        var magic32 = BinaryPrimitives.ReadUInt32LittleEndian(header[..4]);
        var magicBig = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);

        if (magic32 == 0xFEEDFACE || magic32 == 0xFEEDFACF || magic32 == 0xCAFEBABE ||
            magicBig == 0xFEEDFACE || magicBig == 0xFEEDFACF || magicBig == 0xCAFEBABE)
        {
            identity.Format = BinaryFormat.MachO;

            // Fat / Universal Mach-O
            if (magicBig == 0xCAFEBABE || magic32 == 0xCAFEBABE)
            {
                identity.IsFatBinary = true;
                ParseFatMachO(header, identity, stream);
                identity.Description = $"Mach-O Universal Fat Binary ({string.Join(", ", identity.FatSlices.Select(s => s.Arch))})";
                return identity;
            }

            var is64 = magic32 == 0xFEEDFACF || magicBig == 0xFEEDFACF;
            var isBig = magicBig == 0xFEEDFACE || magicBig == 0xFEEDFACF;
            identity.Is64Bit = is64;

            if (header.Length >= 8)
            {
                var cputype = isBig
                    ? BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4))
                    : BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));

                identity.Architecture = ParseMachOCpuType(cputype);
                identity.Description = $"Mach-O {(is64 ? "64-bit" : "32-bit")} ({identity.Architecture})";
            }
            return identity;
        }

        identity.Description = "Unknown binary format";
        return identity;
    }

    private static Architecture ParseMachOCpuType(uint cputype)
    {
        return cputype switch
        {
            0x0100000C => Architecture.Arm64,   // CPU_TYPE_ARM64
            0x0000000C => Architecture.Armv7,   // CPU_TYPE_ARM
            0x01000007 => Architecture.X64,     // CPU_TYPE_X86_64
            0x00000007 => Architecture.X86,     // CPU_TYPE_X86
            _ => Architecture.Unknown
        };
    }

    private static void ParseFatMachO(ReadOnlySpan<byte> header, BinaryIdentity identity, Stream? stream)
    {
        if (header.Length < 8) return;

        // Fat headers are always Big Endian
        var nfatArch = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(4, 4));
        int offset = 8;

        for (int i = 0; i < nfatArch && offset + 20 <= header.Length; i++)
        {
            var cputype = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset, 4));
            var sliceOffset = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset + 8, 4));
            var sliceSize = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(offset + 12, 4));
            offset += 20;

            var arch = ParseMachOCpuType(cputype);
            identity.FatSlices.Add((arch, sliceOffset, sliceSize));
        }

        // Pick ARM64 as default if present, else X64, else first
        var chosen = identity.FatSlices.FirstOrDefault(s => s.Arch == Architecture.Arm64);
        if (chosen.Arch == Architecture.Unknown && identity.FatSlices.Count > 0)
        {
            chosen = identity.FatSlices.FirstOrDefault(s => s.Arch == Architecture.X64);
            if (chosen.Arch == Architecture.Unknown)
            {
                chosen = identity.FatSlices[0];
            }
        }

        identity.Architecture = chosen.Arch;
        identity.Is64Bit = chosen.Arch is Architecture.Arm64 or Architecture.X64;
    }

    public static byte[]? ExtractSlice(Stream fatStream, Architecture preferredArch)
    {
        var identity = Inspect(fatStream);
        if (!identity.IsFatBinary || identity.FatSlices.Count == 0)
        {
            return null;
        }

        var slice = identity.FatSlices.FirstOrDefault(s => s.Arch == preferredArch);
        if (slice.Arch == Architecture.Unknown)
        {
            slice = identity.FatSlices.FirstOrDefault(s => s.Arch == Architecture.Arm64);
            if (slice.Arch == Architecture.Unknown)
            {
                slice = identity.FatSlices[0];
            }
        }

        fatStream.Seek(slice.Offset, SeekOrigin.Begin);
        var buffer = new byte[slice.Size];
        var totalRead = 0;
        while (totalRead < slice.Size)
        {
            var read = fatStream.Read(buffer, totalRead, (int)slice.Size - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return buffer;
    }
}
