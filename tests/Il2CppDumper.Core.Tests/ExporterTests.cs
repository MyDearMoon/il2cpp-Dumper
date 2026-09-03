// Unit tests for Il2CppDumper
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Exporters;
using Il2CppDumper.Core.Model;
using Il2CppDumper.Core.Runtime;
using Mono.Cecil;
using Xunit;

namespace Il2CppDumper.Core.Tests;

public class ExporterTests
{
    private DumpContext CreateMockContext()
    {
        var ctx = new DumpContext
        {
            MetadataVersion = 29f,
            UnityVersion = "2021.3.0f1",
            Architecture = Architecture.Arm64,
            Format = BinaryFormat.Elf
        };

        ctx.StringLiterals.Add("Hello World");
        ctx.StringLiterals.Add("UnityPlayer");

        var img = new ImageModel { Name = "Assembly-CSharp.dll" };
        var playerType = new TypeModel
        {
            ImageName = "Assembly-CSharp.dll",
            Namespace = "Game.Gameplay",
            Name = "PlayerController",
            TypeDefIndex = 10,
            IsPublic = true
        };

        playerType.Fields.Add(new FieldModel
        {
            Name = "health",
            TypeName = "System.Int32",
            Offset = 0x18,
            IsPublic = true
        });

        playerType.Fields.Add(new FieldModel
        {
            Name = "speed",
            TypeName = "System.Single",
            Offset = 0x1C,
            IsPrivate = true
        });

        playerType.Fields.Add(new FieldModel
        {
            Name = "initial",
            TypeName = "System.Char",
            Offset = 0x20,
            IsPublic = true
        });

        playerType.Fields.Add(new FieldModel
        {
            Name = "target",
            TypeName = "UnityEngine.GameObject",
            Offset = 0x28,
            IsPublic = true
        });

        var ctorMethod = new MethodModel
        {
            Name = ".ctor",
            ReturnType = "System.Void",
            IsPublic = true
        };
        playerType.Methods.Add(ctorMethod);

        var method = new MethodModel
        {
            Name = "TakeDamage",
            ReturnType = "System.Void",
            Rva = 0x123456,
            FileOffset = 0x122456,
            MethodPointer = 0x7100123456,
            IsPublic = true
        };
        method.Parameters.Add(new ParameterModel { Name = "amount", TypeName = "System.Int32" });
        playerType.Methods.Add(method);

        var getterMethod = new MethodModel
        {
            Name = "get_Health",
            ReturnType = "System.Int32",
            IsPublic = true
        };
        playerType.Methods.Add(getterMethod);

        playerType.Properties.Add(new PropertyModel
        {
            Name = "Health",
            TypeName = "System.Int32",
            Getter = getterMethod
        });

        var structType = new TypeModel
        {
            ImageName = "Assembly-CSharp.dll",
            Namespace = "Game.Gameplay",
            Name = "PlayerStats",
            IsValueType = true,
            IsPublic = true
        };
        structType.Fields.Add(new FieldModel
        {
            Name = "level",
            TypeName = "System.Int32",
            Offset = 0x0
        });

        var enumType = new TypeModel
        {
            ImageName = "Assembly-CSharp.dll",
            Namespace = "Game.Gameplay",
            Name = "PlayerState",
            IsEnum = true,
            IsValueType = true,
            IsPublic = true
        };
        enumType.Fields.Add(new FieldModel
        {
            Name = "Idle",
            TypeName = "System.Int32",
            IsStatic = true,
            IsConst = true
        });

        img.Types.Add(playerType);
        img.Types.Add(structType);
        img.Types.Add(enumType);
        ctx.Images.Add(img);

        // Add second image to test cross-assembly reference resolution
        var unityImg = new ImageModel { Name = "UnityEngine.CoreModule.dll" };
        unityImg.Types.Add(new TypeModel
        {
            ImageName = "UnityEngine.CoreModule.dll",
            Namespace = "UnityEngine",
            Name = "GameObject",
            IsPublic = true
        });
        ctx.Images.Add(unityImg);

        return ctx;
    }

