using System.Collections.Generic;
using System.Linq;
using Microsoft.OpenApi.Models;

namespace Kiota.Builder.Processors
{
    public class OpenAPIOperationsProcesser
    {
        public static TSOperation GetOperationsForPath(IDictionary<OperationType, OpenApiOperation> operations, int count, HashSet<string> refListsToImport)
        {
            var op = new TSOperation();

            op.Name = "operation" + count;

            foreach (var operation in operations)
            {
                var operationLine = "";
                operationLine = $"{operation.Key}({ConstructInQueryParamList(operation.Value?.RequestBody, refListsToImport)}):{GetReturnTypeOfOperation(operation.Value.Responses, refListsToImport)}";
                op.operationWithParamString.Add(operationLine);
            }
            return op;
        }

        private static string ConstructInQueryParamList(OpenApiRequestBody requestBody, HashSet<string> refListsToImport)
        {
            var queryParam = "";
            var requestBodySchema = requestBody?.Content?.FirstOrDefault().Value.Schema;

            var reference = requestBodySchema?.Reference?.Id;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                refListsToImport.Add(UtilTS.GetModelNameFromReference(reference));
                return "requestBody:"+UtilTS.GetModelNameFromReference(reference);
            }

            
            if (requestBodySchema != null)
            {
            
                    var schema = OpenAPISchemaProcesser.WriteObject("", requestBodySchema);
                    if (schema != null)
                    {
                        string s = "{";
                        foreach (var p in schema.Properties)
                        {
                            s = s + p + ",";
                        }
                        s = s + "}";


                        return "requestBody:" + s;
                    }
                
            }

            return "";

            //return queryParam;
        }
        private static string GetReturnTypeOfOperation(OpenApiResponses responses, HashSet<string> refListsToImport)
        {
            var successResponse = responses.FirstOrDefault(x => x.Key.StartsWith("2")).Value;

            if (!successResponse.Content.Any()) {
                return $"\"{successResponse.Description}\"";
            }
            var reference = successResponse.Reference?.Id;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                refListsToImport.Add(UtilTS.GetModelNameFromReference(reference));
                return UtilTS.GetModelNameFromReference(reference);
            }

            var mediaType = successResponse.Content.FirstOrDefault().Value;
            if (mediaType != null)
            {
                var refer = mediaType.Schema?.Reference?.Id;
                if (!string.IsNullOrWhiteSpace(refer))
                {
                    refListsToImport.Add(UtilTS.GetModelNameFromReference(refer));
                    return UtilTS.GetModelNameFromReference(refer);
                }
                else {
                    var schema = OpenAPISchemaProcesser.WriteObject("", mediaType.Schema);
                    if (schema != null) {
                        string s = "{";
                        foreach (var p in schema.Properties) {
                            s = s + p + ",";
                        }
                        s = s + "}";


                        return s;
                    }
                }
            }

            return "unknown";
        }
    }

    public class TSOperation
    {
        public string Name
        {
            get; set;
        }

        public List<string> operationWithParamString = new List<string>();

        public string ReturnType
        {
            get; set;
        }

        public string QueryParamString
        {
            get; set;
        }
    }
}

