# Target Compatibility & Limitations

A technical reference for supported Unity backends, tested games, architectural edge cases, and why certain games cannot be dumped purely from static disk files.

---

## Tested Games

| Game | Unity Version | Metadata | Status | Technical Details |
| :--- | :--- | :---: | :---: | :--- |
| **Aim Lab** | 2022.3.62 LTS | v31.1 | Supported | 243k methods dumped in ~9s via full LibCpp2IL pipeline |
| **Blue Archive** | 2021.3.56 LTS | v31.0 | Supported | 258k methods dumped in ~10s |
| **Yu-Gi-Oh! Master Duel** | 6000.0.61 (Unity 6) | v31.1 | Supported | 128k methods dumped in ~8s |
| **Goose Goose Duck** | 6000.0.71 (Unity 6) | v31.1 | Supported | Handled Beebyte obfuscated identifiers cleanly |
| **Crab Game** | 2020.3.21 LTS | v27.1 | Supported | Handled non-ASCII unicode homoglyphs |
| **Mobile Legends: Bang Bang (MLBB)** | 2020.3 LTS | v27.1 | Supported | Native Moonton partitioned metadata reconstituted automatically (`metadata.dat` + `stringliteral.dat`) |
| **Fate/Grand Order (FGO)** | 2022.3.62 LTS | v31.1 | Supported | Recovered via Metadata-Only Fallback engine (130 assemblies, 19,308 types, 114,834 methods) |
| **Subway Surfers** | 2022.3 LTS | v29.0 | Supported | Handled via `.xapk` / `.apkm` unpacker and Metadata-Only Fallback (121 assemblies, 63,733 methods) |
| **Among Us** | 2022.3 LTS | v31.0 | Supported | Handled via split APK directory ingestion and Metadata-Only Fallback (171 assemblies, 107,358 methods) |
| **Azur Lane** | 2022.3 LTS | v31.0 | Supported | Recovered via Metadata-Only Fallback engine (127 assemblies, 11,269 types, 77,575 methods) |
| **Honor of Kings (HOK)** | 2021.3 LTS | v29.0 | Supported | Pre-header envelope detected and unwrapped automatically (8-byte checksum prefix removed) |
| **Risk of Rain 2** | Unity Mono | N/A | Not Applicable | Unity Mono build; detected automatically, inspect `Assembly-CSharp.dll` in dnSpy |
| **People Playground** | Unity Mono | N/A | Not Applicable | Unity Mono build; detected automatically, inspect `Assembly-CSharp.dll` in dnSpy |
| **Muck** / **Lethal Company** | Unity Mono | N/A | Not Applicable | Unity Mono build; inspect `Managed/Assembly-CSharp.dll` in dnSpy / ILSpy |
| **Call of Duty: Mobile (CODM)** | 2021.3 | Scrambled | Memory Only | Tencent ACE (`libanort.so` / `libanogs.so`) scrambles static metadata tables on disk |
| **Arknights: Endfield** | 2021.3.34f5 | Scrambled | Memory Only | Tencent ACE (`libanort.so`) scrambles disk metadata tables; requires memory dump |
| **Zenless Zone Zero (ZZZ)** | Custom Unity | Encrypted | Memory Only | Disk metadata encrypted (`MHY\0`); dump decrypted buffer from RAM via Frida |
| **Genshin Impact** / **Honkai: Star Rail** | Custom Unity | Encrypted | Memory Only | Disk metadata encrypted (`MHY\0`); dump decrypted buffer from RAM via Frida |
| **VRChat** | 2022.3 LTS | Encrypted | Memory Only | Disk metadata encrypted (`0x7E7D8417`); dump decrypted buffer from RAM |

---

## Technical Scenarios & Solutions

### 1. Encrypted `global-metadata.dat` (HoYoverse, VRChat)

Standard IL2CPP metadata begins with the 4-byte header `AF 1B B1 FA` (`0xFAB11BAF`). Certain developers encrypt or scramble this file on disk:
- **HoYoverse titles** (Zenless Zone Zero, Genshin Impact, Honkai: Star Rail): Header starts with `4D 48 59 00` (`MHY\0`).
- **VRChat**: Header starts with `17 84 7D 7E` (`0x7E7D8417`).

Because the disk file is encrypted using proprietary block ciphers or asymmetric keys, static file parsers cannot read the type definitions directly. The game decrypts this file into memory when initializing its runtime engine.

> [!TIP]
> **Workaround:** Dump the decrypted metadata buffer from RAM while the game is running using the included Frida scripts in `frida-runtime-dumper/`, then pass the decrypted file:
> ```bash
> Il2CppDumper GameAssembly.dll decrypted_metadata.dat ./dump
> ```

---

### 2. Anti-Cheat Table Scrambling (Tencent ACE)

Games protected by Tencent Anti-Cheat Expert (ACE), such as *Call of Duty: Mobile* and *Arknights: Endfield*, do not replace the magic header with an obvious tag like `MHY\0`. Instead, the `libanort.so` and `libanogs.so` modules alter table offset arrays, pointer tables, and string literal counts on disk (for example, reporting bogus counts of millions of strings) to crash static parsers.

The genuine metadata tables are descrambled and reconstructed in process memory during early native library loading.