    [Fact]
    public void DumpCsExporter_GeneratesValidFiles()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"dumper_test_{Guid.NewGuid():N}");
        try
        {
            var ctx = CreateMockContext();
            var exporter = new DumpCsExporter();
            exporter.Export(ctx, outDir, ExportOptions.All);

            var dumpCs = Path.Combine(outDir, "dump.cs");
            var scriptJson = Path.Combine(outDir, "script.json");
            var strJson = Path.Combine(outDir, "stringliteral.json");

            Assert.True(File.Exists(dumpCs));
            Assert.True(File.Exists(scriptJson));
            Assert.True(File.Exists(strJson));

            var dumpCsContent = File.ReadAllText(dumpCs);
            Assert.Contains("namespace Game.Gameplay", dumpCsContent);
            Assert.Contains("class PlayerController", dumpCsContent);
            Assert.Contains("TakeDamage", dumpCsContent);
            Assert.Contains("0x123456", dumpCsContent);

            var scriptContent = File.ReadAllText(scriptJson);
            Assert.Contains("Game.Gameplay.PlayerController::TakeDamage", scriptContent);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void ScriptExporter_GeneratesDisassemblerScripts()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"script_test_{Guid.NewGuid():N}");
        try
        {
            var ctx = CreateMockContext();
            var exporter = new ScriptExporter();
            exporter.Export(ctx, outDir, ExportOptions.All);

            Assert.True(File.Exists(Path.Combine(outDir, "ida.py")));
            Assert.True(File.Exists(Path.Combine(outDir, "ghidra.py")));
            Assert.True(File.Exists(Path.Combine(outDir, "binja.py")));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void DummyAssemblyExporter_GeneratesLoadableDotNetAssembly()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"dummy_test_{Guid.NewGuid():N}");
        try
        {
            var ctx = CreateMockContext();
            var exporter = new DummyAssemblyExporter();
            exporter.Export(ctx, outDir, ExportOptions.All);

            var dllPath = Path.Combine(outDir, "DummyDll", "Assembly-CSharp.dll");
            Assert.True(File.Exists(dllPath));

            using var assembly = AssemblyDefinition.ReadAssembly(dllPath);
            Assert.NotNull(assembly);
            Assert.Equal("Assembly-CSharp", assembly.Name.Name);

            var type = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "PlayerController");
            Assert.NotNull(type);
            Assert.Equal("Game.Gameplay", type.Namespace);

            var method = type.Methods.FirstOrDefault(m => m.Name == "TakeDamage");
            Assert.NotNull(method);
            Assert.Single(method.Parameters);
            Assert.Equal("amount", method.Parameters[0].Name);
            Assert.Equal("System.Int32", method.Parameters[0].ParameterType.FullName);
            Assert.Equal("System.Void", method.ReturnType.FullName);

            var field = type.Fields.FirstOrDefault(f => f.Name == "health");
            Assert.NotNull(field);
            Assert.Equal("System.Int32", field.FieldType.FullName);

            // Assert property
            var prop = type.Properties.FirstOrDefault(p => p.Name == "Health");
            Assert.NotNull(prop);
            Assert.Equal("System.Int32", prop.PropertyType.FullName);
            Assert.NotNull(prop.GetMethod);
            Assert.Equal("get_Health", prop.GetMethod.Name);

            // Assert value type struct
            var structDef = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "PlayerStats");
            Assert.NotNull(structDef);
            Assert.Equal("System.ValueType", structDef.BaseType.FullName);

            // Assert constructor (.ctor) is preserved and has SpecialName + RTSpecialName
            var ctor = type.Methods.FirstOrDefault(m => m.Name == ".ctor");
            Assert.NotNull(ctor);
            Assert.True(ctor.Attributes.HasFlag(MethodAttributes.SpecialName));
            Assert.True(ctor.Attributes.HasFlag(MethodAttributes.RTSpecialName));

            // Assert cross-assembly type reference scopes to UnityEngine.CoreModule (not CoreLibrary/corlib)
            var targetField = type.Fields.FirstOrDefault(f => f.Name == "target");
            Assert.NotNull(targetField);
            Assert.Equal("UnityEngine.GameObject", targetField.FieldType.FullName);
            Assert.Equal("UnityEngine.CoreModule", targetField.FieldType.Scope.Name);

            // Assert enum has synthesized value__ backing field with SpecialName + RTSpecialName
            var enumDef = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "PlayerState");
            Assert.NotNull(enumDef);
            var valueField = enumDef.Fields.FirstOrDefault(f => f.Name == "value__");
            Assert.NotNull(valueField);
            Assert.True(valueField.Attributes.HasFlag(FieldAttributes.SpecialName));
            Assert.True(valueField.Attributes.HasFlag(FieldAttributes.RTSpecialName));
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void CppSdkExporter_GeneratesCppHeadersAndScaffolding()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"cpp_test_{Guid.NewGuid():N}");
        try
        {
            var ctx = CreateMockContext();
            var exporter = new CppSdkExporter();
            exporter.Export(ctx, outDir, ExportOptions.All);

            var headerPath = Path.Combine(outDir, "cpp-sdk", "il2cpp.h");
            var initPath = Path.Combine(outDir, "cpp-sdk", "il2cpp-init.h");
            var dllMainPath = Path.Combine(outDir, "cpp-sdk", "dllmain.cpp");

            Assert.True(File.Exists(headerPath));
            Assert.True(File.Exists(initPath));
            Assert.True(File.Exists(dllMainPath));

            var header = File.ReadAllText(headerPath);
            Assert.Contains("struct Il2CppObject", header);
            Assert.Contains("#pragma pack(push, 1)", header);
            Assert.Contains("#pragma pack(pop)", header);
            Assert.Contains("char16_t initial; // Offset: 0x20", header);
            Assert.Contains("struct Game_Gameplay_PlayerController : public Il2CppObject {", header);
            Assert.Contains("uint8_t _pad_0x10[0x8];", header);
            Assert.Contains("int32_t health; // Offset: 0x18", header);
            Assert.Contains("struct Game_Gameplay_PlayerStats {", header);
            Assert.Contains("PlayerController", header);
            Assert.Contains("TakeDamage", header);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }

    [Fact]
    public void FridaDumpGenerator_GeneratesRuntimeScripts()
    {
        var outDir = Path.Combine(Path.GetTempPath(), $"frida_test_{Guid.NewGuid():N}");
        try
        {
            FridaDumpGenerator.GenerateScripts(outDir);

            var androidScript = Path.Combine(outDir, "frida-runtime-dumper", "frida-dump-android.js");
            var pcScript = Path.Combine(outDir, "frida-runtime-dumper", "frida-dump-pc.js");
            var bat = Path.Combine(outDir, "frida-runtime-dumper", "run-android.bat");

            Assert.True(File.Exists(androidScript));
            Assert.True(File.Exists(pcScript));
            Assert.True(File.Exists(bat));

            var content = File.ReadAllText(androidScript);
            Assert.Contains("0xFAB11BAF", content);
            Assert.Contains("libil2cpp.so", content);
        }
        finally
        {
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        }
    }
}
