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

        var exportedCount = 0;
        foreach (var img in context.Images)
        {
            try
            {
                var cleanName = img.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? img.Name[..^4]
                    : img.Name;

                var assembly = AssemblyDefinition.CreateAssembly(
                    new AssemblyNameDefinition(cleanName, new Version(1, 0, 0, 0)),
                    cleanName,
                    ModuleKind.Dll);

                var module = assembly.MainModule;
                var localTypes = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);

                // Pass 1: Declare all type skeletons so they can be referenced
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

                var resolver = new CecilTypeResolver(module, localTypes);

                // Pass 2: Set base types, interfaces, fields, methods, and properties
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
                        foreach (var iface in typeModel.Interfaces)
                        {
                            try
                            {
                                typeDef.Interfaces.Add(new InterfaceImplementation(resolver.Resolve(iface)));
                            }
                            catch
                            {
                                // Ignore interface resolution errors
                            }
                        }

                        // Fields
                        foreach (var field in typeModel.Fields)
                        {
                            try
                            {
                                var fieldAttrs = FieldAttributes.CompilerControlled;
                                if (field.IsPublic) fieldAttrs |= FieldAttributes.Public;
                                else if (field.IsPrivate) fieldAttrs |= FieldAttributes.Private;
                                else fieldAttrs |= FieldAttributes.Assembly;

                                if (field.IsStatic) fieldAttrs |= FieldAttributes.Static;

                                var fieldType = resolver.Resolve(field.TypeName);
                                var fDef = new FieldDefinition(SanitizeName(field.Name), fieldAttrs, fieldType);
                                typeDef.Fields.Add(fDef);
                            }
                            catch
                            {
                                // Fallback to object if field type resolution fails
                                var fDef = new FieldDefinition(SanitizeName(field.Name), FieldAttributes.Public, module.TypeSystem.Object);
                                typeDef.Fields.Add(fDef);
                            }
                        }

                        // Methods
                        var methodMap = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
                        foreach (var method in typeModel.Methods)
                        {
                            try
                            {
                                var methodAttrs = MethodAttributes.HideBySig;
                                if (method.IsPublic) methodAttrs |= MethodAttributes.Public;
                                else if (method.IsPrivate) methodAttrs |= MethodAttributes.Private;
                                else methodAttrs |= MethodAttributes.Assembly;

                                if (method.IsStatic) methodAttrs |= MethodAttributes.Static;
                                if (method.IsVirtual) methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                                if (method.IsAbstract) methodAttrs |= MethodAttributes.Abstract;

                                var returnType = resolver.Resolve(method.ReturnType);
                                var mDef = new MethodDefinition(SanitizeName(method.Name), methodAttrs, returnType);

                                foreach (var param in method.Parameters)
                                {
                                    var paramType = resolver.Resolve(param.TypeName);
                                    mDef.Parameters.Add(new ParameterDefinition(SanitizeName(param.Name), ParameterAttributes.None, paramType));
                                }

                                // Method body stub: throw null;
                                if (!method.IsAbstract && !typeModel.IsInterface)
                                {
                                    mDef.Body.InitLocals = true;
                                    var il = mDef.Body.GetILProcessor();
                                    il.Emit(OpCodes.Ldnull);
                                    il.Emit(OpCodes.Throw);
                                }

                                typeDef.Methods.Add(mDef);
                                methodMap[method.Name] = mDef;
                            }
                            catch (Exception ex)
                            {
                                logger?.Invoke($"[Warning] Failed to generate method {method.Name} in {typeModel.FullName}: {ex.Message}");
                            }
                        }

                        // Properties
                        foreach (var prop in typeModel.Properties)
                        {
                            try
                            {
                                var propType = resolver.Resolve(prop.TypeName);
                                var pDef = new PropertyDefinition(SanitizeName(prop.Name), PropertyAttributes.None, propType);

                                if (prop.Getter != null && methodMap.TryGetValue(prop.Getter.Name, out var getter))
                                {
                                    pDef.GetMethod = getter;
                                }

                                if (prop.Setter != null && methodMap.TryGetValue(prop.Setter.Name, out var setter))
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

    private static string SanitizeName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "_unnamed";
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
/// Resolves type names into valid Mono.Cecil TypeReferences, handling primitives, arrays, pointers, and external types.
/// </summary>
internal sealed class CecilTypeResolver
{
    private readonly ModuleDefinition _module;
    private readonly Dictionary<string, TypeDefinition> _localTypes;
    private readonly Dictionary<string, TypeReference> _cache = new(StringComparer.Ordinal);

    public CecilTypeResolver(ModuleDefinition module, Dictionary<string, TypeDefinition> localTypes)
    {
        _module = module;
        _localTypes = localTypes;
    }

    public TypeReference Resolve(string? rawTypeName)
    {
        if (string.IsNullOrWhiteSpace(rawTypeName))
            return _module.TypeSystem.Void;

        var typeName = rawTypeName.Trim();

        // Check cache
        if (_cache.TryGetValue(typeName, out var cached))
            return cached;

        // Arrays: e.g. "System.Int32[]" or "Player[]"
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

        // Built-in Primitives
        TypeReference? resolved = typeName switch
        {
            "void" or "System.Void" => _module.TypeSystem.Void,
            "bool" or "System.Boolean" => _module.TypeSystem.Boolean,
            "byte" or "System.Byte" => _module.TypeSystem.Byte,
            "sbyte" or "System.SByte" => _module.TypeSystem.SByte,
            "short" or "System.Int16" => _module.TypeSystem.Int16,
            "ushort" or "System.UInt16" => _module.TypeSystem.UInt16,
            "int" or "System.Int32" => _module.TypeSystem.Int32,
            "uint" or "System.UInt32" => _module.TypeSystem.UInt32,
            "long" or "System.Int64" => _module.TypeSystem.Int64,
            "ulong" or "System.UInt64" => _module.TypeSystem.UInt64,
            "float" or "System.Single" => _module.TypeSystem.Single,
            "double" or "System.Double" => _module.TypeSystem.Double,
            "char" or "System.Char" => _module.TypeSystem.Char,
            "string" or "System.String" => _module.TypeSystem.String,
            "object" or "System.Object" => _module.TypeSystem.Object,
            "IntPtr" or "System.IntPtr" => _module.TypeSystem.IntPtr,
            "UIntPtr" or "System.UIntPtr" => _module.TypeSystem.UIntPtr,
            _ => null
        };

        if (resolved != null)
        {
            _cache[typeName] = resolved;
            return resolved;
        }

        // Check local defined types in this module
        if (_localTypes.TryGetValue(typeName, out var localTypeDef))
        {
            _cache[typeName] = localTypeDef;
            return localTypeDef;
        }

        // Clean generic arguments for reference (e.g. List`1<System.Int32> -> List`1)
        var cleaned = typeName;
        var bracketIndex = cleaned.IndexOf('<');
        if (bracketIndex >= 0)
        {
            cleaned = cleaned[..bracketIndex];
        }

        // Separate Namespace and Name
        var lastDot = cleaned.LastIndexOf('.');
        string ns = lastDot >= 0 ? cleaned[..lastDot] : string.Empty;
        string name = lastDot >= 0 ? cleaned[(lastDot + 1)..] : cleaned;

        // Check again with cleaned name
        if (_localTypes.TryGetValue(cleaned, out var localCleaned))
        {
            _cache[typeName] = localCleaned;
            return localCleaned;
        }

        // Construct TypeReference referencing CoreLibrary for external types
        var extRef = new TypeReference(ns, name, _module, _module.TypeSystem.CoreLibrary);
        _cache[typeName] = extRef;
        return extRef;
    }
}
