using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata.Ecma335;
using Kiota.Builder.Extensions;
using Kiota.Builder.OpenApiExtensions;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Kiota.Builder
{
    public class OpenAPIOpertaionsProcesser
    {
        public static TSOperation GetOperationsForPath(IDictionary<OperationType, OpenApiOperation> operations, int count, HashSet<string> refListsToImport)
        {
            var op = new TSOperation();

            op.Name = "operation" + count;
            var s = operations.Where(k => k.Key == OperationType.Get);

            foreach (var operation in operations)
            {
                var operationLine = "";
                operationLine = $"{operation.Key.GetType()}:{GetReturnTypeOfOperation(operation.Value.Responses, refListsToImport)}";
                op.operationWithParamString.Add(operationLine);
            }

            return op;
        }

        private static string GetReturnTypeOfOperation(OpenApiResponses responses, HashSet<string> refListsToImport)
        {
            OpenApiResponse successResponse = responses.FirstOrDefault(x => x.Key.StartsWith("2")).Value;

            var reference = successResponse.Reference?.Id;
            if (!string.IsNullOrWhiteSpace(reference))
            {
                refListsToImport.Add(reference);
                return successResponse.Reference.Id;
            }

            var mediaType = successResponse.Content.FirstOrDefault(x => x.Key == "application/json").Value;
            if (mediaType != null)
            {
                var refer = successResponse.Reference?.Id;
                if (!string.IsNullOrWhiteSpace(refer))
                {
                    refListsToImport.Add(refer);
                    return UtilTS.GetModelNameFromReference(refer);
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
    }
}

