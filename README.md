# Il2CppDumper

A fast, lightweight tool to extract types, methods, fields, and symbols from Unity IL2CPP games.

Supports Windows, Android (APK, XAPK, APKM), and iOS (IPA).

---

## Quick Start

1. Download the latest standalone release for your platform from [Releases](https://github.com/MyDearMoon/il2cpp-Dumper/releases).
2. **Drag and drop** your game executable (`Game.exe`), mobile package (`.apk` / `.xapk`), or game folder directly onto `Il2CppDumper.exe`.
3. The tool automatically resolves `GameAssembly.dll` and `global-metadata.dat` and dumps everything into a `dump/` folder next to your target.

---

## Target Compatibility

| Category | Examples | Status | Notes |
| :--- | :--- | :---: | :--- |
| **Standard IL2CPP** | *Blue Archive*, *Aim Lab*, *Master Duel*, *Crab Game* | Supported | Unity 2018 through Unity 6 |
| **Obfuscated IL2CPP** | *Goose Goose Duck*, *Gorilla Tag* | Supported | Automatic identifier sanitization |
| **Split Bundles** | *Subway Surfers*, *Pokemon UNITE* | Supported | Automatic `.xapk` / `.apkm` extraction |
| **Unity Mono** | *Lethal Company*, *Valheim*, *Muck* | N/A | No dump needed; inspect DLL directly in dnSpy |
| **Encrypted Metadata** | *Zenless Zone Zero*, *Genshin Impact*, *VRChat* | Memory Only | Use included Frida runtime dumper |

For a deeper technical breakdown on encrypted metadata and anti-cheat constraints, see [COMPATIBILITY.md](COMPATIBILITY.md).

---

## What It Generates

All outputs are saved to the `dump/` folder:

| File / Folder | Purpose |
| :--- | :--- |
| `dump.cs` | Human-readable C# pseudo-code with field memory offsets and method RVAs |
| `script.json` | Symbol table mapping functions, offsets, and strings for tooling |
| `DummyDll/` | Stripped .NET assemblies for browsing in dnSpy, ILSpy, or referencing in BepInEx |
| `ida.py` / `ghidra.py` / `binja.py` | One-click symbol restoration scripts for IDA Pro, Ghidra, and Binary Ninja |
| `cpp-sdk/` | C++ headers (`il2cpp.h`, `il2cpp-init.h`) with struct layouts and hook scaffolding |
| `frida-runtime-dumper/` | In-memory dumpers for games with on-disk encrypted metadata |

---

## Command Line Usage

Run directly from the terminal without flags:

```bash
# Dump from a game executable, directory, or mobile package:
Il2CppDumper "C:/Games/MyGame/MyGame.exe"
Il2CppDumper game.apk

# Or pass binary and metadata explicitly:
Il2CppDumper GameAssembly.dll global-metadata.dat
Il2CppDumper GameAssembly.dll global-metadata.dat ./custom_output
```

### Options

| Flag | Description |
| :--- | :--- |
| `-i, --input <path>` | Input file or folder |
| `-m, --metadata <path>` | Path to `global-metadata.dat` (if stored elsewhere) |
| `-o, --output <path>` | Output destination folder (defaults to `./dump`) |
| `-a, --arch <name>` | Target architecture (`arm64`, `armv7`, `x64`, `x86`) |
| `--all` | Export all components (default) |
| `--dump-cs` / `--dummy` / `--cpp` | Select specific export components |

---

## Building from Source

Requires [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or higher.

```bash
git clone https://github.com/MyDearMoon/il2cpp-Dumper.git
cd il2cpp-Dumper
dotnet build Il2CppDumper.slnx -c Release
```

---

## License

[MIT](LICENSE)
