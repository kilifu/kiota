using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.OpenApi.Models;

namespace Kiota.Builder.Processors
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
            processPaths(openApiDocument.Paths, outputFolder);
            ProcessComponents(openApiDocument.Components);
            writeApis(outputFolder);
            writeComponents(outputFolder);
            writeOperations(outputFolder);
        }

        private void writeComponents(string outputFolder)
        {
            using (var modelWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "models.ts" : "/models.ts")))
            {
                try
                {
                    foreach (var model in models)
                    {
                        modelWriter.WriteLine($"export interface {model.Name}" + (string.IsNullOrWhiteSpace(model.Parent) ? string.Empty : $" extends {model.Parent}") + "{");

                        foreach (var prop in model.Properties)
                        {
                            modelWriter.WriteLine("   " + prop);
                        }

                        modelWriter.WriteLine("}");

                    }

                    foreach (var e in enumTypes)
                    {
                        modelWriter.WriteLine("export type " + e);
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
                        urlWithOperations.Add($"(api:\"{path.Key}\"):operation{count}");
                        var pathItemObject = path.Value;
                        var operation = OpenAPIOperationsProcesser.GetOperationsForPath(pathItemObject.Operations, count++, modelsUsings);
                        operations.Add(operation);
                        operationUsings.Add(operation.Name);
                    }
                }
                catch (Exception connerr) { Console.WriteLine(connerr.Message); };

            }
        }

        private void writeApis(string outputFolder)
        {
            using (var apiWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "apis.ts" : "/apis.ts")))
            {
                try
                {
                    apiWriter.Write("import {");
                    var imp = "";
                    foreach (var u in operationUsings)
                    {
                        imp = imp + ""+ (String.IsNullOrWhiteSpace(imp) ? u : $", {u}");
                        

                    }
                    apiWriter.Write(imp);
                    apiWriter.WriteLine("} from \"./operations\"");
                    apiWriter.WriteLine("export interface Apis {");
                    foreach (var api in urlWithOperations)
                    {
                        apiWriter.WriteLine("   " + api);
                    }
                    apiWriter.WriteLine("}");
                }
                catch (Exception connerr) { Console.WriteLine(connerr.Message); }
            }
        }
        private void writeOperations(string outputFolder)
        {
            using (var opWriter = new StreamWriter(outputFolder + (outputFolder.EndsWith("/") ? "operations.ts" : "/operations.ts")))
            {
                try
                {
                    opWriter.Write("import {");
                    
                    var imp = "";
                    foreach (var u in modelsUsings)
                    {
                        imp = imp + "" + (String.IsNullOrWhiteSpace(imp) ? u : $", {u}");


                    }
                    opWriter.Write(imp);
                    opWriter.WriteLine("} from \"./models\"");

                    foreach (var operation in operations)
                    {
                        opWriter.WriteLine($"export interface {operation.Name}{{");

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

        private void ProcessComponents(OpenApiComponents openApiComponents)
        {
            foreach (var schema in openApiComponents.Schemas)
            {
                OpenAPISchemaProcesser.ProcessOpenAPISchema(schema.Key, schema.Value, enumTypes, models);
            }
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

    public class TSEnum
    {
        public string Name
        {
            get; set;
        }

        public string Value
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

        public TSOperation tSOperation
        {
            get; set;
        }
    }
}
