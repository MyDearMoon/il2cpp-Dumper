# Target Compatibility & Limitations

This document covers tested Unity targets, supported formats, and technical reasons why certain games cannot be dumped purely from disk files.

## Tested Games

| Game | Unity Version | Metadata | Status | Notes |
| :--- | :--- | :--- | :--- | :--- |
| **Aim Lab** | 2022.3.62 LTS | v31.1 | Supported | 243k methods dumped in ~9s |
| **Goose Goose Duck** | 6000.0.71 (Unity 6) | v31.1 | Supported | Handled Beebyte obfuscated identifiers |
| **Yu-Gi-Oh! Master Duel** | 6000.0.61 (Unity 6) | v31.1 | Supported | 128k methods dumped in ~8s |
| **Blue Archive** | 2021.3.56 LTS | v31.0 | Supported | 258k methods dumped in ~10s |
| **Crab Game** | 2020.3.21 LTS | v27.1 | Supported | Handled non-ASCII unicode homoglyphs |
| **Muck** | Mono | N/A | Not Applicable | Mono build; inspect `Managed/Assembly-CSharp.dll` in dnSpy |
| **Zenless Zone Zero** | Custom Unity | MHY\0 | Memory Dump Only | Metadata encrypted on disk |
| **VRChat** | 2022.3 LTS | 0x7E7D8417 | Memory Dump Only | Metadata encrypted on disk + EAC |

---

## Why Some Games Fail Static Dumps

### 1. Encrypted `global-metadata.dat`
Standard IL2CPP metadata starts with the 4-byte magic `AF 1B B1 FA` (`0xFAB11BAF`). Certain studios encrypt or scramble this file on disk:
- **HoYoverse titles** (ZZZ, Genshin, Star Rail): Header starts with `4D 48 59 00` (`MHY\0`).
- **VRChat**: Header starts with `17 84 7D 7E` (`0x7E7D8417`).

Because the disk file is ciphertext, static tools cannot read the type definitions without the decryption algorithm and key. The game decrypts the metadata into memory during startup.

**Workaround:** Extract the decrypted metadata buffer from RAM while the game is running (see `frida-runtime-dumper/`), then feed it to the dumper:
```bash
il2cpp-dumper -i GameAssembly.dll -m decrypted_metadata.dat -o ./dump --all
```

### 2. The Game Uses Mono, Not IL2CPP
Some games (*Muck*, *Lethal Company*, *Valheim*, *SCP: Secret Laboratory*) use Unity's Mono backend rather than IL2CPP. These games do not have a `GameAssembly.dll` or `global-metadata.dat`.

**Workaround:** You do not need an IL2CPP dumper. The game's original C# assemblies are already present in:
```
<Game>_Data/Managed/Assembly-CSharp.dll
```
Open that file directly in [dnSpy](https://github.com/dnSpy/dnSpy) or [ILSpy](https://github.com/icsharpcode/ILSpy) to view the decompiled C# source code.

### 3. Kernel-Level Anti-Cheat (EAC, BattlEye, Vanguard, HoYoKProtect)
Games running Ring 0 kernel drivers block external processes from attaching debuggers or reading process memory on Windows. While this does not stop static dumping of unencrypted files, it prevents live memory dumping on PC.

**Workaround:** For cross-platform titles, researchers often dump the Android/iOS build inside an emulator or rooted device instead. The metadata and C# logic are shared across platforms, but Android builds do not require Windows kernel drivers.

### 4. Code Obfuscation (Beebyte, Babel)
Titles like *Goose Goose Duck* and *Crab Game* run obfuscators before IL2CPP compilation. Symbol names may contain invalid characters, unicode homoglyphs, or empty strings.

**Status:** Handled automatically. The dumper sanitizes illegal characters so that dumped `.cs` files and `DummyDll/*.dll` assemblies load properly in decompilers.

### 5. Split APKs (.xapk / .apkm)
Modern Android releases split binaries across multiple packages (e.g., `libil2cpp.so` in `config.arm64_v8a.apk` and `global-metadata.dat` in `base.apk`).

**Status:** Handled automatically. Drop the `.xapk`, `.apkm`, or `.zip` file directly into the dumper; it unpacks and pairs the binary and metadata automatically.

---

## Pre-Check: Is My Metadata Encrypted?

Run this in PowerShell to check the first 4 bytes of any `global-metadata.dat`:

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
