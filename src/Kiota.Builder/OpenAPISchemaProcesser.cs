//using System;
//using System.Collections.Generic;
//using System.Linq;
//using Kiota.Builder.Extensions;
//using Kiota.Builder.OpenApiExtensions;
//using Microsoft.OpenApi.Any;
//using Microsoft.OpenApi.Models;

//namespace Kiota.Builder
//{
//    public class OpenAPISchemaProcesser
//    {
//        private CodeTypeBase CreateModelDeclarations(OpenApiSchema schema, OpenApiOperation operation, CodeElement parentElement, OpenApiResponse response)
//        {
//            var codeNamespace = parentElement.GetImmediateParentOfType<CodeNamespace>();

//            if (!schema.IsReferencedSchema() && schema.Properties.Any())
//            { // Inline schema, i.e. specific to the Operation
//                return CreateModelDeclarationAndType(currentNode, schema, operation, codeNamespace, suffixForInlineSchema, typeNameForInlineSchema: typeNameForInlineSchema);
//            }
//            else if (schema.IsAllOf())
//            {
//                return CreateInheritedModelDeclaration(currentNode, schema, operation, codeNamespace);
//            }
//            else if ((schema.IsAnyOf() || schema.IsOneOf()) && string.IsNullOrEmpty(schema.Format))
//            {
//                return CreateComposedModelDeclaration(currentNode, schema, operation, suffixForInlineSchema, codeNamespace);
//            }
//            else if (schema.IsObject() || schema.Properties.Any() || schema.Enum.Any())
//            {
//                // referenced schema, no inheritance or union type
//                return CreateModelDeclarationAndType(currentNode, schema, operation, targetNamespace, response: response, typeNameForInlineSchema: typeNameForInlineSchema);
//            }
//            else if (schema.IsArray())
//            {
//                // collections at root
//                return CreateCollectionModelDeclaration(currentNode, schema, operation, codeNamespace, typeNameForInlineSchema);
//            }
//            else if (!string.IsNullOrEmpty(schema.Type) || !string.IsNullOrEmpty(schema.Format))
//                return GetPrimitiveType(schema, string.Empty);
//            else if (schema.AnyOf.Any() || schema.OneOf.Any() || schema.AllOf.Any()) // we have an empty node because of some local override for schema properties and need to unwrap it.
//                return CreateModelDeclarations(currentNode, schema.AnyOf.FirstOrDefault() ?? schema.OneOf.FirstOrDefault() ?? schema.AllOf.FirstOrDefault(), operation, parentElement, suffixForInlineSchema, response, typeNameForInlineSchema);
//            else throw new Exception("unhandled case, might be object type or array type");
//        }

//        private CodeType CreateModelDeclarationAndType(OpenApiSchema schema, OpenApiOperation operation, CodeNamespace codeNamespace, string classNameSuffix = "", OpenApiResponse response = default, string typeNameForInlineSchema = "")
//        {
//            var className = string.IsNullOrEmpty(typeNameForInlineSchema) ? currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, suffix: classNameSuffix, response: response, schema: schema).CleanupSymbolName() : typeNameForInlineSchema;
//            var codeDeclaration = AddModelDeclarationIfDoesntExist(schema, className, codeNamespace);
//            return new CodeType
//            {
//                TypeDefinition = codeDeclaration,
//                Name = className,
//            };
//        }

//        private CodeTypeBase CreateInheritedModelDeclaration(OpenApiSchema schema, OpenApiOperation operation)
//        {
//            var allOfs = schema.AllOf.FlattenEmptyEntries(x => x.AllOf);
//            CodeElement codeDeclaration = null;
//            var className = string.Empty;
//            foreach (var currentSchema in allOfs)
//            {
//                var referenceId = GetReferenceIdFromOriginalSchema(currentSchema, schema);
//                var shortestNamespaceName = GetModelsNamespaceNameFromReferenceId(referenceId);
//                var shortestNamespace = string.IsNullOrEmpty(referenceId) ? codeNamespaceFromParent : rootNamespace.FindNamespaceByName(shortestNamespaceName);
//                if (shortestNamespace == null)
//                    shortestNamespace = rootNamespace.AddNamespace(shortestNamespaceName);
//                className = (currentSchema.GetSchemaName() ?? currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, schema: schema)).CleanupSymbolName();
//                codeDeclaration = AddModelDeclarationIfDoesntExist(currentNode, currentSchema, className, shortestNamespace, codeDeclaration as CodeClass);
//            }

//            return new CodeType
//            {
//                TypeDefinition = codeDeclaration,
//                Name = className,
//            };
//        }


