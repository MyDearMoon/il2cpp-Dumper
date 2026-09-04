# Target Compatibility & Limitations

A technical reference for supported Unity backends, tested games, and why certain games cannot be dumped purely from disk files.

---

## Tested Games

| Game | Unity Version | Metadata | Status | Notes |
| :--- | :--- | :---: | :---: | :--- |
| **Aim Lab** | 2022.3.62 LTS | v31.1 | Supported | 243k methods dumped in ~9s |
| **Blue Archive** | 2021.3.56 LTS | v31.0 | Supported | 258k methods dumped in ~10s |
| **Yu-Gi-Oh! Master Duel** | 6000.0.61 (Unity 6) | v31.1 | Supported | 128k methods dumped in ~8s |
| **Goose Goose Duck** | 6000.0.71 (Unity 6) | v31.1 | Supported | Handled Beebyte obfuscated identifiers |
| **Crab Game** | 2020.3.21 LTS | v27.1 | Supported | Handled non-ASCII unicode homoglyphs |
| **Subway Surfers** | 2022.3 LTS | v29.0 | Supported | Handled via `.xapk` / `.apkm` unpacker |
| **Muck** / **Lethal Company** | Unity Mono | N/A | Not Applicable | Mono build; inspect `Assembly-CSharp.dll` in dnSpy |
| **Zenless Zone Zero** | Custom Unity | Encrypted | Memory Only | Disk metadata encrypted (`MHY\0`) |
| **VRChat** | 2022.3 LTS | Encrypted | Memory Only | Disk metadata encrypted (`0x7E7D8417`) |

---

## Technical Scenarios & Workarounds

### 1. Encrypted `global-metadata.dat`

Standard IL2CPP metadata begins with the 4-byte header `AF 1B B1 FA` (`0xFAB11BAF`). Certain developers encrypt or scramble this file on disk:
- **HoYoverse titles** (ZZZ, Genshin Impact, Honkai: Star Rail): Header starts with `4D 48 59 00` (`MHY\0`).
- **VRChat**: Header starts with `17 84 7D 7E` (`0x7E7D8417`).

Because the disk file is encrypted, static file parsers cannot read the type definitions directly. The game decrypts this file in memory when launching.

> [!TIP]
> **Workaround:** Dump the decrypted metadata buffer from RAM while the game is running using the included Frida scripts in `frida-runtime-dumper/`, then pass the decrypted file:
> ```bash
> Il2CppDumper GameAssembly.dll decrypted_metadata.dat ./dump
> ```

---

### 2. Unity Mono Games (Not IL2CPP)

Games like *Muck*, *Lethal Company*, *Valheim*, and *Phasmophobia* use Unity's Mono scripting backend instead of IL2CPP. These games do not contain a `GameAssembly.dll` or `global-metadata.dat`.

> [!NOTE]
> **Workaround:** An IL2CPP dumper is not needed. Original C# assemblies already exist in:
> ```
> <Game>_Data/Managed/Assembly-CSharp.dll
> ```
> Open that file directly in [dnSpy](https://github.com/dnSpy/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy) to view full decompiled source code.

---

### 3. Code Obfuscation (Beebyte, Babel)

Games like *Goose Goose Duck* and *Crab Game* run obfuscators before compiling to IL2CPP. Names may contain non-ASCII characters, invalid symbols, or empty identifiers.

**Status:** Handled automatically. Il2CppDumper sanitizes all illegal identifiers so that dumped `.cs` files, disassembler scripts, and `DummyDll/*.dll` assemblies compile and load cleanly.

---

### 4. Split Android Packages (.xapk / .apkm)

Modern Android stores distribute games split across multiple APKs (for example, `libil2cpp.so` in `config.arm64_v8a.apk` and `global-metadata.dat` in `base.apk`).

**Status:** Handled automatically. Drop the `.xapk`, `.apkm`, or `.zip` file directly onto the executable; it automatically unpacks and pairs the binary with the metadata.

---

## Quick Diagnostic: Is My Metadata Encrypted?

Run this PowerShell command to check the header bytes of your `global-metadata.dat`:

```powershell
$fs = [System.IO.File]::OpenRead("path\to\global-metadata.dat")
$buf = New-Object byte[] 4
$fs.Read($buf, 0, 4) | Out-Null
$fs.Close()
[BitConverter]::ToString($buf)
```

- **`AF-1B-B1-FA`**: Standard unencrypted metadata. Ready for static dump.
- **`4D-48-59-00`**: HoYoverse encrypted (`MHY\0`). Requires memory dump.
- **Anything else**: Custom packed or encrypted. Requires memory dump.
