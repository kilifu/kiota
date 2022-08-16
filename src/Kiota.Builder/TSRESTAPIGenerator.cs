using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Kiota.Builder.Extensions;
using Kiota.Builder.OpenApiExtensions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Services;

namespace Kiota.Builder
{
    public class TSRESTAPIGenerator
    {
        //private StreamWriter apiWriter;
        //private StreamWriter operationWriter;
        //private StreamWriter modelWriter;

        private List<TSInterface> models = new List<TSInterface>();
        private List<TSOperation> operations = new List<TSOperation>();
        private List<string> urlWithOperations = new List<string>();
        private List<string> enumTypes = new List<string>();

        private HashSet<string> modelsUsings = new HashSet<string>();
        private HashSet<string> operationUsings = new HashSet<string>();

        public void generate(string outputFolder, OpenApiDocument openApiDocument)
        {
            //using var stream = new FileStream(outputFolder + (outputFolder.EndsWith("/") ? "apis.ts" : "/apis.ts"), FileMode.Create);

            //this.apiWriter = new StreamWriter(stream);

            //using var operationStream = new FileStream(outputFolder + (outputFolder.EndsWith("/") ? "operations.ts" : "/operations.ts"), FileMode.Create);

            //this.operationWriter = new StreamWriter(operationStream);


           

            processPaths(openApiDocument.Paths,outputFolder);
            ProcessComponents(openApiDocument.Components);
            writeComponents(outputFolder);
            writeOperations(outputFolder);
        }

        private void writeComponents(string outputFolder)
        {
           
            using (var modelWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "models.ts" : "/models.ts"))) {

                try
                {
                    foreach (var model in models)
                    {
                        modelWriter.WriteLine($"export  interface {model.Name}" + (string.IsNullOrWhiteSpace(model.Parent) ? string.Empty : $" extends {model.Parent}") + "{");

                        foreach (var prop in model.Properties)
                        {
                            modelWriter.WriteLine("   " + prop);
                        }

                        modelWriter.WriteLine("}");

                    }

                    foreach (var e in enumTypes) {
                        modelWriter.WriteLine("export type "+e);
                    }
                }
                catch (Exception connerr) { Console.WriteLine(connerr.Message); };

            }
        
        }

        private void processPaths(OpenApiPaths openApiPaths, string outputFolder)
        {
            using (var opWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "apis.ts" : "/apis.ts")))
            {

                try
                {
                    var count = 1;
                    foreach (var path in openApiPaths)
                    {
                        var pathAutoComplete = $"(api:'{path.Key}'):operation{count}";
                        var pathItemObject = path.Value;
                        var operation = OpenAPIOpertaionsProcesser.GetOperationsForPath(pathItemObject.Operations, count++, modelsUsings);
                        operations.Add(operation);
                    }
                }
                catch (Exception connerr) { Console.WriteLine(connerr.Message); };

            }

            
        }

        private void writeOperations(string outputFolder)
        {
            using (var opWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "operations.ts" : "/operations.ts")))
            {

                try
                {
                    foreach (var operation in operations)
                    {
                        opWriter.WriteLine($"export  interface {operation.Name}{{");

                        foreach (var prop in operation.operationWithParamString)
                        {
                            opWriter.WriteLine("   " + prop);
                        }

                        opWriter.WriteLine("}");

                    }
                }
                catch (Exception connerr) { Console.WriteLine(connerr.Message); };

            }

        }

        //private void GetOperationsForPath(IDictionary<OperationType, OpenApiOperation> operations, int count)
        //{
        //    var s = operations.Where(k => k.Key == OperationType.Get);
        //    operationWriter.WriteLine($"export interface operation{count}{{");
        //    foreach (var operation in operations)
        //    {
        //        operationWriter.WriteLine($"{operation.Key.GetType()}:(operation.Value.Responses)");
        //    }
        //    operationWriter.WriteLine("}");
        //}


        /**
         * Components , responses and models
         */
        private void ProcessComponents(OpenApiComponents openApiComponents)
        {
            foreach (var schema in openApiComponents.Schemas)
            {
                processOpenAPISchema(schema.Key, schema.Value);
            }
        }

        private string processOpenAPISchema(string modelName, OpenApiSchema openApiSchema)
        {
            if (openApiSchema.Enum != null && openApiSchema.Enum.Any())
            {
                var type = UtilTS.ModelNameConstruction(modelName) + " =";


                SetEnumOptions(openApiSchema, type);


            }
            else
            {
                writeModel(modelName, openApiSchema);
            }
            return UtilTS.ModelNameConstruction(modelName);
        }

        private void SetEnumOptions(OpenApiSchema schema, string type)
        {
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
                    type = type + (type.EndsWith("=") ? $"\"{ newOption}\"" : " | " + $"\"{newOption}\"");
                }

            }

            enumTypes.Add(type);
        }

        private void writeModel(string modelKey, OpenApiSchema model)
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
            //const modelName = ModelNaming(`microsoft.graph.${namespacePrefix}`);
            models.Add(newInter);
            foreach (var key in model.Properties)
            {
                var property = key.Key.Contains("@odata") ? $"`{key}`" : key.Key;
                var prop = $"{property}?: {returnPropertyType(key.Value, false)}";
                newInter.Properties.Add(prop);
            }
        }

        private string returnPropertyType(OpenApiSchema property, bool isArray)
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
            if (string.Equals(property.Type,  "integer"))
            {
                return "number" + arrayPrefix;
            }

            if (property.AnyOf != null && property.AnyOf.Any())
            {
                var arr = property.AnyOf;
                var unionString = "";
                foreach (var element in property.AnyOf)
                {
                    if (!String.IsNullOrWhiteSpace(element.Type))
                    {
                        unionString = !String.IsNullOrWhiteSpace(unionString) ? " | " + element.Type + arrayPrefix : element.Type + arrayPrefix;
                    }
                    if (!String.IsNullOrWhiteSpace(element?.Reference?.Id))
                    {
                        var objectType = UtilTS.GetModelNameFromReference(element.Reference.Id) + arrayPrefix;
                        unionString = !String.IsNullOrWhiteSpace(unionString) ? " | " + objectType : objectType;
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
            if (!String.IsNullOrWhiteSpace(property?.Reference?.Id))
            {
                return UtilTS.GetModelNameFromReference(property.Reference.Id) + arrayPrefix;
            }

            return "unknown";
        }
    }

    public class TSInterface
    {
        public string Name
        {
            get; set;
        }

        public List<string> Properties
        {
            get; set;
        } = new List<string>();

        public string Parent
        {
            get; set;
        }
    }

    public class TSPathOperation
    {
        public string Path
        {
            get; set;
        }

        public TSOperation tSOperation { get; set; }
    }
}
