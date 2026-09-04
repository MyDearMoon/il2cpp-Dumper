using Il2CppDumper.Core.Model;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Il2CppDumper.Core.Exporters;

public sealed class DummyAssemblyExporter : IExporter
{
    public string Name => "Dummy Assemblies (.dll stubs via Mono.Cecil)";

    public void Export(DumpContext context, string outputDirectory, ExportOptions options, Action<string>? logger = null)
    {
        if (!options.ExportDummyDlls) return;

        var dummyDir = Path.Combine(outputDirectory, "DummyDll");
        Directory.CreateDirectory(dummyDir);
        logger?.Invoke($"Exporting dummy assemblies to: {dummyDir}...");

        // Build global type-to-assembly index across ALL images upfront
        // This ensures cross-assembly references (e.g. Assembly-CSharp -> UnityEngine.CoreModule)
        // resolve to the correct AssemblyNameReference rather than falling back to CoreLibrary.
        var globalTypeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var img in context.Images)
        {
            var cleanAsmName = CleanAssemblyName(img.Name);
            foreach (var type in img.Types)
            {
                if (!string.IsNullOrEmpty(type.FullName))
                {
                    globalTypeMap[type.FullName] = cleanAsmName;
                }
            }
        }

        var exportedCount = 0;
        foreach (var img in context.Images)
        {
            try
            {
                var cleanName = CleanAssemblyName(img.Name);

                var assembly = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition(cleanName, new Version(1, 0, 0, 0)),
                    cleanName,
                    ModuleKind.Dll);

                var module = assembly.MainModule;
                var localTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);

