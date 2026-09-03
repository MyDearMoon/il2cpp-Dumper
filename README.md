# Il2CppDumper

Unity IL2CPP extraction and reverse engineering tool for Windows, Android, and iOS.

Supports:
- Dumping `dump.cs` with field offsets, method RVAs, and signatures.
- Rebuilding typed dummy assemblies (`DummyDll/*.dll`) via Mono.Cecil for dnSpy, ILSpy, and BepInEx modding.
- Generating C++ SDK headers (`il2cpp.h`) with byte-accurate struct layouts.
- Auto-restoration scripts for IDA Pro, Ghidra, and Binary Ninja.
- Container ingestion (.apk, .xapk, .apkm, .ipa, zip, game directory).

---

## Features

- **Container Ingestion**: Drag-and-drop `.apk`, `.xapk`, `.apkm`, `.ipa`, or game folders directly. Automatic detection for `arm64-v8a`, `armeabi-v7a`, `x86_64`, and `x86`.
- **C# Static Header Dump (`dump.cs`)**: Clean definitions with namespaces, classes, methods (with RVAs, file offsets, and VAs), fields (with memory offsets), and properties.
- **Disassembler Scripts**: Ready-to-use symbol restoration scripts for IDA Pro (`ida.py`), Ghidra (`ghidra.py`), and Binary Ninja (`binja.py`).
- **Dummy DLL Assemblies (`DummyDll/*.dll`)**: Valid .NET assemblies emitted via `Mono.Cecil` for inspection in dnSpy, ILSpy, or referencing in BepInEx mods.
- **C++ Modding SDK**: Generates `il2cpp.h` with struct offsets and function typedefs, `il2cpp-init.h` runtime address resolver, and a ready-to-compile MinHook template (`dllmain.cpp`).
- **Runtime Memory Dumping Tools**: Bundled Frida scripts (`frida-dump-android.js`, `frida-dump-pc.js`) for games with on-disk encrypted metadata.

---

## Target Compatibility

| Target Category | Examples | Static Dump | Notes |
| :--- | :--- | :---: | :--- |
| **Standard Unity IL2CPP** | *Aim Lab*, *Blue Archive*, *Yu-Gi-Oh! Master Duel*, *Crab Game* | Supported | Unity 2020 through Unity 6 supported |
| **Obfuscated IL2CPP** | *Goose Goose Duck*, *Gorilla Tag* | Supported | Automatic identifier sanitization |
| **Split Android Bundles** | *Subway Surfers*, *Pokemon UNITE* | Supported | Handled via `.xapk` / `.apkm` unpacker |
| **Unity Mono Games** | *Muck*, *Lethal Company*, *Valheim* | N/A | No dump needed; inspect `Managed/Assembly-CSharp.dll` in dnSpy |
| **Encrypted Metadata** | *Zenless Zone Zero*, *Genshin Impact*, *VRChat* | Memory Dump Only | Metadata encrypted on disk (`MHY\0` or custom cipher) |

For a detailed technical breakdown of why certain games fail static dumps and how to work with them, see [COMPATIBILITY.md](COMPATIBILITY.md).

---

## Installation & Requirements

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or higher.

### Build from Source
```bash
git clone https://github.com/MyDearMoon/il2cpp-Dumper.git
cd il2cpp-Dumper
dotnet build Il2CppDumper.slnx -c Release
```

---

## Usage

### 1. Interactive Mode (Recommended)
Simply launch without arguments:
```bash
dotnet run --project src/Il2CppDumper.Cli
```
You will be prompted to drag-and-drop your target file or folder, select the target architecture, and choose which outputs to generate.

### 2. Command Line (CI/CD & Scripting)
```bash
# Dump everything from an APK
il2cpp-dumper -i game.apk -o ./output --all

# Specify preferred architecture (arm64, arm, x64, x86)
il2cpp-dumper -i game.xapk -a arm64 -o ./output --all

# Dump from PC Unity game folder
il2cpp-dumper -i "C:/Games/MyGame" -o ./dump --all

# Dump directly from extracted binary and metadata
il2cpp-dumper -i libil2cpp.so -m global-metadata.dat -o ./dump --dump-cs --dummy
```

### CLI Options

| Flag | Description |
| :--- | :--- |
| `-i, --input <path>` | Target input (APK, XAPK, APKM, IPA, ZIP, Game Folder, or binary) |
| `-m, --metadata <path>` | Optional explicit path to `global-metadata.dat` |
| `-o, --output <path>` | Output destination folder (defaults to `./dump`) |
| `-a, --arch <name>` | Preferred architecture (`arm64`, `armv7`, `x64`, `x86`) |
| `--all` | Export all formats |
| `--dump-cs` | Export `dump.cs` and `script.json` |
| `--scripts` | Export IDA, Ghidra, and Binary Ninja Python scripts |
| `--dummy` | Export Dummy DLL assemblies (Mono.Cecil) |
| `--cpp` | Export C++ Modding SDK (`il2cpp.h` and hooking scaffolding) |
| `--frida` | Export Frida in-memory dumping scripts |
| `--interactive` | Force interactive wizard mode |
| `-h, --help` | Show command-line help |

---

## Output Structure

When running with `--all`, the output directory contains:
```
dump/
├── dump.cs                   # Human-readable C# pseudo-code overview
├── script.json               # JSON symbol table (RVAs, offsets, signatures)
├── stringliteral.json        # Extracted string literals
├── ida.py                    # IDA Pro symbol restoration script
├── ghidra.py                 # Ghidra symbol restoration script
├── binja.py                  # Binary Ninja symbol restoration script
├── DummyDll/                 # Reference assemblies for dnSpy / BepInEx
│   ├── Assembly-CSharp.dll
│   ├── UnityEngine.dll
│   └── ...
├── cpp-sdk/                  # Native C++ hooking SDK
│   ├── il2cpp.h              # Structs and function pointer typedefs
│   ├── il2cpp-init.h         # Runtime base address resolver
│   └── dllmain.cpp           # Visual Studio DLL injection hook template
└── frida-runtime-dumper/     # In-memory runtime dumpers
    ├── frida-dump-android.js # Android memory dumper script
    ├── frida-dump-pc.js      # Windows PC memory dumper script
    └── run-android.bat       # Quick-launch batch script
```

---

## License

This project is licensed under the [MIT License](LICENSE).
