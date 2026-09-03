using System.Text;
using Il2CppDumper.Core.Containers;
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

        // 1. Standard Il2CppObject Header
        writer.WriteLine("// Base IL2CPP Object header for managed reference types");
        writer.WriteLine("struct Il2CppObject {");
        writer.WriteLine("    void* klass;   // Il2CppClass*");
        writer.WriteLine("    void* monitor; // MonitorData*");
        writer.WriteLine("};");
        writer.WriteLine();

        // 2. Forward Declarations
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

        var is64Bit = context.Architecture is Architecture.Arm64 or Architecture.X64 or Architecture.Unknown;
        var headerSize = is64Bit ? 0x10 : 0x8;

        // 3. Struct definitions with true memory-aligned layout
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

                // Reference types inherit from Il2CppObject; value types don't have object header
                if (type.IsValueType)
                {
                    writer.WriteLine($"struct {cppName} {{");
                }
                else
                {
                    writer.WriteLine($"struct {cppName} : public Il2CppObject {{");
                }

                // Layout non-static instance fields by memory offset
                var instanceFields = type.Fields
                    .Where(f => !f.IsStatic && f.Offset >= 0)
                    .OrderBy(f => f.Offset)
                    .ToList();

                var currentOffset = type.IsValueType ? 0 : headerSize;

                for (var i = 0; i < instanceFields.Count; i++)
                {
                    var field = instanceFields[i];
                    var fieldName = SanitizeCpp(field.Name);

                    // Insert gap padding if needed
                    if (field.Offset > currentOffset)
                    {
                        var padBytes = field.Offset - currentOffset;
                        writer.WriteLine($"    uint8_t _pad_0x{currentOffset:X}[0x{padBytes:X}];");
                        currentOffset = field.Offset;
                    }

                    // Next field offset for sizing unknown types
                    var nextOffset = (i + 1 < instanceFields.Count) ? instanceFields[i + 1].Offset : -1;
                    var (cppType, typeSize) = MapCppType(field.TypeName, is64Bit);

                    if (typeSize > 0)
                    {
                        writer.WriteLine($"    {cppType} {fieldName}; // Offset: 0x{field.Offset:X}");
                        currentOffset += typeSize;
                    }
                    else if (nextOffset > field.Offset)
                    {
                        var fieldSpan = nextOffset - field.Offset;
                        writer.WriteLine($"    uint8_t {fieldName}[0x{fieldSpan:X}]; // Offset: 0x{field.Offset:X} ({field.TypeName})");
                        currentOffset = nextOffset;
                    }
                    else
                    {
                        // Fallback pointer size for last/unbounded field
                        var fallbackSize = is64Bit ? 8 : 4;
                        writer.WriteLine($"    void* {fieldName}; // Offset: 0x{field.Offset:X} ({field.TypeName})");
                        currentOffset += fallbackSize;
                    }
                }

                writer.WriteLine("};");
                writer.WriteLine();

                // Method function pointer typedefs with concrete parameter and return types
                foreach (var m in type.Methods)
                {
                    if (m.Rva == 0) continue;
                    var typedefName = $"{cppName}_{SanitizeCpp(m.Name)}_t";
                    var retType = MapCppType(m.ReturnType, is64Bit).CppType;

                    var paramList = $"{cppName}* __this";
                    if (m.Parameters.Count > 0)
                    {
                        var mappedParams = m.Parameters.Select(p =>
                        {
                            var pType = MapCppType(p.TypeName, is64Bit).CppType;
                            return $"{pType} {SanitizeCpp(p.Name)}";
                        });
                        paramList += ", " + string.Join(", ", mappedParams);
                    }
                    paramList += ", const void* method";

                    writer.WriteLine($"// RVA: 0x{m.Rva:X} | Slot: {m.Slot}");
                    writer.WriteLine($"typedef {retType} (*{typedefName})({paramList});");
                }
                writer.WriteLine();
            }
        }

        logger?.Invoke($"Wrote: {headerPath}");
    }

    private static (string CppType, int Size) MapCppType(string? csharpType, bool is64Bit)
    {
        if (string.IsNullOrWhiteSpace(csharpType))
            return ("void", 0);

        var trimmed = csharpType.Trim();
        var ptrSize = is64Bit ? 8 : 4;

        if (trimmed.EndsWith('*') || trimmed.EndsWith('&') || trimmed.EndsWith("[]"))
            return ("void*", ptrSize);

        return trimmed switch
        {
            "void" or "System.Void" => ("void", 0),
            "bool" or "System.Boolean" => ("bool", 1),
            "byte" or "System.Byte" => ("uint8_t", 1),
            "sbyte" or "System.SByte" => ("int8_t", 1),
            "char" or "System.Char" => ("wchar_t", 2),
            "short" or "System.Int16" => ("int16_t", 2),
            "ushort" or "System.UInt16" => ("uint16_t", 2),
            "int" or "System.Int32" => ("int32_t", 4),
            "uint" or "System.UInt32" => ("uint32_t", 4),
            "long" or "System.Int64" => ("int64_t", 8),
            "ulong" or "System.UInt64" => ("uint64_t", 8),
            "float" or "System.Single" => ("float", 4),
            "double" or "System.Double" => ("double", 8),
            "string" or "System.String" => ("void*", ptrSize),
            "object" or "System.Object" => ("void*", ptrSize),
            "IntPtr" or "System.IntPtr" => ("void*", ptrSize),
            "UIntPtr" or "System.UIntPtr" => ("uintptr_t", ptrSize),
            _ => ("void*", -1) // -1 indicates custom/complex type whose size depends on offsets
        };
    }

    private static void ExportInitHeader(DumpContext context, string sdkDir, Action<string>? logger)
    {
        var initPath = Path.Combine(sdkDir, "il2cpp-init.h");
        using var writer = new StreamWriter(initPath, false, Encoding.UTF8);

        writer.WriteLine("#pragma once");
        writer.WriteLine("#include <cstdint>");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("#include <windows.h>");
        writer.WriteLine("#else");
        writer.WriteLine("#include <dlfcn.h>");
        writer.WriteLine("#endif");
        writer.WriteLine();
        writer.WriteLine("inline uintptr_t GetIl2CppBase()");
        writer.WriteLine("{");
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("    return reinterpret_cast<uintptr_t>(GetModuleHandleA(\"GameAssembly.dll\"));");
        writer.WriteLine("#else");
        writer.WriteLine("    // Linux / Android libil2cpp.so resolver");
        writer.WriteLine("    return reinterpret_cast<uintptr_t>(dlopen(\"libil2cpp.so\", RTLD_NOLOAD));");
        writer.WriteLine("#endif");
        writer.WriteLine("}");
        writer.WriteLine();
        writer.WriteLine("template <typename T>");
        writer.WriteLine("inline T ResolveMethod(uintptr_t rva)");
        writer.WriteLine("{");
        writer.WriteLine("    uintptr_t base = GetIl2CppBase();");
        writer.WriteLine("    if (!base) return nullptr;");
        writer.WriteLine("    return reinterpret_cast<T>(base + rva);");
        writer.WriteLine("}");

        logger?.Invoke($"Wrote: {initPath}");
    }

    private static void ExportDllMain(DumpContext context, string sdkDir, Action<string>? logger)
    {
        var dllMainPath = Path.Combine(sdkDir, "dllmain.cpp");
        using var writer = new StreamWriter(dllMainPath, false, Encoding.UTF8);

        writer.WriteLine("// ===========================================================================");
        writer.WriteLine("// MinHook / Dobby Function Hooking Scaffold");
        writer.WriteLine("// ===========================================================================");
        writer.WriteLine("#include \"il2cpp.h\"");
        writer.WriteLine("#include \"il2cpp-init.h\"");
        writer.WriteLine();
        writer.WriteLine("#if defined(_WIN32)");
        writer.WriteLine("#include <windows.h>");
        writer.WriteLine();
        writer.WriteLine("DWORD WINAPI MainThread(LPVOID lpParam)");
        writer.WriteLine("{");
        writer.WriteLine("    // 1. Wait for GameAssembly.dll to initialize");
        writer.WriteLine("    while (!GetIl2CppBase()) {");
        writer.WriteLine("        Sleep(100);");
        writer.WriteLine("    }");
        writer.WriteLine();
        writer.WriteLine("    // 2. Initialize hooking framework (e.g. MinHook)");
        writer.WriteLine("    // MH_Initialize();");
        writer.WriteLine("    // MH_CreateHook(reinterpret_cast<LPVOID>(GetIl2CppBase() + 0x123456), &Hooked_Method, reinterpret_cast<LPVOID*>(&Original_Method));");
        writer.WriteLine("    // MH_EnableHook(MH_ALL_HOOKS);");
        writer.WriteLine();
        writer.WriteLine("    return 0;");
        writer.WriteLine("}");
        writer.WriteLine();
        writer.WriteLine("BOOL APIENTRY DllMain(HMODULE hModule, DWORD ul_reason_for_call, LPVOID lpReserved)");
        writer.WriteLine("{");
        writer.WriteLine("    if (ul_reason_for_call == DLL_PROCESS_ATTACH) {");
        writer.WriteLine("        DisableThreadLibraryCalls(hModule);");
        writer.WriteLine("        CreateThread(nullptr, 0, MainThread, hModule, 0, nullptr);");
        writer.WriteLine("    }");
        writer.WriteLine("    return TRUE;");
        writer.WriteLine("}");
        writer.WriteLine("#endif");

        logger?.Invoke($"Wrote: {dllMainPath}");
    }

    private static string SanitizeCpp(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_unnamed";
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }
        var res = sb.ToString();
        if (char.IsDigit(res[0])) res = "_" + res;
        return res;
    }
}