                // Pass 1: Declare all type skeletons in this assembly
                var typePairs = new List<(TypeModel Model, TypeDefinition Def)>();
                foreach (var typeModel in img.Types)
                {
                    try
                    {
                        var typeAttrs = TypeAttributes.AnsiClass;
                        if (typeModel.IsPublic) typeAttrs |= TypeAttributes.Public;
                        else typeAttrs |= TypeAttributes.NotPublic;

                        if (typeModel.IsInterface)
                        {
                            typeAttrs |= TypeAttributes.Interface | TypeAttributes.Abstract;
                        }
                        else if (typeModel.IsAbstract)
                        {
                            typeAttrs |= TypeAttributes.Abstract;
                        }
                        else
                        {
                            typeAttrs |= TypeAttributes.Class;
                        }

                        var sanitizedName = SanitizeName(typeModel.Name);
                        var typeDef = new TypeDefinition(typeModel.Namespace, sanitizedName, typeAttrs);

                        typePairs.Add((typeModel, typeDef));
                        localTypes[typeModel.FullName] = typeDef;
                        localTypes[sanitizedName] = typeDef;
                        module.Types.Add(typeDef);
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Warning] Failed to declare type {typeModel.FullName} in {img.Name}: {ex.Message}");
                    }
                }

                var resolver = new CecilTypeResolver(module, localTypes, globalTypeMap, cleanName);

                // Pass 2: Populate base types, interfaces, fields, methods, and properties
                foreach (var (typeModel, typeDef) in typePairs)
                {
                    try
                    {
                        // Base type resolution
                        var isSystemObject = typeModel.Namespace == "System" && typeModel.Name == "Object";
                        if (typeModel.IsInterface || isSystemObject)
                        {
                            typeDef.BaseType = null;
                        }
                        else if (typeModel.IsEnum)
                        {
                            typeDef.BaseType = resolver.Resolve("System.Enum");
                        }
                        else if (typeModel.IsValueType)
                        {
                            typeDef.BaseType = resolver.Resolve("System.ValueType");
                        }
                        else
                        {
                            typeDef.BaseType = resolver.Resolve(typeModel.BaseTypeName ?? "System.Object");
                        }

                        // Interfaces
                        if (typeModel.Interfaces != null)
                        {
                            foreach (var iface in typeModel.Interfaces)
                            {
                                try
                                {
                                    if (!string.IsNullOrWhiteSpace(iface))
                                        typeDef.Interfaces.Add(new InterfaceImplementation(resolver.Resolve(iface)));
                                }
                                catch
                                {
                                    // Ignore interface resolution errors
                                }
                            }
                        }

                        // Enum backing field: CLR enums require an instance field named "value__"
                        if (typeModel.IsEnum && typeModel.Fields?.Any(f => f.Name == "value__") == false)
                        {
                            var underlyingType = resolver.Resolve("System.Int32");
                            var valueField = new FieldDefinition(
                                "value__",
                                FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName,
                                underlyingType);
                            typeDef.Fields.Add(valueField);
                        }

                        // Fields
                        if (typeModel.Fields != null)
                        {
                            foreach (var field in typeModel.Fields)
                            {
                                try
                                {
                                    var fieldAttrs = FieldAttributes.CompilerControlled;
                                    if (field.IsPublic) fieldAttrs |= FieldAttributes.Public;
                                    else if (field.IsPrivate) fieldAttrs |= FieldAttributes.Private;
                                    else fieldAttrs |= FieldAttributes.Assembly;

                                    if (field.IsStatic) fieldAttrs |= FieldAttributes.Static;
                                    if (field.IsConst) fieldAttrs |= FieldAttributes.Literal | FieldAttributes.HasDefault;

                                    var fieldType = resolver.Resolve(field.TypeName);
                                    var fDef = new FieldDefinition(SanitizeName(field.Name), fieldAttrs, fieldType);
                                    typeDef.Fields.Add(fDef);
                                }
                                catch
                                {
                                    var fDef = new FieldDefinition(SanitizeName(field.Name), FieldAttributes.Public, resolver.Resolve("System.Object"));
                                    typeDef.Fields.Add(fDef);
                                }
                            }
                        }

                        // Methods
                        var methodMap = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
                        if (typeModel.Methods != null)
                        {
                            foreach (var method in typeModel.Methods)
                            {
                                try
                                {
                                    var isConstructor = method.Name is ".ctor" or ".cctor";
                                    var methodAttrs = MethodAttributes.HideBySig;

                                    if (method.IsPublic) methodAttrs |= MethodAttributes.Public;
                                    else if (method.IsPrivate) methodAttrs |= MethodAttributes.Private;
                                    else methodAttrs |= MethodAttributes.Assembly;

                                    if (method.IsStatic) methodAttrs |= MethodAttributes.Static;
                                    if (method.IsVirtual) methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                                    if (method.IsAbstract) methodAttrs |= MethodAttributes.Abstract;

                                    // Constructors must preserve their exact name and have SpecialName | RTSpecialName
                                    if (isConstructor)
                                    {
                                        methodAttrs |= MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                                    }

                                    var methodName = isConstructor ? method.Name : SanitizeName(method.Name);
                                    var returnType = isConstructor ? resolver.Resolve("System.Void") : resolver.Resolve(method.ReturnType);
                                    var mDef = new MethodDefinition(methodName, methodAttrs, returnType);

                                    if (method.Parameters != null)
                                    {
                                        foreach (var param in method.Parameters)
                                        {
                                            var paramType = resolver.Resolve(param.TypeName);
                                            mDef.Parameters.Add(new ParameterDefinition(SanitizeName(param.Name), ParameterAttributes.None, paramType));
                                        }
                                    }

                                    // Method body stub: throw null;
                                    // Delegates, runtime methods, and interfaces cannot have an IL body in CLI metadata
                                    var isDelegate = typeModel.BaseTypeName?.Contains("Delegate") == true;
                                    if (!method.IsAbstract && !typeModel.IsInterface && !typeDef.IsInterface && !isDelegate)
                                    {
                                        mDef.Body ??= new MethodBody(mDef);
                                        mDef.Body.InitLocals = true;
                                        var il = mDef.Body.GetILProcessor();
                                        il.Emit(OpCodes.Ldnull);
                                        il.Emit(OpCodes.Throw);
                                    }

                                    typeDef.Methods.Add(mDef);
                                    if (!string.IsNullOrEmpty(method.Name))
                                    {
                                        methodMap[method.Name] = mDef;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    logger?.Invoke($"[Warning] Failed to generate method {method.Name} in {typeModel.FullName}: {ex.Message}");
                                }
                            }
                        }

                        // Properties
                        if (typeModel.Properties != null)
                        {
                            foreach (var prop in typeModel.Properties)
                            {
                                try
                                {
                                    var propType = resolver.Resolve(prop.TypeName);
                                    var pDef = new PropertyDefinition(SanitizeName(prop.Name), PropertyAttributes.None, propType);

                                    if (prop.Getter?.Name != null && methodMap.TryGetValue(prop.Getter.Name, out var getter))
                                    {
                                        pDef.GetMethod = getter;
                                    }

                                    if (prop.Setter?.Name != null && methodMap.TryGetValue(prop.Setter.Name, out var setter))
                                    {
                                        pDef.SetMethod = setter;
                                    }

                                    typeDef.Properties.Add(pDef);
                                }
                                catch
                                {
                                    // Ignore property error
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger?.Invoke($"[Warning] Error populating members for {typeModel.FullName}: {ex.Message}");
                    }
                }

                var outDll = Path.Combine(dummyDir, $"{cleanName}.dll");
                assembly.Write(outDll);
                exportedCount++;
            }
            catch (Exception ex)
            {
                logger?.Invoke($"Warning: Failed to generate dummy assembly for {img.Name}: {ex.Message}");
            }
        }

        logger?.Invoke($"Successfully generated {exportedCount} dummy DLL assemblies in {dummyDir}");
    }

    private static string CleanAssemblyName(string imgName)
    {
        return imgName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? imgName[..^4]
            : imgName;
    }

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_unnamed";

        // Explicitly preserve CLI constructor names
        if (name is ".ctor" or ".cctor")
            return name;

        return name
            .Replace('<', '_')
            .Replace('>', '_')
            .Replace('$', '_')
            .Replace('`', '_')
            .Replace('.', '_')
            .Replace('/', '_')
            .Replace('\\', '_');
    }
}