//        private CodeTypeBase CreateComposedModelDeclaration(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, string suffixForInlineSchema, CodeNamespace codeNamespace)
//        {
//            var typeName = currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, suffix: suffixForInlineSchema, schema: schema).CleanupSymbolName();
//            var typesCount = schema.AnyOf?.Count ?? schema.OneOf?.Count ?? 0;
//            if ((typesCount == 1 && schema.Nullable && schema.IsAnyOf()) || // nullable on the root schema outside of anyOf
//                typesCount == 2 && schema.AnyOf.Any(static x => // nullable on a schema in the anyOf
//                                                            x.Nullable &&
//                                                            !x.Properties.Any() &&
//                                                            !x.IsOneOf() &&
//                                                            !x.IsAnyOf() &&
//                                                            !x.IsAllOf() &&
//                                                            !x.IsArray() &&
//                                                            !x.IsReferencedSchema()))
//            { // once openAPI 3.1 is supported, there will be a third case oneOf with Ref and type null.
//                var targetSchema = schema.AnyOf.First(static x => !string.IsNullOrEmpty(x.GetSchemaName()));
//                var className = targetSchema.GetSchemaName().CleanupSymbolName();
//                var shortestNamespace = GetShortestNamespace(codeNamespace, targetSchema);
//                return new CodeType
//                {
//                    TypeDefinition = AddModelDeclarationIfDoesntExist(currentNode, targetSchema, className, shortestNamespace),
//                    Name = className,
//                };// so we don't create unnecessary union types when anyOf was used only for nullable.
//            }
//            var (unionType, schemas) = (schema.IsOneOf(), schema.IsAnyOf()) switch
//            {
//                (true, false) => (new CodeExclusionType
//                {
//                    Name = typeName,
//                } as CodeComposedTypeBase, schema.OneOf),
//                (false, true) => (new CodeUnionType
//                {
//                    Name = typeName,
//                }, schema.AnyOf),
//                (_, _) => throw new InvalidOperationException("Schema is not oneOf nor anyOf"),
//            };
//            var membersWithNoName = 0;
//            foreach (var currentSchema in schemas)
//            {
//                var shortestNamespace = GetShortestNamespace(codeNamespace, currentSchema);
//                var className = currentSchema.GetSchemaName().CleanupSymbolName();
//                if (string.IsNullOrEmpty(className))
//                    if (GetPrimitiveType(currentSchema) is CodeType primitiveType && !string.IsNullOrEmpty(primitiveType.Name))
//                    {
//                        unionType.AddType(primitiveType);
//                        continue;
//                    }
//                    else
//                        className = $"{unionType.Name}Member{++membersWithNoName}";
//                var codeDeclaration = AddModelDeclarationIfDoesntExist(currentNode, currentSchema, className, shortestNamespace);
//                unionType.AddType(new CodeType
//                {
//                    TypeDefinition = codeDeclaration,
//                    Name = className,
//                });
//            }
//            return unionType;
//        }

//        private CodeTypeBase CreateCollectionModelDeclaration(OpenApiSchema schema, OpenApiOperation operation, string typeNameForInlineSchema = default)
//        {
//            CodeTypeBase type = GetPrimitiveType(schema?.Items, string.Empty);
//            if (type == null || string.IsNullOrEmpty(type.Name))
//            {
//                type = CreateModelDeclarations(schema?.Items, operation, default, typeNameForInlineSchema: typeNameForInlineSchema);
//            }
//            type.CollectionKind = CodeTypeBase.CodeTypeCollectionKind.Complex;
//            return type;
//        }

//        private static CodeType GetPrimitiveType(OpenApiSchema typeSchema, string childType = default)
//        {
//            var typeNames = new List<string> { typeSchema?.Items?.Type, childType, typeSchema?.Type };
//            if (typeSchema?.AnyOf?.Any() ?? false)
//                typeNames.AddRange(typeSchema.AnyOf.Select(x => x.Type)); // double is sometimes an anyof string, number and enum
//                                                                          // first value that's not null, and not "object" for primitive collections, the items type matters
//            var typeName = typeNames.FirstOrDefault(static x => !string.IsNullOrEmpty(x) && !typeNamesToSkip.Contains(x));

//            var isExternal = false;
//            if (typeSchema?.Items?.Enum?.Any() ?? false)
//                typeName = childType;
//            else
//            {
//                var format = typeSchema?.Format ?? typeSchema?.Items?.Format;
//                var primitiveTypeName = (typeName?.ToLowerInvariant(), format?.ToLowerInvariant()) switch
//                {
//                    ("string", "base64url") => "binary",
//                    ("file", _) => "binary",
//                    ("string", "duration") => "TimeSpan",
//                    ("string", "time") => "TimeOnly",
//                    ("string", "date") => "DateOnly",
//                    ("string", "date-time") => "DateTimeOffset",
//                    ("string", _) => "string", // covers commonmark and html
//                    ("number", "double" or "float" or "decimal") => format.ToLowerInvariant(),
//                    ("number" or "integer", "int8") => "sbyte",
//                    ("number" or "integer", "uint8") => "byte",
//                    ("number" or "integer", "int64") => "int64",
//                    ("number", "int32") => "integer",
//                    ("number", _) => "int64",
//                    ("integer", _) => "integer",
//                    ("boolean", _) => "boolean",
//                    (_, "byte" or "binary") => "binary",
//                    (_, _) => string.Empty,
//                };
//                if (primitiveTypeName != string.Empty)
//                {
//                    typeName = primitiveTypeName;
//                    isExternal = true;
//                }
//            }
//            return new CodeType
//            {
//                Name = typeName,
//                IsExternal = isExternal,
//            };
//        }

