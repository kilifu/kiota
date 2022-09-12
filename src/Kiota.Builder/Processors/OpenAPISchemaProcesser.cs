using System;
using System.Collections.Generic;
using System.Linq;
using Kiota.Builder.Extensions;
using Kiota.Builder.OpenApiExtensions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Kiota.Builder.Processors
{
    public class OpenAPISchemaProcesser
    {
        public static string ProcessOpenAPISchema(string modelName, OpenApiSchema openApiSchema, List<string> enumTypes, List<TSInterface> models)
        {
            if (openApiSchema.Enum != null && openApiSchema.Enum.Any())
            {
                var newEnum = ConstructTSTypeFromEnum(openApiSchema, UtilTS.ModelNameConstruction(modelName));
                enumTypes.Add($"{newEnum.Name} = {newEnum.Value}");
            }
            else
            {
                models.Add(CreateTSInterfaceFromSchema(modelName, openApiSchema));
            }
            return UtilTS.ModelNameConstruction(modelName);
        }

        public static TSEnum ConstructTSTypeFromEnum(OpenApiSchema schema, string enumName)
        {
            var newEnum = new TSEnum();
            newEnum.Name = enumName;
            OpenApiEnumValuesDescriptionExtension extensionInformation = null;
            if (schema.Extensions.TryGetValue(OpenApiEnumValuesDescriptionExtension.Name, out var rawExtension) && rawExtension is OpenApiEnumValuesDescriptionExtension localExtInfo)
                extensionInformation = localExtInfo;
            var entries = schema.Enum.OfType<OpenApiString>().Where(static x => !x.Value.Equals("null", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Value)).Select(static x => x.Value);
            foreach (var enumValue in entries)
            {
                var optionDescription = extensionInformation?.ValuesDescriptions.FirstOrDefault(x => x.Value.Equals(enumValue, StringComparison.OrdinalIgnoreCase));
                var newOption = (optionDescription?.Name ?? enumValue).CleanupSymbolName();
                if (!string.IsNullOrEmpty(newOption))
                {
                    //Console.WriteLine(type+" "+newOption);
                    newEnum.Value = String.IsNullOrWhiteSpace(newEnum.Value) ? $"\"{newOption}\"" : newEnum.Value+ " | " + $"\"{newOption}\"";
                }
            }
            return newEnum;
        }

        public static TSInterface CreateTSInterfaceFromSchema(string modelKey, OpenApiSchema model)
        {
            var parent = "";
            if (model.AllOf != null && model.AllOf.Any())
            {
                parent = UtilTS.GetModelNameFromReference(model.AllOf.First().Reference.Id);

                //parent = model.allOf[0].$ref.replace(prefix + namespacePrefix, "");
                model = model.AllOf.Last();
            }

            var newInter = new TSInterface();
            newInter.Name = UtilTS.ModelNameConstruction(modelKey);
            newInter.Parent = parent;
            foreach (var key in model.Properties)
            {
                var property = key.Key.Contains("@odata") ? $"\"{key.Key}\"" : key.Key;
                var prop = $"{property}?: {returnPropertyType(key.Value, false)}";
                newInter.Properties.Add(prop);
            }

            return newInter;
        }

        private static string returnPropertyType(OpenApiSchema property, bool isArray)
        {
            var arrayPrefix = "";
            if (isArray)
            {
                arrayPrefix = "[]";
            }
            if (string.Equals(property.Type, "string"))
            {
                return "string" + arrayPrefix;
            }
            if (string.Equals(property.Type, "integer"))
            {
                return "number" + arrayPrefix;
            }

            if (string.Equals(property.Type, "object")) {
                var schema = CreateTSInterfaceFromSchema("", property);
                return ConstructRawObject(schema);
            }

            if (property.AnyOf != null && property.AnyOf.Any())
            {                                                                                                           
                var unionString = "";
                foreach (var element in property.AnyOf)
                {
                   
                    if (!string.IsNullOrWhiteSpace(element?.Reference?.Id))
                    {
                        var objectType = UtilTS.GetModelNameFromReference(element.Reference.Id) + arrayPrefix;
                        unionString = !string.IsNullOrWhiteSpace(unionString) ? unionString + " | " + objectType : objectType;
                    }
                    else if (!string.IsNullOrWhiteSpace(element.Type))
                    {
                        var returnType = returnPropertyType(element, isArray);
                        unionString = unionString + (!string.IsNullOrWhiteSpace(unionString) ? " | " + returnType : returnType); // if element type == object -- get element type
                    }
                }
                return unionString;
            }

            if (string.Equals(property.Type, "boolean"))
            {
                return "boolean" + arrayPrefix;
            }

            if (property.Type == "array")
            {
                return returnPropertyType(property.Items, true);

            }
            // Separate method for reference object
            if (!string.IsNullOrWhiteSpace(property?.Reference?.Id))
            {
                return UtilTS.GetModelNameFromReference(property.Reference.Id) + arrayPrefix;
            }

            return "unknown";
        }

        /**
         * Construct TS/JS object fromm a model. 
         * Some cases the return type or request body is of an object instead of a reference to a model.
         */
        public static string ConstructRawObject(TSInterface model)
        {
            string objectBody = "{";
            foreach (var prop in model.Properties)
            {
                objectBody = objectBody + prop + ",";
            }
            objectBody = objectBody + "}";

            return objectBody;
        }
    }
}
