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
                var requestBodyPart = operation.Value?.RequestBody != null ? ConstructInQueryParamList(operation.Value?.RequestBody, refListsToImport) : string.Empty;
                /**   Example - - - -> post(requestBody: bodyType): returnType[] ***/
                operationLine = $"{operation.Key}({requestBodyPart}):{GetReturnTypeOfOperation(operation.Value.Responses, refListsToImport)}";
                op.operationWithParamString.Add(operationLine);
            }
            return op;
        }

        private static string ConstructInQueryParamList(OpenApiRequestBody requestBody, HashSet<string> refListsToImport)
        {
            var requestBodySchema = requestBody?.Content?.FirstOrDefault().Value.Schema;

            var reference = requestBodySchema?.Reference?.Id;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                refListsToImport.Add(UtilTS.GetModelNameFromReference(reference));
                return "requestBody:" + UtilTS.GetModelNameFromReference(reference);
            }

            if (requestBodySchema != null)
            {
                var schema = OpenAPISchemaProcesser.CreateTSInterfaceFromSchema("", requestBodySchema, refListsToImport);
                if (schema != null)
                {
                    return "requestBody:" + OpenAPISchemaProcesser.ConstructRawObject(schema);
                }
            }

            return "";
        }

        private static string GetReturnTypeOfOperation(OpenApiResponses responses, HashSet<string> refListsToImport)
        {
            var successResponse = responses.FirstOrDefault(x => x.Key.StartsWith("2")).Value;
            // Return with no content and only description
            if (!successResponse.Content.Any())
            {
                return $"\"{successResponse.Description}\"";
            }

            var reference = successResponse.Reference?.Id;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                var modelName = UtilTS.GetModelNameFromReference(reference);
                refListsToImport.Add(modelName);
                return modelName;
            }

            var mediaType = successResponse.Content.FirstOrDefault().Value;
            if (mediaType != null)
            {
                var mediaReference = mediaType.Schema?.Reference?.Id;
                if (!string.IsNullOrWhiteSpace(mediaReference))
                {
                    var modelName = UtilTS.GetModelNameFromReference(mediaReference);
                    refListsToImport.Add(modelName);
                    return modelName;
                }
                else
                {
                    var schema = OpenAPISchemaProcesser.CreateTSInterfaceFromSchema("", mediaType.Schema, refListsToImport);
                    if (schema != null)
                    {
                        return OpenAPISchemaProcesser.ConstructRawObject(schema);
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

