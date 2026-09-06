using System.Reflection;
using AssetRipper.Primitives;
using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Model;
using LibCpp2IL.Metadata;

namespace Il2CppDumper.Core.Metadata;

public static class MetadataOnlyDumper
{
    private static string SafeGetString(Il2CppMetadata meta, int index)
    {
        if (index < 0) return string.Empty;
        try
        {
            return meta.GetStringFromIndex(index) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static DumpContext Dump(string metadataPath, string? binaryPath, UnityVersion unityVersion, Action<string>? logger = null)
    {
        logger?.Invoke("Parsing metadata structures in metadata-only mode...");
        var bytes = File.ReadAllBytes(metadataPath);

        var meta = Il2CppMetadata.ReadFrom(bytes, unityVersion);
        logger?.Invoke($"Parsed Il2CppMetadata (version: {meta.MetadataVersion})");

        var dumpContext = new DumpContext
        {
            MetadataVersion = meta.MetadataVersion,
            UnityVersion = unityVersion.ToString(),
            Architecture = Architecture.Arm64,
            Format = BinaryFormat.Elf
        };

        var metaType = meta.GetType();
        var imgField = metaType.GetField("imageDefinitions", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var typeDefsField = metaType.GetField("typeDefs", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var methodDefsField = metaType.GetField("methodDefs", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var fieldDefsField = metaType.GetField("fieldDefs", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var paramDefsField = metaType.GetField("parameterDefs", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var strLiteralsField = metaType.GetField("stringLiterals", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        var images = (Il2CppImageDefinition[]?)imgField?.GetValue(meta) ?? Array.Empty<Il2CppImageDefinition>();
        var typeDefs = (Il2CppTypeDefinition[]?)typeDefsField?.GetValue(meta) ?? Array.Empty<Il2CppTypeDefinition>();
        var methodDefs = (Il2CppMethodDefinition[]?)methodDefsField?.GetValue(meta) ?? Array.Empty<Il2CppMethodDefinition>();
        var fieldDefs = (Il2CppFieldDefinition[]?)fieldDefsField?.GetValue(meta) ?? Array.Empty<Il2CppFieldDefinition>();
        var paramDefs = (Il2CppParameterDefinition[]?)paramDefsField?.GetValue(meta) ?? Array.Empty<Il2CppParameterDefinition>();
        var stringLiterals = (Il2CppStringLiteral[]?)strLiteralsField?.GetValue(meta) ?? Array.Empty<Il2CppStringLiteral>();

        // Collect string literals
        for (int i = 0; i < stringLiterals.Length; i++)
        {
            try
            {
                var str = meta.GetStringLiteralFromIndex((uint)i);
                if (!string.IsNullOrEmpty(str))
                {
                    dumpContext.StringLiterals.Add(str);
                }
            }
            catch { }
        }

        logger?.Invoke($"Reconstructing {images.Length} assemblies and {typeDefs.Length} types...");

        foreach (var img in images)
        {
            var imgName = SafeGetString(meta, img.nameIndex);
            if (string.IsNullOrEmpty(imgName)) continue;

            var imageModel = new ImageModel { Name = imgName };

            int firstType = (int)img.firstTypeIndex.Value;
            int typeCount = (int)img.typeCount;

            for (int t = firstType; t < firstType + typeCount && t < typeDefs.Length; t++)
            {
                var td = typeDefs[t];
                var typeName = SafeGetString(meta, td.NameIndex);
                var typeNamespace = SafeGetString(meta, td.NamespaceIndex);

                var isValueType = (td.Bitfield & 0x1) != 0;
                var isEnum = (td.Bitfield & 0x2) != 0;

                var typeModel = new TypeModel
                {
                    ImageName = imgName,
                    Namespace = typeNamespace,
                    Name = typeName,
                    TypeDefIndex = t,
                    IsValueType = isValueType,
                    IsEnum = isEnum,
                    IsInterface = (td.Flags & 0x00000020) != 0,
                    IsAbstract = (td.Flags & 0x00000080) != 0,
                    IsPublic = (td.Flags & 0x00000001) != 0,
                    BaseTypeName = isEnum ? "System.Enum" : (isValueType ? "System.ValueType" : "System.Object")
                };

                // Fields
                int firstField = (int)td.FirstFieldIdx.Value;
                int fieldCount = td.FieldCount;
                for (int f = firstField; f < firstField + fieldCount && f < fieldDefs.Length; f++)
                {
                    var fd = fieldDefs[f];
                    var fieldName = SafeGetString(meta, fd.nameIndex);
                    if (string.IsNullOrEmpty(fieldName)) continue;

                    typeModel.Fields.Add(new FieldModel
                    {
                        Name = fieldName,
                        TypeName = "object",
                        Offset = f * 8,
                        IsPublic = true
                    });
                }

                // Methods
                int firstMethod = (int)td.FirstMethodIdx.Value;
                int methodCount = td.MethodCount;
                for (int m = firstMethod; m < firstMethod + methodCount && m < methodDefs.Length; m++)
                {
                    var md = methodDefs[m];
                    var methodName = SafeGetString(meta, md.nameIndex);
                    if (string.IsNullOrEmpty(methodName)) continue;

                    var methodModel = new MethodModel
                    {
                        Name = methodName,
                        ReturnType = "void",
                        MethodIndex = m,
                        Rva = md.token,
                        MethodPointer = md.token,
                        Slot = md.slot,
                        IsPublic = (md.flags & 0x0006) == 0x0006,
                        IsPrivate = (md.flags & 0x0001) == 0x0001,
                        IsStatic = (md.flags & 0x0010) != 0,
                        IsVirtual = (md.flags & 0x0040) != 0
                    };

                    int firstParam = (int)md.parameterStart.Value;
                    int paramCount = md.parameterCount;
                    for (int p = firstParam; p < firstParam + paramCount && p < paramDefs.Length; p++)
                    {
                        var pd = paramDefs[p];
                        var paramName = SafeGetString(meta, pd.nameIndex);
                        methodModel.Parameters.Add(new ParameterModel
                        {
                            Name = string.IsNullOrEmpty(paramName) ? $"p{p - firstParam}" : paramName,
                            TypeName = "object"
                        });
                    }

                    typeModel.Methods.Add(methodModel);
                }

                imageModel.Types.Add(typeModel);
            }

            dumpContext.Images.Add(imageModel);
        }

        logger?.Invoke($"Metadata-only processing complete: {dumpContext.TotalImages} images, {dumpContext.TotalTypes} types, {dumpContext.TotalMethods} methods, {dumpContext.TotalFields} fields.");
        return dumpContext;
    }
}
