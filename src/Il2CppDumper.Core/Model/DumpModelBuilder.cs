using System.Reflection;
using Il2CppDumper.Core.Containers;
using LibCpp2IL;
using LibCpp2IL.Metadata;
using LibCpp2IL.Reflection;

namespace Il2CppDumper.Core.Model;

public static class DumpModelBuilder
{
    public static DumpContext Build(
        LibCpp2IlContext cppContext,
        Architecture architecture,
        BinaryFormat format,
        Action<string>? logger = null)
    {
        var model = new DumpContext
        {
            MetadataVersion = cppContext.Metadata.MetadataVersion,
            UnityVersion = cppContext.Metadata.UnityVersion.ToString(),
            Architecture = architecture,
            Format = format
        };

        logger?.Invoke($"Building model for metadata v{model.MetadataVersion} (Unity {model.UnityVersion})...");

        // 1. Process String Literals
        if (cppContext.Metadata.stringLiterals != null)
        {
            for (var i = 0; i < cppContext.Metadata.stringLiterals.Length; i++)
            {
                try
                {
                    var str = cppContext.Metadata.GetStringLiteralFromIndex((uint)i);
                    if (!string.IsNullOrEmpty(str))
                    {
                        model.StringLiterals.Add(str);
                    }
                }
                catch
                {
                    // Ignore malformed string literal
                }
            }
        }

        // 2. Process Assemblies / Images
        var images = cppContext.Metadata.imageDefinitions;
        if (images != null)
        {
            var typeDefIndexCounter = 0;
            foreach (var imgDef in images)
            {
                var imgModel = new ImageModel
                {
                    Name = imgDef.Name ?? "UnknownAssembly"
                };

                var types = imgDef.Types;
                if (types != null)
                {
                    foreach (var typeDef in types)
                    {
                        var typeModel = BuildTypeModel(cppContext, typeDef, imgModel.Name, typeDefIndexCounter++);
                        imgModel.Types.Add(typeModel);

                        foreach (var method in typeModel.Methods)
                        {
                            if (method.Rva != 0 && !model.MethodsByRva.ContainsKey(method.Rva))
                            {
                                model.MethodsByRva[method.Rva] = method;
                            }
                        }
                    }
                }

                model.Images.Add(imgModel);
            }
        }

        logger?.Invoke($"Model ready: {model.TotalImages} images, {model.TotalTypes} types, {model.TotalMethods} methods, {model.TotalFields} fields, {model.TotalStringLiterals} strings.");
        return model;
    }

    private static TypeModel BuildTypeModel(
        LibCpp2IlContext cppContext,
        Il2CppTypeDefinition typeDef,
        string imageName,
        int typeDefIndex)
    {
        var typeModel = new TypeModel
        {
            ImageName = imageName,
            Namespace = typeDef.Namespace ?? string.Empty,
            Name = typeDef.Name ?? $"Type_{typeDefIndex}",
            TypeDefIndex = typeDef.TypeIndex.IsNonNull ? typeDef.TypeIndex.Value : typeDefIndex,
            IsValueType = typeDef.IsValueType,
            IsEnum = typeDef.IsEnumType,
            IsInterface = typeDef.IsInterface,
            IsAbstract = typeDef.IsAbstract,
            IsPublic = typeDef.Attributes.HasFlag(TypeAttributes.Public) || typeDef.Attributes.HasFlag(TypeAttributes.NestedPublic)
        };

        // Base type
        try
        {
            if (typeDef.BaseType != null)
            {
                typeModel.BaseTypeName = typeDef.BaseType.ToString();
            }
        }
        catch
        {
            // Ignore resolution errors
        }

        // Interfaces
        if (typeDef.Interfaces != null)
        {
            foreach (var iface in typeDef.Interfaces)
            {
                try
                {
                    typeModel.Interfaces.Add(iface.ToString());
                }
                catch
                {
                    // Ignore
                }
            }
        }

        // Methods
        if (typeDef.Methods != null)
        {
            for (var mIdx = 0; mIdx < typeDef.Methods.Length; mIdx++)
            {
                var methodDef = typeDef.Methods[mIdx];
                var methodModel = BuildMethodModel(cppContext, methodDef, mIdx);
                typeModel.Methods.Add(methodModel);
            }
        }

        // Fields
        if (typeDef.Fields != null)
        {
            for (var fIdx = 0; fIdx < typeDef.Fields.Length; fIdx++)
            {
                var fieldDef = typeDef.Fields[fIdx];
                var fieldModel = BuildFieldModel(cppContext, typeDef, fieldDef, fIdx);
                typeModel.Fields.Add(fieldModel);
            }
        }

        // Properties
        if (typeDef.Properties != null)
        {
            foreach (var propDef in typeDef.Properties)
            {
                var propModel = new PropertyModel
                {
                    Name = propDef.Name ?? "Property",
                    TypeName = propDef.PropertyType?.ToString() ?? "object"
                };

                if (propDef.Getter != null)
                {
                    propModel.Getter = typeModel.Methods.FirstOrDefault(m => m.Name == propDef.Getter.Name);
                }
                if (propDef.Setter != null)
                {
                    propModel.Setter = typeModel.Methods.FirstOrDefault(m => m.Name == propDef.Setter.Name);
                }

                typeModel.Properties.Add(propModel);
            }
        }

        return typeModel;
    }

