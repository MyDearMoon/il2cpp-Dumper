using System.Text;
using Il2CppDumper.Core.Model;

namespace Il2CppDumper.Core.Exporters;

public sealed class CppSdkExporter : IExporter
{
    public string Name => "C++ Modding SDK (il2cpp.h & Visual Studio Scaffolding)";

    public void Export(DumpContext context, string outputDirectory, ExportOptions options, Action<string>? logger = null)
    {
        if (!options.ExportCppSdk) return;

        var sdkDir = Path.Combine(outputDirectory, "cpp-sdk");
        Directory.CreateDirectory(sdkDir);
        logger?.Invoke($"Exporting C++ Modding SDK to: {sdkDir}...");

        // 1. Generate il2cpp.h
        ExportHeader(context, sdkDir, logger);

        // 2. Generate il2cpp-init.h
        ExportInitHeader(context, sdkDir, logger);

        // 3. Generate sample hooking dllmain.cpp
        ExportDllMain(context, sdkDir, logger);

        logger?.Invoke($"C++ Modding SDK generated in {sdkDir}");
    }

    private static void ExportHeader(DumpContext context, string sdkDir, Action<string>? logger)
    {
        var headerPath = Path.Combine(sdkDir, "il2cpp.h");
        using var writer = new StreamWriter(headerPath, false, Encoding.UTF8);

        writer.WriteLine("// ===========================================================================");
        writer.WriteLine("// Auto-generated C++ SDK by Il2Cpp-Dumper (All-in-One)");
        writer.WriteLine($"// Metadata Version: {context.MetadataVersion} | Unity Version: {context.UnityVersion}");
        writer.WriteLine($"// Architecture: {context.Architecture} | Format: {context.Format}");
        writer.WriteLine("// ===========================================================================");
        writer.WriteLine("#pragma once");
        writer.WriteLine("#include <cstdint>");
        writer.WriteLine("#include <cstddef>");
        writer.WriteLine();

        writer.WriteLine("// Forward Declarations");
        foreach (var img in context.Images)
        {
            foreach (var type in img.Types)
            {
                if (!type.IsEnum)
                {
                    writer.WriteLine($"struct {SanitizeCpp(type.FullName)};");
                }
            }
        }
        writer.WriteLine();

        // Struct definitions
        foreach (var img in context.Images)
        {
            writer.WriteLine($"// ---------------------------------------------------------------------------");
            writer.WriteLine($"// Assembly: {img.Name}");
            writer.WriteLine($"// ---------------------------------------------------------------------------");

            foreach (var type in img.Types)
            {
                var cppName = SanitizeCpp(type.FullName);
                if (type.IsEnum)
                {
                    writer.WriteLine($"enum class {cppName} : int32_t {{");
                    foreach (var f in type.Fields.Where(f => f.IsConst))
                    {
                        writer.WriteLine($"    {SanitizeCpp(f.Name)},");
                    }
                    writer.WriteLine("};");
                    writer.WriteLine();
                    continue;
                }

                writer.WriteLine($"struct {cppName} {{");
                foreach (var field in type.Fields.Where(f => !f.IsStatic))
                {
                    var offsetStr = field.Offset >= 0 ? $" // 0x{field.Offset:X}" : "";
                    writer.WriteLine($"    uint8_t pad_{SanitizeCpp(field.Name)}[0x8];{offsetStr}");
                }
                writer.WriteLine("};");
                writer.WriteLine();

                // Method function pointer typedefs
                foreach (var m in type.Methods)
                {
                    if (m.Rva == 0) continue;
                    var typedefName = $"{cppName}_{SanitizeCpp(m.Name)}_t";
                    var paramList = $"{cppName}* __this";
                    if (m.Parameters.Count > 0)
                    {
                        paramList += ", " + string.Join(", ", m.Parameters.Select(p => $"void* {SanitizeCpp(p.Name)}"));
                    }
                    paramList += ", const void* method";

                    writer.WriteLine($"// RVA: 0x{m.Rva:X} | Slot: {m.Slot}");
                    writer.WriteLine($"typedef void* (*{typedefName})({paramList});");
                }
                writer.WriteLine();
            }
        }

        logger?.Invoke($"Wrote: {headerPath}");
    }

