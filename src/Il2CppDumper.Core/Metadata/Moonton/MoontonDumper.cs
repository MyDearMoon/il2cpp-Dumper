using Il2CppDumper.Core.Containers;
using Il2CppDumper.Core.Model;

namespace Il2CppDumper.Core.Metadata.Moonton;

public static class MoontonDumper
{
    public static bool IsMoontonMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath)) return false;
        try
        {
            using var fs = File.OpenRead(metadataPath);
            if (fs.Length < 8) return false;
            var buffer = new byte[8];
            if (fs.Read(buffer, 0, 8) < 8) return false;
            var magic = BitConverter.ToUInt32(buffer, 0);
            var version = BitConverter.ToInt32(buffer, 4);
            return magic == 0xFAB11BAF && version == 1024;
        }
        catch
        {
            return false;
        }
    }

    public static DumpContext Dump(string metadataPath, string? binaryPath, Action<string>? logger = null)
    {
        logger?.Invoke("Detected Moonton / MLBB partitioned IL2CPP metadata (Version 1024 / HybridCLR)!");
        logger?.Invoke("Scanning directory for all partition modules...");

        var metaContext = MoontonMetadataContext.LoadFromDirectoryOrFile(metadataPath, logger);

        if (metaContext.Partitions.Count == 0)
        {
            throw new InvalidOperationException("Failed to load any valid Moonton metadata partitions.");
        }

        var dumpContext = new DumpContext
        {
            MetadataVersion = 24.3f,
            UnityVersion = "2019.4.33f1 (Moonton / MLBB HybridCLR)",
            Architecture = Architecture.Arm64,
            Format = BinaryFormat.Elf
        };

        // Populate string literals from all partitions
        dumpContext.StringLiterals.AddRange(metaContext.GetAllStringLiterals());
        logger?.Invoke($"Loaded {dumpContext.StringLiterals.Count} string literals across partitions.");

        // Process partitions: Order Partition 3 (Game C#), then 2 (FirstPass), then 1 (Base Engine)
        var orderedPartitions = metaContext.Partitions.Values
            .OrderByDescending(p => p.PartitionId)
            .ToList();

        foreach (var part in orderedPartitions)
        {
            var imageName = part.PartitionId switch
            {
                3 => "Assembly-CSharp.dll",
                2 => "Assembly-CSharp-firstpass.dll",
                1 => "UnityEngine.CoreModule.dll",
                _ => $"Partition_{part.PartitionId}.dll"
            };

            var image = new ImageModel { Name = imageName };
            logger?.Invoke($"Decompiling {imageName} ({part.TypeCount} types, {part.MethodCount} methods)...");

            var typeCount = part.TypeCount;
            for (int t = 0; t < typeCount; t++)
            {
                var tBase = part.TypeDefsOffset + t * 92;
                if (tBase + 92 > part.Data.Length) break;

                var nameIndex = BitConverter.ToInt32(part.Data, tBase);
                var nsIndex = BitConverter.ToInt32(part.Data, tBase + 4);

                var typeName = metaContext.ResolveString(nameIndex, part.PartitionId);
                var ns = metaContext.ResolveString(nsIndex, part.PartitionId);

                if (string.IsNullOrEmpty(typeName)) continue;

                var flags = BitConverter.ToUInt32(part.Data, tBase + 32);
                var firstFieldIdx = BitConverter.ToInt32(part.Data, tBase + 36) & 0x00FFFFFF;
                var firstMethodIdx = BitConverter.ToInt32(part.Data, tBase + 40) & 0x00FFFFFF;
                var methodCount = BitConverter.ToUInt16(part.Data, tBase + 68);
                var fieldCount = BitConverter.ToUInt16(part.Data, tBase + 72);

                var typeModel = new TypeModel
                {
                    ImageName = imageName,
                    Namespace = ns,
                    Name = typeName,
                    TypeDefIndex = t,
                    IsPublic = (flags & 0x00000001) != 0 || (flags & 0x00000007) == 0x00000001,
                    IsInterface = (flags & 0x00000020) != 0,
                    IsAbstract = (flags & 0x00000080) != 0
                };

                // Read Fields (12 bytes each)
                for (int f = 0; f < fieldCount; f++)
                {
                    var fBase = part.FieldsOffset + (firstFieldIdx + f) * 12;
                    if (fBase + 12 > part.Data.Length) break;

                    var fNameToken = BitConverter.ToInt32(part.Data, fBase);
                    var fName = metaContext.ResolveString(fNameToken, part.PartitionId);

                    if (!string.IsNullOrEmpty(fName))
                    {
                        typeModel.Fields.Add(new FieldModel
                        {
                            Name = fName,
                            TypeName = "object",
                            Offset = f * 8,
                            IsPublic = true
                        });
                    }
                }

                // Read Methods (32 bytes each)
                for (int m = 0; m < methodCount; m++)
                {
                    var mBase = part.MethodsOffset + (firstMethodIdx + m) * 32;
                    if (mBase + 32 > part.Data.Length) break;

                    var mNameToken = BitConverter.ToInt32(part.Data, mBase);
                    var paramStart = BitConverter.ToInt32(part.Data, mBase + 12) & 0x00FFFFFF;
                    var mToken = BitConverter.ToUInt32(part.Data, mBase + 20);
                    var mFlags = BitConverter.ToUInt16(part.Data, mBase + 24);
                    var slot = BitConverter.ToUInt16(part.Data, mBase + 28);
                    var paramCount = BitConverter.ToUInt16(part.Data, mBase + 30);

                    var mName = metaContext.ResolveString(mNameToken, part.PartitionId);
                    if (string.IsNullOrEmpty(mName)) continue;

                    var methodModel = new MethodModel
                    {
                        Name = mName,
                        ReturnType = "void",
                        MethodIndex = m,
                        Rva = mToken,
                        MethodPointer = mToken,
                        Slot = slot != 0xFFFF ? slot : -1,
                        IsPublic = (mFlags & 0x0006) == 0x0006,
                        IsPrivate = (mFlags & 0x0001) == 0x0001,
                        IsStatic = (mFlags & 0x0010) != 0,
                        IsVirtual = (mFlags & 0x0040) != 0
                    };

                    // Read Parameters (12 bytes each)
                    for (int p = 0; p < paramCount; p++)
                    {
                        var pBase = part.ParametersOffset + (paramStart + p) * 12;
                        if (pBase + 12 > part.Data.Length) break;

                        var pNameToken = BitConverter.ToInt32(part.Data, pBase);
                        var pName = metaContext.ResolveString(pNameToken, part.PartitionId);
                        methodModel.Parameters.Add(new ParameterModel
                        {
                            Name = string.IsNullOrEmpty(pName) ? $"arg{p}" : pName,
                            TypeName = "object"
                        });
                    }

                    typeModel.Methods.Add(methodModel);
                }

                image.Types.Add(typeModel);
            }

            dumpContext.Images.Add(image);
        }

        logger?.Invoke($"Moonton metadata processing complete: {dumpContext.TotalImages} images, {dumpContext.TotalTypes} types, {dumpContext.TotalMethods} methods, {dumpContext.TotalFields} fields.");
        return dumpContext;
    }
}