    private static MethodModel BuildMethodModel(
        LibCpp2IlContext cppContext,
        Il2CppMethodDefinition methodDef,
        int methodIndex)
    {
        var methodModel = new MethodModel
        {
            Name = methodDef.Name ?? $"Method_{methodIndex}",
            ReturnType = methodDef.ReturnType?.ToString() ?? "void",
            MethodPointer = methodDef.MethodPointer,
            Rva = methodDef.Rva,
            FileOffset = methodDef.MethodOffsetInFile,
            Slot = methodDef.slot,
            MethodIndex = methodIndex,
            IsStatic = methodDef.IsStatic,
            IsPublic = methodDef.Attributes.HasFlag(MethodAttributes.Public),
            IsPrivate = methodDef.Attributes.HasFlag(MethodAttributes.Private),
            IsVirtual = methodDef.Attributes.HasFlag(MethodAttributes.Virtual),
            IsAbstract = methodDef.Attributes.HasFlag(MethodAttributes.Abstract)
        };

        if (methodDef.Parameters != null)
        {
            foreach (var param in methodDef.Parameters)
            {
                methodModel.Parameters.Add(new ParameterModel
                {
                    Name = param.ParameterName ?? "param",
                    TypeName = param.Type?.ToString() ?? "object",
                    DefaultValue = param.DefaultValue?.ToString()
                });
            }
        }

        return methodModel;
    }

    private static FieldModel BuildFieldModel(
        LibCpp2IlContext cppContext,
        Il2CppTypeDefinition typeDef,
        Il2CppFieldDefinition fieldDef,
        int fieldIndexInType)
    {
        var isStatic = false;
        var isConst = false;

        if (typeDef.FieldAttributes != null && fieldIndexInType < typeDef.FieldAttributes.Length)
        {
            var attrs = typeDef.FieldAttributes[fieldIndexInType];
            isStatic = attrs.HasFlag(FieldAttributes.Static);
            isConst = attrs.HasFlag(FieldAttributes.Literal);
        }

        var fieldModel = new FieldModel
        {
            Name = fieldDef.Name ?? $"Field_{fieldIndexInType}",
            TypeName = fieldDef.FieldType?.ToString() ?? "object",
            IsStatic = isStatic,
            IsConst = isConst,
            IsPublic = typeDef.FieldAttributes != null && fieldIndexInType < typeDef.FieldAttributes.Length && typeDef.FieldAttributes[fieldIndexInType].HasFlag(FieldAttributes.Public),
            IsPrivate = typeDef.FieldAttributes != null && fieldIndexInType < typeDef.FieldAttributes.Length && typeDef.FieldAttributes[fieldIndexInType].HasFlag(FieldAttributes.Private)
        };

        try
        {
            fieldModel.Offset = cppContext.Binary.GetFieldOffsetFromIndex(
                typeDef.TypeIndex,
                fieldIndexInType,
                fieldDef.FieldIndex,
                typeDef.IsValueType,
                isStatic);
        }
        catch
        {
            fieldModel.Offset = -1;
        }

        return fieldModel;
    }
}
