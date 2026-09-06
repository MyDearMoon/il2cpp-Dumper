# Il2CppDumper

A fast, lightweight tool to extract types, methods, fields, and symbols from Unity IL2CPP games.

Supports Windows (PE), Android (ELF: APK, XAPK, APKM, Split Bundles), and iOS (Mach-O: IPA).

---

## Quick Start

1. Download the latest standalone release for your platform from [Releases](https://github.com/MyDearMoon/il2cpp-Dumper/releases).
2. **Drag and drop** your game executable (`Game.exe`), mobile package (`.apk` / `.xapk`), split APK directory, or game folder directly onto `Il2CppDumper.exe`.
3. The tool automatically resolves `GameAssembly.dll` / `libil2cpp.so` and `global-metadata.dat` (even across split APKs, partitioned Moonton assets, or enveloped headers) and dumps everything into a `dump/` folder next to your target.

---

## Target Compatibility

| Category | Examples | Status | Notes |
| :--- | :--- | :---: | :--- |
| **Standard IL2CPP** | *Blue Archive*, *Aim Lab*, *Master Duel*, *Crab Game* | Supported | Unity 2018 through Unity 6 |
| **Partitioned Metadata** | *Mobile Legends: Bang Bang (MLBB)* | Supported | Native Moonton partitioned metadata adapter |
| **Metadata-Only Fallback** | *Fate/Grand Order*, *Subway Surfers*, *Among Us*, *Azur Lane* | Supported | Automatic recovery when binary pointer scanning fails |
| **Envelope / Prefixed Headers** | *Honor of Kings (HOK)* | Supported | Automatic pre-header offset scanning and unwrapping |
| **Split App Bundles** | *Among Us*, *Endfield*, *Subway Surfers* | Supported | Automatic `.xapk` / `.apkm` and split APK directory ingestion |
| **Obfuscated IL2CPP** | *Goose Goose Duck*, *Gorilla Tag* | Supported | Automatic identifier sanitization |
| **Unity Mono** | *Risk of Rain 2*, *Lethal Company*, *Valheim*, *Muck* | N/A | No dump needed; inspect DLLs directly in dnSpy |
| **Encrypted Metadata** | *Zenless Zone Zero*, *Genshin Impact*, *Honkai: Star Rail* | Memory Only | Disk metadata encrypted (`MHY\0`); use included Frida scripts |

For a deeper technical breakdown on encrypted metadata, packaging variants, and anti-cheat constraints, see [COMPATIBILITY.md](COMPATIBILITY.md).

---

## What It Generates

All outputs are saved to the `dump/` folder:

| File / Folder | Purpose |
| :--- | :--- |
| `dump.cs` | Human-readable C# pseudo-code with field memory offsets and method RVAs |
| `script.json` | Symbol table mapping functions, offsets, and strings for tooling |
| `DummyDll/` | Stripped .NET assemblies for browsing in dnSpy, ILSpy, or referencing in BepInEx |
| `ida.py` / `ghidra.py` / `binja.py` | One-click symbol restoration scripts for IDA Pro, Ghidra, and Binary Ninja |
| `cpp-sdk/` | C++ headers (`il2cpp.h`, `il2cpp-init.h`, `dllmain.cpp`) with struct layouts and hook scaffolding |
| `frida-runtime-dumper/` | In-memory dumpers for games with on-disk encrypted metadata |

---

## Command Line Usage

Run directly from the terminal without flags:

```bash
# Dump from a game executable, directory, or mobile package:
Il2CppDumper "C:/Games/MyGame/MyGame.exe"
Il2CppDumper game.apk
Il2CppDumper "C:/Downloads/SplitApkFolder"

# Or pass binary and metadata explicitly:
Il2CppDumper GameAssembly.dll global-metadata.dat
Il2CppDumper libil2cpp.so global-metadata.dat ./custom_output
```

### Options

| Flag | Description |
| :--- | :--- |
| `-i, --input <path>` | Input file, archive, or directory |
| `-m, --metadata <path>` | Path to `global-metadata.dat` (if stored elsewhere) |
| `-o, --output <path>` | Output destination folder (defaults to `./dump`) |
| `-a, --arch <name>` | Target architecture (`arm64`, `armv7`, `x64`, `x86`) |
| `--unity-version <ver>` | Explicit Unity version override (e.g. `2022.3.62f2`) |
| `--all` | Export all components (default) |
| `--dump-cs` / `--dummy` / `--cpp` | Select specific export components |
| `--no-open` | Suppress automatically opening output directory in File Explorer |

---

## Building from Source

Requires [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or higher.

```bash
git clone https://github.com/MyDearMoon/il2cpp-Dumper.git
cd il2cpp-Dumper
dotnet test Il2CppDumper.slnx -c Release
dotnet build Il2CppDumper.slnx -c Release
```

---

## License

[MIT](LICENSE)
