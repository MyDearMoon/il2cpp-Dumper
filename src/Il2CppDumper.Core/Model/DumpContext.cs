using Il2CppDumper.Core.Containers;

namespace Il2CppDumper.Core.Model;

public sealed class DumpContext
{
    public float MetadataVersion { get; set; }
    public string UnityVersion { get; set; } = string.Empty;
    public Architecture Architecture { get; set; } = Architecture.Unknown;
    public BinaryFormat Format { get; set; } = BinaryFormat.Unknown;
    public List<ImageModel> Images { get; set; } = new();
    public List<string> StringLiterals { get; set; } = new();
    public Dictionary<ulong, MethodModel> MethodsByRva { get; set; } = new();

    public int TotalImages => Images.Count;
    public int TotalTypes => Images.Sum(i => i.Types.Count);
    public int TotalMethods => Images.Sum(i => i.Types.Sum(t => t.Methods.Count));
    public int TotalFields => Images.Sum(i => i.Types.Sum(t => t.Fields.Count));
    public int TotalStringLiterals => StringLiterals.Count;
}

public sealed class ImageModel
{
    public string Name { get; set; } = string.Empty;
    public List<TypeModel> Types { get; set; } = new();

    public override string ToString() => $"{Name} ({Types.Count} types)";
}

public sealed class TypeModel
{
    public string ImageName { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string FullName => string.IsNullOrEmpty(Namespace) ? Name : $"{Namespace}.{Name}";
    public string? BaseTypeName { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public int TypeDefIndex { get; set; }

    public bool IsValueType { get; set; }
    public bool IsEnum { get; set; }
    public bool IsInterface { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsPublic { get; set; }

    public List<FieldModel> Fields { get; set; } = new();
    public List<MethodModel> Methods { get; set; } = new();
    public List<PropertyModel> Properties { get; set; } = new();

    public override string ToString() => FullName;
}

public sealed class MethodModel
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = "void";
    public List<ParameterModel> Parameters { get; set; } = new();
    public bool IsStatic { get; set; }
    public bool IsPublic { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsVirtual { get; set; }
    public bool IsAbstract { get; set; }

    public ulong MethodPointer { get; set; }
    public ulong Rva { get; set; }
    public long FileOffset { get; set; }
    public int Slot { get; set; }
    public int MethodIndex { get; set; }

    public string Signature =>
        $"{ReturnType} {Name}({string.Join(", ", Parameters.Select(p => $"{p.TypeName} {p.Name}"))})";

    public override string ToString() => Signature;
}

public sealed class ParameterModel
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
}

public sealed class FieldModel
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int Offset { get; set; }
    public bool IsStatic { get; set; }
    public bool IsConst { get; set; }
    public bool IsPublic { get; set; }
    public bool IsPrivate { get; set; }
    public string? DefaultValue { get; set; }

    public override string ToString() => $"{TypeName} {Name} // Offset: 0x{Offset:X}";
}

public sealed class PropertyModel
{
    public string Name { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public MethodModel? Getter { get; set; }
    public MethodModel? Setter { get; set; }
}
