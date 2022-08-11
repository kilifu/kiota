using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.OpenApi.Models;

namespace Kiota.Builder
{
    public class TSRESTAPIGenerator
    {
        private StreamWriter apiWriter;
        private StreamWriter operationWriter;
        private StreamWriter modelWriter;


        public void generate(string outputFolder, OpenApiDocument openApiDocument)
        {
            using var stream = new FileStream(outputFolder + (outputFolder.EndsWith("/") ? "apis.ts" : "/apis.ts"), FileMode.Create);

            this.apiWriter = new StreamWriter(stream);

            using var operationStream = new FileStream(outputFolder + (outputFolder.EndsWith("/") ? "operations.ts" : "/operations.ts"), FileMode.Create);

            this.operationWriter = new StreamWriter(operationStream);


            processPaths(openApiDocument.Paths);

        }
        private void WritePaths(string outputFolder)
        {
            
          
        }


        private void processPaths(OpenApiPaths openApiPaths)
        {
            var count = 1;
            foreach (var path in openApiPaths) {

                apiWriter.WriteLine($"(api:'{path.Key})'");
                var sd = path.Value;
                writeOperationsForPath(sd.Operations, count++);
            }
        }

        private void writeOperationsForPath(IDictionary<OperationType, OpenApiOperation> operations,int count)
        {
            var s = operations.Where(k => k.Key == OperationType.Get);
            operationWriter.WriteLine($"export interface operation{count}{{");
            foreach (var operation in operations) {
                if (operation.Key == OperationType.Get) {
                    operationWriter.WriteLine("get:unknown");
                }
            }
            operationWriter.WriteLine("}");
        }
    }
}
