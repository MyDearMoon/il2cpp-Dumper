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
                var voidType = module.TypeSystem.Void;
                var objectType = module.TypeSystem.Object;

                foreach (var typeModel in img.Types)
                {
                    var typeAttrs = TypeAttributes.Class | TypeAttributes.AnsiClass;
                    if (typeModel.IsPublic) typeAttrs |= TypeAttributes.Public;
                    else typeAttrs |= TypeAttributes.NotPublic;

                    if (typeModel.IsInterface) typeAttrs |= TypeAttributes.Interface | TypeAttributes.Abstract;
                    else if (typeModel.IsAbstract) typeAttrs |= TypeAttributes.Abstract;

                    var isSystemObject = typeModel.Namespace == "System" && typeModel.Name == "Object";
                    var baseType = (typeModel.IsInterface || isSystemObject) ? null : objectType;

                    var typeDef = new TypeDefinition(
                        typeModel.Namespace,
                        SanitizeName(typeModel.Name),
                        typeAttrs,
                        baseType);

                    // Add Fields
                    foreach (var field in typeModel.Fields)
                    {
                        var fieldAttrs = FieldAttributes.CompilerControlled;
                        if (field.IsPublic) fieldAttrs |= FieldAttributes.Public;
                        else if (field.IsPrivate) fieldAttrs |= FieldAttributes.Private;
                        else fieldAttrs |= FieldAttributes.Assembly;

                        if (field.IsStatic) fieldAttrs |= FieldAttributes.Static;

                        var fDef = new FieldDefinition(SanitizeName(field.Name), fieldAttrs, objectType);
                        typeDef.Fields.Add(fDef);
                    }

                    // Add Methods
                    foreach (var method in typeModel.Methods)
                    {
                        var methodAttrs = MethodAttributes.HideBySig;
                        if (method.IsPublic) methodAttrs |= MethodAttributes.Public;
                        else if (method.IsPrivate) methodAttrs |= MethodAttributes.Private;
                        else methodAttrs |= MethodAttributes.Assembly;

                        if (method.IsStatic) methodAttrs |= MethodAttributes.Static;
                        if (method.IsVirtual) methodAttrs |= MethodAttributes.Virtual | MethodAttributes.NewSlot;
                        if (method.IsAbstract) methodAttrs |= MethodAttributes.Abstract;

                        var mDef = new MethodDefinition(SanitizeName(method.Name), methodAttrs, voidType);

                        foreach (var param in method.Parameters)
                        {
                            mDef.Parameters.Add(new ParameterDefinition(SanitizeName(param.Name), ParameterAttributes.None, objectType));
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
                    }

                    module.Types.Add(typeDef);
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
        return name.Replace('<', '_').Replace('>', '_').Replace('$', '_').Replace('`', '_').Replace('.', '_');
    }
}
