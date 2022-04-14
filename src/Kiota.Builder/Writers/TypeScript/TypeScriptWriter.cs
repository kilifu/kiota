﻿namespace Kiota.Builder.Writers.TypeScript
{
    public class TypeScriptWriter : LanguageWriter
    {
        public TypeScriptWriter(string rootPath, string clientNamespaceName)
        {
            PathSegmenter = new TypeScriptPathSegmenter(rootPath,clientNamespaceName);
            var conventionService = new TypeScriptConventionService(null);
            AddOrReplaceCodeElementWriter(new CodeClassDeclarationWriter(conventionService, clientNamespaceName));
            AddOrReplaceCodeElementWriter(new CodeBlockEndWriter());
            AddOrReplaceCodeElementWriter(new CodeEnumWriter(conventionService));
            AddOrReplaceCodeElementWriter(new CodeMethodWriter(conventionService));
            AddOrReplaceCodeElementWriter(new CodeFunctionWriter(conventionService, clientNamespaceName));
            AddOrReplaceCodeElementWriter(new CodePropertyWriter(conventionService));
            AddOrReplaceCodeElementWriter(new CodeTypeWriter(conventionService));
            AddOrReplaceCodeElementWriter(new CodeNameSpaceWriter(conventionService));
            AddOrReplaceCodeElementWriter(new CodeInterfaceWriter(conventionService, clientNamespaceName));
        }
    }
}