    private static void ExportInitHeader(DumpContext context, string sdkDir, Action<string>? logger)
    {
        var initPath = Path.Combine(sdkDir, "il2cpp-init.h");
        using var writer = new StreamWriter(initPath, false, Encoding.UTF8);

        writer.WriteLine("#pragma once");
        writer.WriteLine("#include <cstdint>");
        writer.WriteLine("#include \"il2cpp.h\"");
        writer.WriteLine();
        writer.WriteLine("namespace Il2CppSDK {");
        writer.WriteLine("    inline uintptr_t BaseAddress = 0;");
        writer.WriteLine();
        writer.WriteLine("    inline void Initialize(uintptr_t moduleBase) {");
        writer.WriteLine("        BaseAddress = moduleBase;");
        writer.WriteLine("    }");
        writer.WriteLine();
        writer.WriteLine("    template <typename T>");
        writer.WriteLine("    inline T ResolveRva(uintptr_t rva) {");
        writer.WriteLine("        return reinterpret_cast<T>(BaseAddress + rva);");
        writer.WriteLine("    }");
        writer.WriteLine("}");

        logger?.Invoke($"Wrote: {initPath}");
    }

    private static void ExportDllMain(DumpContext context, string sdkDir, Action<string>? logger)
    {
        var dllMainPath = Path.Combine(sdkDir, "dllmain.cpp");
        using var writer = new StreamWriter(dllMainPath, false, Encoding.UTF8);

        writer.WriteLine("// Auto-generated hooking entrypoint template by Il2Cpp-Dumper (All-in-One)");
        writer.WriteLine("#include <windows.h>");
        writer.WriteLine("#include <iostream>");
        writer.WriteLine("#include \"il2cpp-init.h\"");
        writer.WriteLine();
        writer.WriteLine("DWORD WINAPI MainThread(LPVOID lpParam) {");
        writer.WriteLine("    // 1. Locate game assembly base");
        writer.WriteLine("    uintptr_t base = reinterpret_cast<uintptr_t>(GetModuleHandleA(\"GameAssembly.dll\"));");
        writer.WriteLine("    if (!base) base = reinterpret_cast<uintptr_t>(GetModuleHandleA(nullptr));");
        writer.WriteLine();
        writer.WriteLine("    Il2CppSDK::Initialize(base);");
        writer.WriteLine("    std::cout << \"[+] Il2CppSDK initialized at base: 0x\" << std::hex << base << std::endl;");
        writer.WriteLine();
        writer.WriteLine("    // 2. Install your hooks here with MinHook or Dobby");
        writer.WriteLine("    // Example:");
        writer.WriteLine("    // auto targetFunc = Il2CppSDK::ResolveRva<TargetFunction_t>(0x123456);");
        writer.WriteLine("    // MH_CreateHook(targetFunc, &HookedFunction, reinterpret_cast<LPVOID*>(&OriginalFunction));");
        writer.WriteLine("    // MH_EnableHook(targetFunc);");
        writer.WriteLine();
        writer.WriteLine("    return 0;");
        writer.WriteLine("}");
        writer.WriteLine();
        writer.WriteLine("BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved) {");
        writer.WriteLine("    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {");
        writer.WriteLine("        DisableThreadLibraryCalls(hModule);");
        writer.WriteLine("        CreateThread(nullptr, 0, MainThread, hModule, 0, nullptr);");
        writer.WriteLine("    }");
        writer.WriteLine("    return TRUE;");
        writer.WriteLine("}");

        logger?.Invoke($"Wrote: {dllMainPath}");
    }

    private static string SanitizeCpp(string name)
    {
        if (string.IsNullOrEmpty(name)) return "unnamed";
        return name
            .Replace('.', '_')
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('$', '_')
            .Replace('`', '_')
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace(':', '_');
    }
}