//        private CodeElement AddModelDeclarationIfDoesntExist(OpenApiSchema schema, string declarationName, CodeClass inheritsFrom = null)
//        {
//            var existingDeclaration = GetExistingDeclaration(currentNamespace, currentNode, declarationName);
//            if (existingDeclaration == null) // we can find it in the components
//            {
//                if (schema.Enum.Any())
//                {
//                    var newEnum = new CodeEnum
//                    {
//                        Name = declarationName,//TODO set the flag property
//                        Description = currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel),
//                    };
//                    SetEnumOptions(schema, newEnum);
//                    return currentNamespace.AddEnum(newEnum).First();
//                }
//                else
//                    return AddModelClass(currentNode, schema, declarationName, currentNamespace, inheritsFrom);
//            }
//            else
//                return existingDeclaration;
//        }

//        private static void SetEnumOptions(OpenApiSchema schema, CodeEnum target)
//        {
//            OpenApiEnumValuesDescriptionExtension extensionInformation = null;
//            if (schema.Extensions.TryGetValue(OpenApiEnumValuesDescriptionExtension.Name, out var rawExtension) && rawExtension is OpenApiEnumValuesDescriptionExtension localExtInfo)
//                extensionInformation = localExtInfo;
//            var entries = schema.Enum.OfType<OpenApiString>().Where(static x => !x.Value.Equals("null", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Value)).Select(static x => x.Value);
//            foreach (var enumValue in entries)
//            {
//                var optionDescription = extensionInformation?.ValuesDescriptions.FirstOrDefault(x => x.Value.Equals(enumValue, StringComparison.OrdinalIgnoreCase));
//                var newOption = new CodeEnumOption
//                {
//                    Name = (optionDescription?.Name ?? enumValue).CleanupSymbolName(),
//                    SerializationName = !string.IsNullOrEmpty(optionDescription?.Name) ? enumValue : null,
//                    Description = optionDescription?.Description,
//                };
//                if (!string.IsNullOrEmpty(newOption.Name))
//                    target.AddOption(newOption);
//            }
//        }



//        private TSInterface AddModelClass(OpenApiSchema schema)
//        {
//            var newModelInterface = new TSInterface();
         
//            var referencedAllOfs = schema.AllOf.Where(x => x.Reference != null);
//            if (referencedAllOfs.Any())
//            {// any non-reference would be the current class in some description styles
//                var parentSchema = referencedAllOfs.FirstOrDefault();
//                if (parentSchema != null)
//                {
//                    newModelInterface.Parent = UtilTS.GetModelNameFromReference(parentSchema.Reference.Id);
//                }
//            }
//            CreatePropertiesForModelClass(schema, newModelInterface);
//            return newModelInterface;
//        }

//        private void CreatePropertiesForModelClass(OpenApiSchema schema, CodeNamespace ns, CodeClass model)
//        {

//            if (schema?.Properties?.Any() ?? false)
//            {
//                model.AddProperty(schema
//                                    .Properties
//                                    .Select(x => {
//                                        var propertySchema = x.Value;
//                                        var className = UtilTS.GetModelNameFromReference(propertySchema.GetSchemaName());
//                                        if (string.IsNullOrEmpty(className))
//                                            className = $"{model.Name}_{x.Key}";
                                        
//                                    try {
//                                        return CreateProperty(x.Key, typeSchema: propertySchema);
//#if RELEASE
//                                    } catch (InvalidSchemaException ex) {
//                                        throw new InvalidOperationException($"Error creating property {x.Key} for model {model.Name} in API path {currentNode.Path}, the schema is invalid.", ex);
//                                    }
//#endif
//                                    })
//                                    .ToArray());
//            }
//            else if (schema?.AllOf?.Any(x => x.IsObject()) ?? false)
//                CreatePropertiesForModelClass(schema.AllOf.Last(x => x.IsObject()), ns, model);
//        }

//        private CodeProperty CreateProperty(string childIdentifier, OpenApiSchema typeSchema = null)
//        {
//            var propertyName = childIdentifier.CleanupSymbolName();
//            var prop = new CodeProperty
//            {
//                Name = propertyName,
//                Kind = kind,
//                Description = typeSchema?.Description.CleanupDescription() ?? $"The {propertyName} property",
//            };
//            if (propertyName != childIdentifier)
//                prop.SerializationName = childIdentifier;
//            if (kind == CodePropertyKind.Custom &&
//                typeSchema?.Default is OpenApiString stringDefaultValue &&
//                !string.IsNullOrEmpty(stringDefaultValue.Value))
//                prop.DefaultValue = $"\"{stringDefaultValue.Value}\"";

//            if (existingType != null)
//                prop.Type = existingType;
//            else
//            {
//                prop.Type = GetPrimitiveType(typeSchema, childType);
//                prop.Type.CollectionKind = typeSchema.IsArray() ? CodeType.CodeTypeCollectionKind.Complex : default;
//                logger.LogTrace("Creating property {name} of {type}", prop.Name, prop.Type.Name);
//            }
//            return prop;
//        }

//    }
//}