> [!TIP]
> **Workaround:** Dump the descrambled `global-metadata.dat` buffer directly from process memory once `libil2cpp.so` has completed initialization.

---

### 3. Envelope / Prefixed Metadata Headers

Certain mobile titles (such as *Honor of Kings*) wrap the standard metadata inside a custom container or prepend an 8- to 32-byte header containing file sizes, bundle checksums, or proprietary wrapper tags before the IL2CPP magic number `0xFAB11BAF`.

**Status:** Handled automatically. The built-in `MetadataNormalizer` scans the first 4 KB of the file for the IL2CPP magic bytes and unwraps the valid metadata payload transparently without requiring manual hex editing.

---

### 4. Moonton Partitioned Metadata

Moonton games (such as *Mobile Legends: Bang Bang*) split the metadata across separate files under `base_assets/assets/bin/Data/Managed/Metadata/`:
- `metadata.dat`: Contains type definitions, methods, fields, and properties.
- `stringliteral.dat`: Contains string literals and string index definitions.

**Status:** Handled automatically. When Il2CppDumper detects `metadata.dat` alongside `stringliteral.dat`, the native Moonton adapter reconstructs a standard unified metadata stream in-memory before proceeding with extraction.

---

### 5. Metadata-Only Fallback Engine

In Unity 2022.3+ 64-bit ARM (`arm64-v8a`) binaries (such as *Fate/Grand Order*, *Azur Lane*, *Subway Surfers*, and *Among Us*), static pointer table scanning in disassembler engines can encounter generic method pointer struct offsets that lie beyond the mapped stream length.

When pointer reading from the native binary encounters misalignment:
1. Full pointer scanning logs a diagnostic warning.
2. The `MetadataOnlyDumper` engine takes over automatically.
3. Complete C# definitions, type signatures, interfaces, properties, field layouts, and stripped assemblies (`dump.cs`, `script.json`, `DummyDll/`) are generated directly from the intact metadata.

---

### 6. Split Android App Bundles (.xapk / .apkm / Directories)

Modern Android app stores distribute titles as split APK bundles:
- `base.apk`: Contains resources and `assets/bin/Data/Managed/Metadata/global-metadata.dat`.
- `config.arm64_v8a.apk` / `split_config.arm64_v8a.apk`: Contains native code (`libil2cpp.so`).

**Status:** Handled automatically. Il2CppDumper accepts `.xapk`, `.apkm`, `.zip`, or a directory containing split `.apk` files directly. It scans all constituent APKs, pairs the highest-priority native library (`arm64-v8a` preferred) with `global-metadata.dat`, and performs extraction.

---

### 7. Unity Mono Games (Not IL2CPP)

Games like *Risk of Rain 2*, *People Playground*, *Muck*, *Lethal Company*, *Valheim*, and *Phasmophobia* use Unity's Mono scripting backend instead of IL2CPP. These games do not contain a `GameAssembly.dll` or `global-metadata.dat`.

**Status:** Detected automatically. Il2CppDumper checks for Mono runtime indicators (`<Game>_Data/Managed/Assembly-CSharp.dll` or `MonoBleedingEdge/`) and informs the user immediately.

> [!NOTE]
> An IL2CPP dumper is not needed for Mono games. Decompiled source code can be inspected directly by opening:
> ```
> <Game>_Data/Managed/Assembly-CSharp.dll
> ```
> in [dnSpy](https://github.com/dnSpy/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy).

---

### 8. Obfuscated Identifiers (Beebyte, Babel)

Games like *Goose Goose Duck* and *Crab Game* run obfuscators prior to IL2CPP compilation. Type, method, and field names may contain invalid identifier characters, control characters, or Unicode homoglyphs.

**Status:** Handled automatically. All symbol names are sanitized so that dumped `.cs` files, disassembler scripts (`ida.py`, `ghidra.py`, `binja.py`), and `DummyDll/*.dll` assemblies compile and load into reverse engineering tools without syntax errors.

---

## Quick Diagnostic: Is My Metadata Encrypted, Wrapped, or Scrambled?

Run this PowerShell command to check the first 16 bytes of your `global-metadata.dat`:

```powershell
$fs = [System.IO.File]::OpenRead("path\to\global-metadata.dat")
$buf = New-Object byte[] 16
$read = $fs.Read($buf, 0, 16)
$fs.Close()
[BitConverter]::ToString($buf, 0, $read)
```

- **Starts with `AF-1B-B1-FA`**: Standard unencrypted metadata. Ready for direct static dumping.
- **Contains `AF-1B-B1-FA` at an offset within 4 KB**: Enveloped or prefixed metadata (e.g. *Honor of Kings*). Automatically detected and unwrapped by `MetadataNormalizer`.
- **Starts with `4D-48-59-00`**: HoYoverse encryption (`MHY\0`). Requires runtime RAM dump.
- **Starts with `17-84-7D-7E`**: VRChat encryption (`0x7E7D8417`). Requires runtime RAM dump.
- **Starts with `AF-1B-B1-FA` but fails with astronomical counts or EOF errors**: Anti-cheat table scrambling (e.g. Tencent ACE in *CODM* / *Endfield*). Requires runtime RAM dump.
- **Custom bytes**: Proprietary encryption or packing. Requires runtime RAM dump.
