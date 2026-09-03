namespace Il2CppDumper.Core.Containers;

public enum Architecture
{
    Unknown,
    Arm64,
    Armv7,
    X64,
    X86,
    Wasm
}

public enum BinaryFormat
{
    Unknown,
    Elf,
    PE,
    MachO,
    Wasm
}
