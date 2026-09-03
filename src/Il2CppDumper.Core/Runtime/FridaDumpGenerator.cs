using System.Text;

namespace Il2CppDumper.Core.Runtime;

public static class FridaDumpGenerator
{
    public static void GenerateScripts(string outputDirectory, Action<string>? logger = null)
    {
        var runtimeDir = Path.Combine(outputDirectory, "frida-runtime-dumper");
        Directory.CreateDirectory(runtimeDir);

        // 1. Android Memory Dumper
        var androidScriptPath = Path.Combine(runtimeDir, "frida-dump-android.js");
        File.WriteAllText(androidScriptPath, GetAndroidScript(), Encoding.UTF8);

        // 2. PC Memory Dumper
        var pcScriptPath = Path.Combine(runtimeDir, "frida-dump-pc.js");
        File.WriteAllText(pcScriptPath, GetPcScript(), Encoding.UTF8);

        // 3. Quick-start launcher batch / shell
        var batPath = Path.Combine(runtimeDir, "run-android.bat");
        File.WriteAllText(batPath, "@echo off\r\nset /p PKG=\"Enter Android package name (e.g. com.company.game): \"\r\nfrida -U -f %PKG% -l frida-dump-android.js --no-pause\r\npause\r\n", Encoding.ASCII);

        logger?.Invoke($"Generated runtime Frida memory dumpers in: {runtimeDir}");
    }

    private static string GetAndroidScript()
    {
        return @"// Frida In-Memory Dumper for Protected Unity IL2CPP Games
// Bypasses encrypted global-metadata.dat & packed libil2cpp.so
// Usage: frida -U -f <package_name> -l frida-dump-android.js --no-pause

console.log('[+] Il2Cpp In-Memory Dumper injected.');

function dumpMemory(name, base, size) {
    var filename = '/sdcard/Download/' + name;
    console.log('[*] Dumping ' + name + ' from ' + base + ' (size: ' + size + ' bytes) to ' + filename);
    
    var file = new File(filename, 'wb');
    if (!file) {
        console.log('[-] Failed to open ' + filename + ' for writing.');
        return;
    }

    var buffer = base.readByteArray(size);
    file.write(buffer);
    file.flush();
    file.close();
    console.log('[+] Successfully saved: ' + filename);
}

function searchMetadataInMemory() {
    var SANITY = 0xFAB11BAF;
    Process.enumerateRanges('r--').forEach(function(range) {
        try {
            if (range.size < 0x1000) return;
            var magic = range.base.readU32();
            if (magic === SANITY) {
                var version = range.base.add(4).readS32();
                console.log('[+] FOUND global-metadata.dat in RAM at: ' + range.base + ' (version: ' + version + ')');
                dumpMemory('global-metadata.dat', range.base, range.size);
            }
        } catch(e) {}
    });
}

function hookIl2Cpp() {
    var il2cpp = Process.findModuleByName('libil2cpp.so');
    if (il2cpp) {
        console.log('[+] Found libil2cpp.so loaded at: ' + il2cpp.base + ' (size: ' + il2cpp.size + ')');
        dumpMemory('libil2cpp.so', il2cpp.base, il2cpp.size);
        searchMetadataInMemory();
        return;
    }

    // Hook dlopen / android_dlopen_ext to catch libil2cpp when loaded
    var dlopen = Module.findExportByName(null, 'android_dlopen_ext') || Module.findExportByName(null, 'dlopen');
    if (dlopen) {
        Interceptor.attach(dlopen, {
            onEnter: function(args) {
                this.name = args[0].readCString();
            },
            onLeave: function(retval) {
                if (this.name && this.name.indexOf('libil2cpp.so') !== -1) {
                    console.log('[+] Detected libil2cpp.so load!');
                    setTimeout(function() {
                        var mod = Process.findModuleByName('libil2cpp.so');
                        if (mod) {
                            dumpMemory('libil2cpp.so', mod.base, mod.size);
                        }
                        searchMetadataInMemory();
                    }, 1000);
                }
            }
        });
    }
}

hookIl2Cpp();
";
    }

    private static string GetPcScript()
    {
        return @"// Frida In-Memory Dumper for Windows Unity Games
// Usage: frida -n Game.exe -l frida-dump-pc.js

console.log('[+] PC Il2Cpp Dumper script loaded.');

function searchMetadata() {
    var SANITY = 0xFAB11BAF;
    Process.enumerateRanges('r--').forEach(function(range) {
        try {
            if (range.size < 0x1000) return;
            var magic = range.base.readU32();
            if (magic === SANITY) {
                var version = range.base.add(4).readS32();
                console.log('[+] Found global-metadata.dat in memory at: ' + range.base + ' (version: ' + version + ')');
            }
        } catch(e) {}
    });
}

var mod = Process.findModuleByName('GameAssembly.dll');
if (mod) {
    console.log('[+] Found GameAssembly.dll at: ' + mod.base + ' (size: ' + mod.size + ')');
    searchMetadata();
} else {
    console.log('[-] GameAssembly.dll not found in current process.');
}
";
    }
}