/// <summary>
/// Resolves type names into valid Mono.Cecil TypeReferences, correctly scoping cross-assembly types.
/// </summary>
internal sealed class CecilTypeResolver
{
    private readonly ModuleDefinition _module;
    private readonly Dictionary<string, TypeDefinition> _localTypes;
    private readonly Dictionary<string, string> _globalTypeMap;
    private readonly string _currentAssemblyName;
    private readonly Dictionary<string, AssemblyNameReference> _asmRefs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TypeReference> _cache = new(StringComparer.Ordinal);
    private readonly IMetadataScope _coreScope;

    public CecilTypeResolver(
        ModuleDefinition module,
        Dictionary<string, TypeDefinition> localTypes,
        Dictionary<string, string> globalTypeMap,
        string currentAssemblyName)
    {
        _module = module;
        _localTypes = localTypes;
        _globalTypeMap = globalTypeMap;
        _currentAssemblyName = currentAssemblyName;
        _coreScope = _module.TypeSystem.CoreLibrary;

        // Populate existing assembly references
        foreach (var r in _module.AssemblyReferences)
        {
            _asmRefs[r.Name] = r;
        }
    }

    public TypeReference Resolve(string? rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
            return ResolvePrimitive("System.Void");

        var typeName = rawTypeName.Trim();

        if (_cache.TryGetValue(typeName, out var cached))
            return cached;

        // Arrays: e.g. "System.Int32[]"
        if (typeName.EndsWith("[]", StringComparison.Ordinal))
        {
            var element = Resolve(typeName[..^2]);
            var arrayRef = new ArrayType(element);
            _cache[typeName] = arrayRef;
            return arrayRef;
        }

        // Pointers: e.g. "void*"
        if (typeName.EndsWith('*'))
        {
            var element = Resolve(typeName[..^1]);
            var ptrRef = new PointerType(element);
            _cache[typeName] = ptrRef;
            return ptrRef;
        }

        // ByRef: e.g. "ref int", "out int", "int&"
        if (typeName.EndsWith('&'))
        {
            var element = Resolve(typeName[..^1]);
            var byRef = new ByReferenceType(element);
            _cache[typeName] = byRef;
            return byRef;
        }
        if (typeName.StartsWith("ref ", StringComparison.OrdinalIgnoreCase) || typeName.StartsWith("out ", StringComparison.OrdinalIgnoreCase))
        {
            var element = Resolve(typeName[4..]);
            var byRef = new ByReferenceType(element);
            _cache[typeName] = byRef;
            return byRef;
        }

        // 1. Normalize C# primitive aliases
        var normalized = NormalizePrimitive(typeName);

        // 2. Check local defined types in this module first
        if (_localTypes.TryGetValue(normalized, out var localTypeDef))
        {
            _cache[typeName] = localTypeDef;
            return localTypeDef;
        }

        // 3. Handle generics: strip generic arguments for base definition lookup
        var cleaned = normalized;
        var bracketIndex = cleaned.IndexOf('<');
        if (bracketIndex >= 0)
        {
            cleaned = cleaned[..bracketIndex];
        }

        if (_localTypes.TryGetValue(cleaned, out var localCleaned))
        {
            _cache[typeName] = localCleaned;
            return localCleaned;
        }

        // 4. Primitive types: safe resolution using _coreScope without calling _module.TypeSystem primitives
        // which throw NullReferenceException on dynamic modules created with CreateAssembly
        if (IsPrimitive(cleaned, out var isValueType))
        {
            var prim = CreatePrimitiveRef(cleaned, isValueType);
            _cache[typeName] = prim;
            return prim;
        }

        var lastDot = cleaned.LastIndexOf('.');
        string ns = lastDot >= 0 ? cleaned[..lastDot] : string.Empty;
        string name = lastDot >= 0 ? cleaned[(lastDot + 1)..] : cleaned;

        // 5. Cross-assembly scoping
        IMetadataScope scope;
        if (_globalTypeMap.TryGetValue(cleaned, out var sourceAssembly) &&
            !string.Equals(sourceAssembly, _currentAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            scope = GetOrCreateAssemblyReference(sourceAssembly);
        }
        else if (ns.StartsWith("System", StringComparison.Ordinal))
        {
            scope = _coreScope;
        }
        else if (ns.StartsWith("UnityEngine", StringComparison.Ordinal))
        {
            scope = GetOrCreateAssemblyReference("UnityEngine.CoreModule");
        }
        else
        {
            var rootNs = ns.Contains('.') ? ns[..ns.IndexOf('.')] : ns;
            scope = string.IsNullOrEmpty(rootNs)
                ? _coreScope
                : GetOrCreateAssemblyReference(rootNs);
        }

        var extRef = new TypeReference(ns, name, _module, scope);
        _cache[typeName] = extRef;
        return extRef;
    }

    private static string NormalizePrimitive(string name) => name switch
    {
        "void" => "System.Void",
        "bool" => "System.Boolean",
        "byte" => "System.Byte",
        "sbyte" => "System.SByte",
        "short" => "System.Int16",
        "ushort" => "System.UInt16",
        "int" => "System.Int32",
        "uint" => "System.UInt32",
        "long" => "System.Int64",
        "ulong" => "System.UInt64",
        "float" => "System.Single",
        "double" => "System.Double",
        "char" => "System.Char",
        "string" => "System.String",
        "object" => "System.Object",
        "IntPtr" => "System.IntPtr",
        "UIntPtr" => "System.UIntPtr",
        _ => name
    };

    private static bool IsPrimitive(string fullName, out bool isValueType)
    {
        switch (fullName)
        {
            case "System.Void":
            case "System.Boolean":
            case "System.Byte":
            case "System.SByte":
            case "System.Int16":
            case "System.UInt16":
            case "System.Int32":
            case "System.UInt32":
            case "System.Int64":
            case "System.UInt64":
            case "System.Single":
            case "System.Double":
            case "System.Char":
            case "System.IntPtr":
            case "System.UIntPtr":
                isValueType = true;
                return true;
            case "System.String":
            case "System.Object":
                isValueType = false;
                return true;
            default:
                isValueType = false;
                return false;
        }
    }

    private TypeReference CreatePrimitiveRef(string fullName, bool isValueType)
    {
        var lastDot = fullName.LastIndexOf('.');
        var ns = lastDot >= 0 ? fullName[..lastDot] : "System";
        var name = lastDot >= 0 ? fullName[(lastDot + 1)..] : fullName;
        return new TypeReference(ns, name, _module, _coreScope) { IsValueType = isValueType };
    }

    private TypeReference ResolvePrimitive(string fullName)
    {
        IsPrimitive(fullName, out var isValueType);
        return CreatePrimitiveRef(fullName, isValueType);
    }

    private AssemblyNameReference GetOrCreateAssemblyReference(string assemblyName)
    {
        if (_asmRefs.TryGetValue(assemblyName, out var existing))
            return existing;

        var newRef = new AssemblyNameReference(assemblyName, new Version(0, 0, 0, 0));
        _module.AssemblyReferences.Add(newRef);
        _asmRefs[assemblyName] = newRef;
        return newRef;
    }
}
