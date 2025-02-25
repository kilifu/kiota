﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Globbing;
using Kiota.Builder.Caching;
using Kiota.Builder.CodeDOM;
using Kiota.Builder.CodeRenderers;
using Kiota.Builder.Configuration;
using Kiota.Builder.Exceptions;
using Kiota.Builder.Extensions;
using Kiota.Builder.Lock;
using Kiota.Builder.OpenApiExtensions;
using Kiota.Builder.Refiners;
using Kiota.Builder.Writers;

using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using Microsoft.OpenApi.Services;

using HttpMethod = Kiota.Builder.CodeDOM.HttpMethod;

namespace Kiota.Builder;

public class KiotaBuilder
{
    private readonly ILogger<KiotaBuilder> logger;
    private readonly GenerationConfiguration config;
    private readonly HttpClient httpClient;
    private OpenApiDocument originalDocument;
    private OpenApiDocument openApiDocument;
    internal void SetOpenApiDocument(OpenApiDocument document) => openApiDocument = document ?? throw new ArgumentNullException(nameof(document));

    public KiotaBuilder(ILogger<KiotaBuilder> logger, GenerationConfiguration config, HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(client);
        this.logger = logger;
        this.config = config;
        this.httpClient = client;
    }
    private void CleanOutputDirectory()
    {
        if(config.CleanOutput && Directory.Exists(config.OutputPath))
        {
            logger.LogInformation("Cleaning output directory {path}", config.OutputPath);
            Directory.Delete(config.OutputPath, true);
        }
    }
    public async Task<OpenApiUrlTreeNode> GetUrlTreeNodeAsync(CancellationToken cancellationToken) {
        var sw = new Stopwatch();
        string inputPath = config.OpenAPIFilePath;
        var (_, openApiTree, _) = await GetTreeNodeInternal(inputPath, false, sw, cancellationToken);
        return openApiTree;
    }
    private async Task<(int, OpenApiUrlTreeNode, bool)> GetTreeNodeInternal(string inputPath, bool generating, Stopwatch sw, CancellationToken cancellationToken) {
        var stepId = 0;
        sw.Start();
        await using var input = await (originalDocument == null ?
                                        LoadStream(inputPath, cancellationToken) :
                                        Task.FromResult<Stream>(new MemoryStream()));
        if(input == null)
            return (0, null, false);
        StopLogAndReset(sw, $"step {++stepId} - reading the stream - took");

        // Create patterns
        sw.Start();
        var pathPatterns = BuildGlobPatterns();
        StopLogAndReset(sw, $"step {++stepId} - parsing URI patterns - took");

        // Parse OpenAPI
        sw.Start();
        if (originalDocument == null)
            openApiDocument = CreateOpenApiDocument(input);
        else
            openApiDocument = new OpenApiDocument(originalDocument);
        StopLogAndReset(sw, $"step {++stepId} - parsing the document - took");
        if(originalDocument == null)
            originalDocument = new OpenApiDocument(openApiDocument);

        // Should Generate
        sw.Start();
        var shouldGenerate = await ShouldGenerate(cancellationToken);
        StopLogAndReset(sw, $"step {++stepId} - checking whether the output should be updated - took");

        OpenApiUrlTreeNode openApiTree = null;
        if(shouldGenerate || !generating) {

            // filter paths
            sw.Start();
            FilterPathsByPatterns(openApiDocument, pathPatterns.Item1, pathPatterns.Item2);
            StopLogAndReset(sw, $"step {++stepId} - filtering API paths with patterns - took");

            SetApiRootUrl();

            modelNamespacePrefixToTrim = GetDeeperMostCommonNamespaceNameForModels(openApiDocument);

            // Create Uri Space of API
            sw.Start();
            openApiTree = CreateUriSpace(openApiDocument);
            StopLogAndReset(sw, $"step {++stepId} - create uri space - took");
        }

        return (stepId, openApiTree, shouldGenerate);
    }
    private async Task<bool> ShouldGenerate(CancellationToken cancellationToken) {
        if(config.CleanOutput) return true;
        var existingLock = await lockManagementService.GetLockFromDirectoryAsync(config.OutputPath, cancellationToken);
        var configurationLock = new KiotaLock(config) {
            DescriptionHash = openApiDocument.HashCode,
        };
        var comparer = new KiotaLockComparer();
        return !comparer.Equals(existingLock, configurationLock);
    }

    public async Task<LanguagesInformation> GetLanguageInformationAsync(CancellationToken cancellationToken)
    {
        await GetTreeNodeInternal(config.OpenAPIFilePath, false, new Stopwatch(), cancellationToken);
        if (openApiDocument == null)
            return null;
        if (openApiDocument.Extensions.TryGetValue(OpenApiKiotaExtension.Name, out var ext) && ext is OpenApiKiotaExtension kiotaExt)
            return kiotaExt.LanguagesInformation;
        return null;
    }

    /// <summary>
    /// Generates the code from the OpenAPI document
    /// </summary>
    /// <param name="cancellationToken">The cancellation token</param>
    /// <returns>Whether the generated code was updated or not</returns>
    public async Task<bool> GenerateClientAsync(CancellationToken cancellationToken)
    {
        var sw = new Stopwatch();
        // Read input stream
        string inputPath = config.OpenAPIFilePath;

        try {
            CleanOutputDirectory();
            // doing this verification at the beginning to give immediate feedback to the user
            Directory.CreateDirectory(config.OutputPath);
        } catch (Exception ex) {
            throw new InvalidOperationException($"Could not open/create output directory {config.OutputPath}, reason: {ex.Message}", ex);
        }
        var (stepId, openApiTree, shouldGenerate) = await GetTreeNodeInternal(inputPath, true, sw, cancellationToken);

        if(!shouldGenerate) {
            logger.LogInformation("No changes detected, skipping generation");
            return false;
        }
        // Create Source Model
        sw.Start();
        var generatedCode = CreateSourceModel(openApiTree);
        StopLogAndReset(sw, $"step {++stepId} - create source model - took");

        // RefineByLanguage
        sw.Start();
        await ApplyLanguageRefinement(config, generatedCode, cancellationToken);
        StopLogAndReset(sw, $"step {++stepId} - refine by language - took");

        // Write language source
        sw.Start();
        await CreateLanguageSourceFilesAsync(config.Language, generatedCode, cancellationToken);
        StopLogAndReset(sw, $"step {++stepId} - writing files - took");

        // Write lock file
        sw.Start();
        await UpdateLockFile(cancellationToken);
        StopLogAndReset(sw, $"step {++stepId} - writing lock file - took");
        return true;
    }
    private readonly LockManagementService lockManagementService = new();
    private async Task UpdateLockFile(CancellationToken cancellationToken) {
        var configurationLock = new KiotaLock(config) {
            DescriptionHash = openApiDocument.HashCode,
        };
        await lockManagementService.WriteLockFileAsync(config.OutputPath, configurationLock, cancellationToken);
    }
    public (List<Glob>, List<Glob>) BuildGlobPatterns() {
        var includePatterns = new List<Glob>();
        var excludePatterns = new List<Glob>();
        if (config.IncludePatterns?.Any() ?? false) {
            includePatterns.AddRange(config.IncludePatterns.Select(static x => Glob.Parse(x)));
        }
        if (config.ExcludePatterns?.Any() ?? false) {
            excludePatterns.AddRange(config.ExcludePatterns.Select(static x => Glob.Parse(x)));
        }
        return (includePatterns, excludePatterns);
    }
    public void FilterPathsByPatterns(OpenApiDocument doc, List<Glob> includePatterns, List<Glob> excludePatterns) {
        if (!includePatterns.Any() && !excludePatterns.Any()) return;

        doc.Paths.Keys.Except(
            doc.Paths.Keys.Where(x => (!includePatterns.Any() || includePatterns.Any(y => y.IsMatch(x))) &&
                                (!excludePatterns.Any() || !excludePatterns.Any(y => y.IsMatch(x))))
        )
        .ToList()
        .ForEach(x => doc.Paths.Remove(x));
    }
    private void SetApiRootUrl() {
        config.ApiRootUrl = openApiDocument.Servers.FirstOrDefault()?.Url.TrimEnd('/');
        if(string.IsNullOrEmpty(config.ApiRootUrl))
            logger.LogWarning("A servers entry (v3) or host + basePath + schemes properties (v2) was not present in the OpenAPI description. The root URL will need to be set manually with the request adapter.");
    }
    private void StopLogAndReset(Stopwatch sw, string prefix) {
        sw.Stop();
        logger.LogDebug("{prefix} {swElapsed}", prefix, sw.Elapsed);
        sw.Reset();
    }


    private async Task<Stream> LoadStream(string inputPath, CancellationToken cancellationToken)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        inputPath = inputPath.Trim();

        Stream input;
        if (inputPath.StartsWith("http"))
            try {
                var cachingProvider = new DocumentCachingProvider(httpClient, logger) {
                    ClearCache = config.ClearCache,
                };
                var targetUri = new Uri(inputPath);
                var fileName = targetUri.GetFileName() is string name && !string.IsNullOrEmpty(name) ? name : "description.yml";
                input = await cachingProvider.GetDocumentAsync(targetUri, "generation", fileName, cancellationToken: cancellationToken);
            } catch (HttpRequestException ex) {
                throw new InvalidOperationException($"Could not download the file at {inputPath}, reason: {ex.Message}", ex);
            }
        else
            try {
                input = new FileStream(inputPath, FileMode.Open);
            } catch (Exception ex) when (ex is FileNotFoundException ||
                ex is PathTooLongException ||
                ex is DirectoryNotFoundException ||
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is SecurityException ||
                ex is NotSupportedException) {
                throw new InvalidOperationException($"Could not open the file at {inputPath}, reason: {ex.Message}", ex);
            }
        stopwatch.Stop();
        logger.LogTrace("{timestamp}ms: Read OpenAPI file {file}", stopwatch.ElapsedMilliseconds, inputPath);
        return input;
    }

    public OpenApiDocument CreateOpenApiDocument(Stream input)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        logger.LogTrace("Parsing OpenAPI file");
        var reader = new OpenApiStreamReader(new OpenApiReaderSettings
        {
            ExtensionParsers = new()
            {
                {
                    OpenApiPagingExtension.Name,
                    (i, _) => OpenApiPagingExtension.Parse(i)
                },
                {
                    OpenApiEnumValuesDescriptionExtension.Name,
                    static (i, _ ) => OpenApiEnumValuesDescriptionExtension.Parse(i)
                },
                {
                    OpenApiKiotaExtension.Name,
                    static (i, _ ) => OpenApiKiotaExtension.Parse(i)
                },
            }
        });
        var doc = reader.Read(input, out var diag);
        stopwatch.Stop();
        if (diag.Errors.Count > 0)
        {
            logger.LogTrace("{timestamp}ms: Parsed OpenAPI with errors. {count} paths found.", stopwatch.ElapsedMilliseconds, doc?.Paths?.Count ?? 0);
            foreach(var parsingError in diag.Errors)
            {
                logger.LogError("OpenApi Parsing error: {message}", parsingError.ToString());
            }
        }
        else
        {
            logger.LogTrace("{timestamp}ms: Parsed OpenAPI successfully. {count} paths found.", stopwatch.ElapsedMilliseconds, doc?.Paths?.Count ?? 0);
        }

        return doc;
    }
    public static string GetDeeperMostCommonNamespaceNameForModels(OpenApiDocument document)
    {
        if(!(document?.Components?.Schemas?.Any() ?? false)) return string.Empty;
        var distinctKeys = document.Components
                                .Schemas
                                .Keys
                                .Select(x => string.Join(nsNameSeparator, x.Split(nsNameSeparator, StringSplitOptions.RemoveEmptyEntries)
                                                .SkipLast(1)))
                                .Where(x => !string.IsNullOrEmpty(x))
                                .Distinct()
                                .OrderByDescending(x => x.Count(y => y == nsNameSeparator));
        if(!distinctKeys.Any()) return string.Empty;
        var longestKey = distinctKeys.FirstOrDefault();
        var candidate = string.Empty;
        var longestKeySegments = longestKey.Split(nsNameSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach(var segment in longestKeySegments)
        {
            var testValue = (candidate + nsNameSeparator + segment).Trim(nsNameSeparator);
            if(distinctKeys.All(x => x.StartsWith(testValue, StringComparison.OrdinalIgnoreCase)))
                candidate = testValue;
            else
                break;
        }

        return candidate;
    }

    /// <summary>
    /// Translate OpenApi PathItems into a tree structure that will define the classes
    /// </summary>
    /// <param name="doc">OpenAPI Document of the API to be processed</param>
    /// <returns>Root node of the API URI space</returns>
    public OpenApiUrlTreeNode CreateUriSpace(OpenApiDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if(openApiDocument == null) openApiDocument = doc;

        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var node = OpenApiUrlTreeNode.Create(doc, Constants.DefaultOpenApiLabel);
        stopwatch.Stop();
        logger.LogTrace("{timestamp}ms: Created UriSpace tree", stopwatch.ElapsedMilliseconds);
        return node;
    }
    private CodeNamespace rootNamespace;
    private CodeNamespace modelsNamespace;
    private string modelNamespacePrefixToTrim;

    /// <summary>
    /// Convert UriSpace of OpenApiPathItems into conceptual SDK Code model
    /// </summary>
    /// <param name="root">Root OpenApiUriSpaceNode of API to be generated</param>
    /// <returns></returns>
    public CodeNamespace CreateSourceModel(OpenApiUrlTreeNode root)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        rootNamespace = CodeNamespace.InitRootNamespace();
        var codeNamespace = rootNamespace.AddNamespace(config.ClientNamespaceName);
        modelsNamespace = rootNamespace.AddNamespace(config.ModelsNamespaceName);
        InitializeInheritanceIndex();
        StopLogAndReset(stopwatch, $"{nameof(InitializeInheritanceIndex)}");
        CreateRequestBuilderClass(codeNamespace, root, root);
        StopLogAndReset(stopwatch, $"{nameof(CreateRequestBuilderClass)}");
        stopwatch.Start();
        MapTypeDefinitions(codeNamespace);
        StopLogAndReset(stopwatch, $"{nameof(MapTypeDefinitions)}");

        logger.LogTrace("{timestamp}ms: Created source model with {count} classes", stopwatch.ElapsedMilliseconds, codeNamespace.GetChildElements(true).Count());

        return rootNamespace;
    }

    /// <summary>
    /// Manipulate CodeDOM for language specific issues
    /// </summary>
    /// <param name="config"></param>
    /// <param name="generatedCode"></param>
    /// <param name="token"></param>
    public async Task ApplyLanguageRefinement(GenerationConfiguration config, CodeNamespace generatedCode, CancellationToken token)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        await ILanguageRefiner.Refine(config, generatedCode, token);

        stopwatch.Stop();
        logger.LogDebug("{timestamp}ms: Language refinement applied", stopwatch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Iterate through Url Space and create request builder classes for each node in the tree
    /// </summary>
    /// <param name="root">Root node of URI space from the OpenAPI described API</param>
    /// <returns>A CodeNamespace object that contains request builder classes for the Uri Space</returns>

    public async Task CreateLanguageSourceFilesAsync(GenerationLanguage language, CodeNamespace generatedCode, CancellationToken cancellationToken)
    {
        var languageWriter = LanguageWriter.GetLanguageWriter(language, config.OutputPath, config.ClientNamespaceName, config.UsesBackingStore);
        var stopwatch = new Stopwatch();
        stopwatch.Start();
        var codeRenderer = CodeRenderer.GetCodeRender(config);
        await codeRenderer.RenderCodeNamespaceToFilePerClassAsync(languageWriter, generatedCode, cancellationToken);
        stopwatch.Stop();
        logger.LogTrace("{timestamp}ms: Files written to {path}", stopwatch.ElapsedMilliseconds, config.OutputPath);
    }
    private static readonly string requestBuilderSuffix = "RequestBuilder";
    private static readonly string itemRequestBuilderSuffix = "ItemRequestBuilder";
    private static readonly string voidType = "void";
    private static readonly string coreInterfaceType = "IRequestAdapter";
    private static readonly string requestAdapterParameterName = "requestAdapter";
    private static readonly string constructorMethodName = "constructor";
    /// <summary>
    /// Create a CodeClass instance that is a request builder class for the OpenApiUrlTreeNode
    /// </summary>
    private void CreateRequestBuilderClass(CodeNamespace currentNamespace, OpenApiUrlTreeNode currentNode, OpenApiUrlTreeNode rootNode)
    {
        // Determine Class Name
        CodeClass codeClass;
        var isApiClientClass = currentNode == rootNode;
        if (isApiClientClass)
            codeClass = currentNamespace.AddClass(new CodeClass {
            Name = config.ClientClassName,
            Kind = CodeClassKind.RequestBuilder,
            Description = "The main entry point of the SDK, exposes the configuration and the fluent API."
        }).First();
        else
        {
            var targetNS = currentNode.DoesNodeBelongToItemSubnamespace() ? currentNamespace.EnsureItemNamespace() : currentNamespace;
            var className = currentNode.DoesNodeBelongToItemSubnamespace() ? currentNode.GetClassName(config.StructuredMimeTypes, itemRequestBuilderSuffix) : currentNode.GetClassName(config.StructuredMimeTypes, requestBuilderSuffix);
            codeClass = targetNS.AddClass(new CodeClass {
                Name = className.CleanupSymbolName(),
                Kind = CodeClassKind.RequestBuilder,
                Description = currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel, $"Builds and executes requests for operations under {currentNode.Path}"),
            }).First();
        }

        logger.LogTrace("Creating class {class}", codeClass.Name);

        // Add properties for children
        foreach (var child in currentNode.Children)
        {
            var propIdentifier = child.Value.GetClassName(config.StructuredMimeTypes);
            var propType = child.Value.DoesNodeBelongToItemSubnamespace() ? propIdentifier + itemRequestBuilderSuffix : propIdentifier + requestBuilderSuffix;
            if (child.Value.IsPathSegmentWithSingleSimpleParameter())
            {
                var prop = CreateIndexer($"{propIdentifier}-indexer", propType, child.Value, currentNode);
                codeClass.SetIndexer(prop);
            }
            else if (child.Value.IsComplexPathWithAnyNumberOfParameters())
            {
                CreateMethod(propIdentifier, propType, codeClass, child.Value);
            }
            else
            {
                var description = child.Value.GetPathItemDescription(Constants.DefaultOpenApiLabel);
                var prop = CreateProperty(propIdentifier, propType, kind: CodePropertyKind.RequestBuilder); // we should add the type definition here but we can't as it might not have been generated yet
                if (!string.IsNullOrWhiteSpace(description))
                {
                    prop.Description = description;
                }
                codeClass.AddProperty(prop);
            }
        }

        // Add methods for Operations
        if (currentNode.HasOperations(Constants.DefaultOpenApiLabel))
        {
            foreach(var operation in currentNode
                                    .PathItems[Constants.DefaultOpenApiLabel]
                                    .Operations)
                CreateOperationMethods(currentNode, operation.Key, operation.Value, codeClass);
        }
        CreateUrlManagement(codeClass, currentNode, isApiClientClass);

        Parallel.ForEach(currentNode.Children.Values, childNode =>
        {
            var targetNamespaceName = childNode.GetNodeNamespaceFromPath(config.ClientNamespaceName);
            var targetNamespace = rootNamespace.FindOrAddNamespace(targetNamespaceName);
            CreateRequestBuilderClass(targetNamespace, childNode, rootNode);
        });
    }
    private static void CreateMethod(string propIdentifier, string propType, CodeClass codeClass, OpenApiUrlTreeNode currentNode)
    {
        var methodToAdd = new CodeMethod
        {
            Name = propIdentifier.CleanupSymbolName(),
            Kind = CodeMethodKind.RequestBuilderWithParameters,
            Description = currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel, $"Builds and executes requests for operations under {currentNode.Path}"),
            Access = AccessModifier.Public,
            IsAsync = false,
            IsStatic = false,
            Parent = codeClass,
            ReturnType = new CodeType
            {
                Name = propType,
                ActionOf = false,
                CollectionKind = CodeTypeBase.CodeTypeCollectionKind.None,
                IsExternal = false,
                IsNullable = false,
            }
        };
        AddPathParametersToMethod(currentNode, methodToAdd, false);
        codeClass.AddMethod(methodToAdd);
    }
    private static void AddPathParametersToMethod(OpenApiUrlTreeNode currentNode, CodeMethod methodToAdd, bool asOptional) {
        foreach(var parameter in currentNode.GetPathParametersForCurrentSegment()) {
            var codeName = parameter.Name.SanitizeParameterNameForCodeSymbols();
            var mParameter = new CodeParameter {
                Name = codeName,
                Optional = asOptional,
                Description = parameter.Description.CleanupDescription(),
                Kind = CodeParameterKind.Path,
                SerializationName = parameter.Name.Equals(codeName) ? default : parameter.Name.SanitizeParameterNameForUrlTemplate(),
            };
            mParameter.Type = GetPrimitiveType(parameter.Schema ?? parameter.Content.Values.FirstOrDefault()?.Schema);
            mParameter.Type.CollectionKind = parameter.Schema.IsArray() ? CodeTypeBase.CodeTypeCollectionKind.Array : default;
            // not using the content schema as RFC6570 will serialize arrays as CSVs and content expects a JSON array, we failsafe to opaque string, it could be improved by involving the serialization layers.
            methodToAdd.AddParameter(mParameter);
        }
    }
    private static readonly string PathParametersParameterName = "pathParameters";
    private void CreateUrlManagement(CodeClass currentClass, OpenApiUrlTreeNode currentNode, bool isApiClientClass) {
        var pathProperty = new CodeProperty {
            Access = AccessModifier.Private,
            Name = "urlTemplate",
            DefaultValue = $"\"{currentNode.GetUrlTemplate()}\"",
            ReadOnly = true,
            Description = "Url template to use to build the URL for the current request builder",
            Kind = CodePropertyKind.UrlTemplate,
            Type = new CodeType {
                Name = "string",
                IsNullable = false,
                IsExternal = true,
            },
        };
        currentClass.AddProperty(pathProperty);

        var requestAdapterProperty = new CodeProperty
        {
            Name = requestAdapterParameterName,
            Description = "The request adapter to use to execute the requests.",
            Kind = CodePropertyKind.RequestAdapter,
            Access = AccessModifier.Private,
            ReadOnly = true,
            Type = new CodeType
            {
                Name = coreInterfaceType,
                IsExternal = true,
                IsNullable = false,
            }
        };
        currentClass.AddProperty(requestAdapterProperty);
        var constructor = new CodeMethod {
            Name = constructorMethodName,
            Kind = isApiClientClass ? CodeMethodKind.ClientConstructor : CodeMethodKind.Constructor,
            IsAsync = false,
            IsStatic = false,
            Description = $"Instantiates a new {currentClass.Name.ToFirstCharacterUpperCase()} and sets the default values.",
            Access = AccessModifier.Public,
            ReturnType = new CodeType { Name = voidType, IsExternal = true },
            Parent = currentClass,
        };
        var pathParametersProperty = new CodeProperty {
            Name = PathParametersParameterName,
            Description = "Path parameters for the request",
            Kind = CodePropertyKind.PathParameters,
            Access = AccessModifier.Private,
            ReadOnly = true,
            Type = new CodeType {
                Name = "Dictionary<string, object>",
                IsExternal = true,
                IsNullable = false,
            },
        };
        currentClass.AddProperty(pathParametersProperty);
        if(isApiClientClass) {
            constructor.SerializerModules = config.Serializers;
            constructor.DeserializerModules = config.Deserializers;
            constructor.BaseUrl = config.ApiRootUrl;
            pathParametersProperty.DefaultValue = $"new {pathParametersProperty.Type.Name}()";
        } else {
            constructor.AddParameter(new CodeParameter {
                Name = PathParametersParameterName,
                Type = pathParametersProperty.Type,
                Optional = false,
                Description = pathParametersProperty.Description,
                Kind = CodeParameterKind.PathParameters,
            });
            AddPathParametersToMethod(currentNode, constructor, true);
        }
        constructor.AddParameter(new CodeParameter {
            Name = requestAdapterParameterName,
            Type = requestAdapterProperty.Type,
            Optional = false,
            Description = requestAdapterProperty.Description,
            Kind = CodeParameterKind.RequestAdapter,
        });
        if(isApiClientClass && config.UsesBackingStore) {
            var factoryInterfaceName = $"{BackingStoreInterface}Factory";
            var backingStoreParam = new CodeParameter {
                Name = "backingStore",
                Optional = true,
                Description = "The backing store to use for the models.",
                Kind = CodeParameterKind.BackingStore,
                Type = new CodeType {
                    Name = factoryInterfaceName,
                    IsNullable = true,
                }
            };
            constructor.AddParameter(backingStoreParam);
        }
        currentClass.AddMethod(constructor);
    }
    private static readonly Func<CodeClass, int> shortestNamespaceOrder = x => x.GetNamespaceDepth();
    /// <summary>
    /// Remaps definitions to custom types so they can be used later in generation or in refiners
    /// </summary>
    private void MapTypeDefinitions(CodeElement codeElement) {
        var unmappedTypes = GetUnmappedTypeDefinitions(codeElement).Distinct();

        var unmappedTypesWithNoName = unmappedTypes.Where(x => string.IsNullOrEmpty(x.Name)).ToList();

        unmappedTypesWithNoName.ForEach(x => {
            logger.LogWarning("Type with empty name and parent {ParentName}", x.Parent.Name);
        });

        var unmappedTypesWithName = unmappedTypes.Except(unmappedTypesWithNoName);

        var unmappedRequestBuilderTypes = unmappedTypesWithName
                                .Where(x =>
                                x.Parent is CodeProperty property && property.IsOfKind(CodePropertyKind.RequestBuilder) ||
                                x.Parent is CodeIndexer ||
                                x.Parent is CodeMethod method && method.IsOfKind(CodeMethodKind.RequestBuilderWithParameters))
                                .ToList();

        Parallel.ForEach(unmappedRequestBuilderTypes, x => {
            var parentNS = x.Parent.Parent.Parent as CodeNamespace;
            x.TypeDefinition = parentNS.FindChildrenByName<CodeClass>(x.Name).MinBy(shortestNamespaceOrder);
            // searching down first because most request builder properties on a request builder are just sub paths on the API
            if(x.TypeDefinition == null) {
                parentNS = parentNS.Parent as CodeNamespace;
                x.TypeDefinition = (parentNS
                    .FindNamespaceByName($"{parentNS.Name}.{x.Name.Substring(0, x.Name.Length - requestBuilderSuffix.Length).ToFirstCharacterLowerCase()}".TrimEnd(nsNameSeparator))
                    ?.FindChildrenByName<CodeClass>(x.Name)).MinBy(shortestNamespaceOrder);
                // in case of the .item namespace, going to the parent and then down to the target by convention
                // this avoid getting the wrong request builder in case we have multiple request builders with the same name in the parent branch
                // in both cases we always take the uppermost item (smaller numbers of segments in the namespace name)
            }
        });

        Parallel.ForEach(unmappedTypesWithName.Where(x => x.TypeDefinition == null).GroupBy(x => x.Name), x => {
            if (rootNamespace.FindChildByName<ITypeDefinition>(x.First().Name) is CodeElement definition)
                foreach (var type in x)
                {
                    type.TypeDefinition = definition;
                    logger.LogWarning("Mapped type {typeName} for {ParentName} using the fallback approach.", type.Name, type.Parent.Name);
                }
        });
    }
    private static readonly char nsNameSeparator = '.';
    private static IEnumerable<CodeType> filterUnmappedTypeDefinitions(IEnumerable<CodeTypeBase> source) =>
    source.OfType<CodeType>()
            .Union(source
                    .OfType<CodeComposedTypeBase>()
                    .SelectMany(x => x.Types))
            .Where(x => !x.IsExternal && x.TypeDefinition == null);
    private IEnumerable<CodeType> GetUnmappedTypeDefinitions(CodeElement codeElement) {
        var childElementsUnmappedTypes = codeElement.GetChildElements(true).SelectMany(x => GetUnmappedTypeDefinitions(x));
        return codeElement switch
        {
            CodeMethod method => filterUnmappedTypeDefinitions(method.Parameters.Select(x => x.Type).Union(new[] { method.ReturnType })).Union(childElementsUnmappedTypes),
            CodeProperty property => filterUnmappedTypeDefinitions(new[] { property.Type }).Union(childElementsUnmappedTypes),
            CodeIndexer indexer => filterUnmappedTypeDefinitions(new[] { indexer.ReturnType }).Union(childElementsUnmappedTypes),
            _ => childElementsUnmappedTypes,
        };
    }
    private CodeIndexer CreateIndexer(string childIdentifier, string childType, OpenApiUrlTreeNode currentNode, OpenApiUrlTreeNode parentNode)
    {
        logger.LogTrace("Creating indexer {name}", childIdentifier);
        return new CodeIndexer
        {
            Name = childIdentifier,
            Description = currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel, $"Gets an item from the {currentNode.GetNodeNamespaceFromPath(config.ClientNamespaceName)} collection"),
            IndexType = new CodeType { Name = "string", IsExternal = true, },
            ReturnType = new CodeType { Name = childType },
            SerializationName = currentNode.Segment.SanitizeParameterNameForUrlTemplate(),
            PathSegment = parentNode.GetNodeNamespaceFromPath(string.Empty).Split('.').Last(),
        };
    }

    private CodeProperty CreateProperty(string childIdentifier, string childType, OpenApiSchema typeSchema = null, CodeTypeBase existingType = null, CodePropertyKind kind = CodePropertyKind.Custom)
    {
        var propertyName = childIdentifier.CleanupSymbolName();
        var prop = new CodeProperty
        {
            Name = propertyName,
            Kind = kind,
            Description = typeSchema?.Description.CleanupDescription() ?? $"The {propertyName} property",
            ReadOnly = typeSchema?.ReadOnly ?? false,
        };
        if(propertyName != childIdentifier)
            prop.SerializationName = childIdentifier;
        if(kind == CodePropertyKind.Custom &&
            typeSchema?.Default is OpenApiString stringDefaultValue &&
            !string.IsNullOrEmpty(stringDefaultValue.Value))
            prop.DefaultValue = $"\"{stringDefaultValue.Value}\"";

        if (existingType != null)
            prop.Type = existingType;
        else {
            prop.Type = GetPrimitiveType(typeSchema, childType);
            prop.Type.CollectionKind = typeSchema.IsArray() ? CodeTypeBase.CodeTypeCollectionKind.Complex : default;
            logger.LogTrace("Creating property {name} of {type}", prop.Name, prop.Type.Name);
        }
        return prop;
    }
    private static readonly HashSet<string> typeNamesToSkip = new(StringComparer.OrdinalIgnoreCase) {"object", "array"};
    private static CodeType GetPrimitiveType(OpenApiSchema typeSchema, string childType = default) {
        var typeNames = new List<string>{typeSchema?.Items?.Type, childType, typeSchema?.Type};
        if(typeSchema?.AnyOf?.Any() ?? false)
            typeNames.AddRange(typeSchema.AnyOf.Select(x => x.Type)); // double is sometimes an anyof string, number and enum
        if(typeSchema?.OneOf?.Any() ?? false)
            typeNames.AddRange(typeSchema.OneOf.Select(x => x.Type)); // double is sometimes an oneof string, number and enum
        // first value that's not null, and not "object" for primitive collections, the items type matters
        var typeName = typeNames.FirstOrDefault(static x => !string.IsNullOrEmpty(x) && !typeNamesToSkip.Contains(x));

        var isExternal = false;
        if (typeSchema?.Items?.IsEnum() ?? false)
            typeName = childType;
        else {
            var format = typeSchema?.Format ?? typeSchema?.Items?.Format;
            var primitiveTypeName = (typeName?.ToLowerInvariant(), format?.ToLowerInvariant()) switch {
                ("string", "base64url") => "binary",
                ("file", _) => "binary",
                ("string", "duration") => "TimeSpan",
                ("string", "time") => "TimeOnly",
                ("string", "date") => "DateOnly",
                ("string", "date-time") => "DateTimeOffset",
                ("string", _) => "string", // covers commonmark and html
                ("number", "double" or "float" or "decimal") => format.ToLowerInvariant(),
                ("number" or "integer", "int8") => "sbyte",
                ("number" or "integer", "uint8") => "byte",
                ("number" or "integer", "int64") => "int64",
                ("number", "int32") => "integer",
                ("number", _) => "int64",
                ("integer", _) => "integer",
                ("boolean", _) => "boolean",
                (_, "byte" or "binary") => "binary",
                (_, _) => string.Empty,
            };
            if(primitiveTypeName != string.Empty) {
                typeName = primitiveTypeName;
                isExternal = true;
            }
        }
        return new CodeType {
            Name = typeName,
            IsExternal = isExternal,
        };
    }
    private const string RequestBodyPlainTextContentType = "text/plain";
    private static readonly HashSet<string> noContentStatusCodes = new() { "201", "202", "204", "205" };
    private static readonly HashSet<string> errorStatusCodes = new(Enumerable.Range(400, 599).Select(static x => x.ToString())
                                                                                 .Concat(new[] { "4XX", "5XX" }), StringComparer.OrdinalIgnoreCase);

    private void AddErrorMappingsForExecutorMethod(OpenApiUrlTreeNode currentNode, OpenApiOperation operation, CodeMethod executorMethod) {
        foreach(var response in operation.Responses.Where(x => errorStatusCodes.Contains(x.Key))) {
            var errorCode = response.Key.ToUpperInvariant();
            var errorSchema = response.Value.GetResponseSchema(config.StructuredMimeTypes);
            if(errorSchema != null) {
                var parentElement = string.IsNullOrEmpty(response.Value.Reference?.Id) && string.IsNullOrEmpty(errorSchema?.Reference?.Id)
                    ? executorMethod as CodeElement
                    : modelsNamespace;
                var errorType = CreateModelDeclarations(currentNode, errorSchema, operation, parentElement, $"{errorCode}Error", response: response.Value);
                if (errorType is CodeType codeType &&
                    codeType.TypeDefinition is CodeClass codeClass &&
                    !codeClass.IsErrorDefinition)
                {
                    codeClass.IsErrorDefinition = true;
                }
                executorMethod.AddErrorMapping(errorCode, errorType);
            }
        }
    }
    private void CreateOperationMethods(OpenApiUrlTreeNode currentNode, OperationType operationType, OpenApiOperation operation, CodeClass parentClass)
    {
        var parameterClass = CreateOperationParameterClass(currentNode, operationType, operation, parentClass);
        var requestConfigClass = parentClass.AddInnerClass(new CodeClass {
            Name = $"{parentClass.Name}{operationType}RequestConfiguration",
            Kind = CodeClassKind.RequestConfiguration,
            Description = "Configuration for the request such as headers, query parameters, and middleware options.",
        }).First();

        var schema = operation.GetResponseSchema(config.StructuredMimeTypes);
        var method = (HttpMethod)Enum.Parse(typeof(HttpMethod), operationType.ToString());
        var executorMethod = new CodeMethod {
            Name = operationType.ToString(),
            Kind = CodeMethodKind.RequestExecutor,
            HttpMethod = method,
            Description = (operation.Description ?? operation.Summary).CleanupDescription(),
            Parent = parentClass,
        };

        if (operation.Extensions.TryGetValue(OpenApiPagingExtension.Name, out var extension) && extension is OpenApiPagingExtension pagingExtension)
        {
            executorMethod.PagingInformation = new PagingInformation
            {
                ItemName = pagingExtension.ItemName,
                NextLinkName = pagingExtension.NextLinkName,
                OperationName = pagingExtension.OperationName,
            };
        }

        AddErrorMappingsForExecutorMethod(currentNode, operation, executorMethod);
        if (schema != null)
        {
            var returnType = CreateModelDeclarations(currentNode, schema, operation, executorMethod, "Response");
            executorMethod.ReturnType = returnType ?? throw new InvalidOperationException("Could not resolve return type for operation");
        } else {
            string returnType;
            if(operation.Responses.Any(x => noContentStatusCodes.Contains(x.Key)))
                returnType = voidType;
            else if (operation.Responses.Any(x => x.Value.Content.ContainsKey(RequestBodyPlainTextContentType)))
                returnType = "string";
            else
                returnType = "binary";
            executorMethod.ReturnType = new CodeType { Name = returnType, IsExternal = true, };
        }

        AddRequestConfigurationProperties(parameterClass, requestConfigClass);
        AddRequestBuilderMethodParameters(currentNode, operationType, operation, requestConfigClass, executorMethod);
        parentClass.AddMethod(executorMethod);

        var handlerParam = new CodeParameter {
            Name = "responseHandler",
            Optional = true,
            Kind = CodeParameterKind.ResponseHandler,
            Description = "Response handler to use in place of the default response handling provided by the core service",
            Type = new CodeType { Name = "IResponseHandler", IsExternal = true },
        };
        executorMethod.AddParameter(handlerParam);// Add response handler parameter

        var cancellationParam = new CodeParameter{
            Name = "cancellationToken",
            Optional = true,
            Kind = CodeParameterKind.Cancellation,
            Description = "Cancellation token to use when cancelling requests",
            Type = new CodeType { Name = "CancellationToken", IsExternal = true },
        };
        executorMethod.AddParameter(cancellationParam);// Add cancellation token parameter
        logger.LogTrace("Creating method {name} of {type}", executorMethod.Name, executorMethod.ReturnType);

        var generatorMethod = new CodeMethod {
            Name = $"Create{operationType.ToString().ToFirstCharacterUpperCase()}RequestInformation",
            Kind = CodeMethodKind.RequestGenerator,
            IsAsync = false,
            HttpMethod = method,
            Description = (operation.Description ?? operation.Summary).CleanupDescription(),
            ReturnType = new CodeType { Name = "RequestInformation", IsNullable = false, IsExternal = true},
            Parent = parentClass,
        };
        if (schema != null) {
            var mediaType = operation.Responses.Values.SelectMany(static x => x.Content).First(x => x.Value.Schema == schema).Key;
            generatorMethod.AcceptedResponseTypes.Add(mediaType);
        }
        if (config.Language == GenerationLanguage.Shell)
            SetPathAndQueryParameters(generatorMethod, currentNode, operation);
        AddRequestBuilderMethodParameters(currentNode, operationType, operation, requestConfigClass, generatorMethod);
        parentClass.AddMethod(generatorMethod);
        logger.LogTrace("Creating method {name} of {type}", generatorMethod.Name, generatorMethod.ReturnType);
    }
    private static readonly Func<OpenApiParameter, CodeParameter> GetCodeParameterFromApiParameter = x => {
        var codeName = x.Name.SanitizeParameterNameForCodeSymbols();
        return new CodeParameter
        {
            Name = codeName,
            SerializationName = codeName.Equals(x.Name) ? default : x.Name,
            Type = GetQueryParameterType(x.Schema),
            Description = x.Description.CleanupDescription(),
            Kind = x.In switch
                {
                    ParameterLocation.Query => CodeParameterKind.QueryParameter,
                    ParameterLocation.Header => CodeParameterKind.Headers,
                    ParameterLocation.Path => CodeParameterKind.Path,
                    _ => throw new NotSupportedException($"No matching parameter kind is supported for parameters in {x.In}"),
                },
            Optional = !x.Required
        };
    };
    private static readonly Func<OpenApiParameter, bool> ParametersFilter = x => x.In == ParameterLocation.Path || x.In == ParameterLocation.Query || x.In == ParameterLocation.Header;
    private static void SetPathAndQueryParameters(CodeMethod target, OpenApiUrlTreeNode currentNode, OpenApiOperation operation)
    {
        var pathAndQueryParameters = currentNode
            .PathItems[Constants.DefaultOpenApiLabel]
            .Parameters
            .Where(ParametersFilter)
            .Select(GetCodeParameterFromApiParameter)
            .Union(operation
                    .Parameters
                    .Where(ParametersFilter)
                    .Select(GetCodeParameterFromApiParameter))
            .ToArray();
        target.AddPathQueryOrHeaderParameter(pathAndQueryParameters);
    }

    private static void AddRequestConfigurationProperties(CodeClass parameterClass, CodeClass requestConfigClass) {
        if(parameterClass != null) {
            requestConfigClass.AddProperty(new CodeProperty
            {
                Name = "queryParameters",
                Kind = CodePropertyKind.QueryParameters,
                Description = "Request query parameters",
                Type = new CodeType { Name = parameterClass.Name, TypeDefinition = parameterClass },
            });
        }
        requestConfigClass.AddProperty(new CodeProperty {
            Name = "headers",
            Kind = CodePropertyKind.Headers,
            Description = "Request headers",
            Type = new CodeType { Name = "IDictionary<string, string>", IsExternal = true },
        },
        new CodeProperty {
            Name = "options",
            Kind = CodePropertyKind.Options,
            Description = "Request options",
            Type = new CodeType { Name = "IList<IRequestOption>", IsExternal = true },
        });
    }

    private void AddRequestBuilderMethodParameters(OpenApiUrlTreeNode currentNode, OperationType operationType, OpenApiOperation operation, CodeClass requestConfigClass, CodeMethod method) {
        if (operation.GetRequestSchema(config.StructuredMimeTypes) is OpenApiSchema requestBodySchema)
        {
            var requestBodyType = CreateModelDeclarations(currentNode, requestBodySchema, operation, method, $"{operationType}RequestBody", isRequestBody: true);
            method.AddParameter(new CodeParameter {
                Name = "body",
                Type = requestBodyType,
                Optional = false,
                Kind = CodeParameterKind.RequestBody,
                Description = requestBodySchema.Description.CleanupDescription()
            });
            method.RequestBodyContentType = operation.RequestBody.Content.First(x => x.Value.Schema == requestBodySchema).Key;
        } else if (operation.RequestBody?.Content?.Any() ?? false) {
            var nParam = new CodeParameter {
                Name = "body",
                Optional = false,
                Kind = CodeParameterKind.RequestBody,
                Description = "Binary request body",
                Type = new CodeType {
                    Name = "binary",
                    IsExternal = true,
                    IsNullable = false,
                },
            };
            method.AddParameter(nParam);
        }
        method.AddParameter(new CodeParameter {
            Name = "requestConfiguration",
            Optional = true,
            Type = new CodeType { Name = requestConfigClass.Name, TypeDefinition = requestConfigClass, ActionOf = true },
            Kind = CodeParameterKind.RequestConfiguration,
            Description = "Configuration for the request such as headers, query parameters, and middleware options.",
        });
    }
    private string GetModelsNamespaceNameFromReferenceId(string referenceId) {
        if (string.IsNullOrEmpty(referenceId)) return referenceId;
        if(referenceId.StartsWith(config.ClientClassName, StringComparison.OrdinalIgnoreCase)) // the client class having a namespace segment name can be problematic in some languages
            referenceId = referenceId[config.ClientClassName.Length..];
        referenceId = referenceId.Trim(nsNameSeparator);
        if(!string.IsNullOrEmpty(modelNamespacePrefixToTrim) && referenceId.StartsWith(modelNamespacePrefixToTrim, StringComparison.OrdinalIgnoreCase))
            referenceId = referenceId[modelNamespacePrefixToTrim.Length..];
        referenceId = referenceId.Trim(nsNameSeparator);
        var lastDotIndex = referenceId.LastIndexOf(nsNameSeparator);
        var namespaceSuffix = lastDotIndex != -1 ? $".{referenceId[..lastDotIndex]}" : string.Empty;
        return $"{modelsNamespace.Name}{namespaceSuffix}";
    }
    private CodeType CreateModelDeclarationAndType(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeNamespace codeNamespace, string classNameSuffix = "", OpenApiResponse response = default, string typeNameForInlineSchema = "", bool isRequestBody = false) {
        var className = string.IsNullOrEmpty(typeNameForInlineSchema) ? currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, suffix: classNameSuffix, response: response, schema: schema, requestBody: isRequestBody).CleanupSymbolName() : typeNameForInlineSchema;
        var codeDeclaration = AddModelDeclarationIfDoesntExist(currentNode, schema, className, codeNamespace);
        return new CodeType {
            TypeDefinition = codeDeclaration,
            Name = className,
        };
    }
    private CodeTypeBase CreateInheritedModelDeclaration(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, string classNameSuffix, CodeNamespace codeNamespace, bool isRequestBody) {
        var allOfs = schema.AllOf.FlattenEmptyEntries(static x => x.AllOf);
        CodeElement codeDeclaration = null;
        var className = string.Empty;
        var codeNamespaceFromParent = GetShortestNamespace(codeNamespace,schema);
        foreach(var currentSchema in allOfs) {
            var referenceId = GetReferenceIdFromOriginalSchema(currentSchema, schema);
            var shortestNamespaceName = GetModelsNamespaceNameFromReferenceId(referenceId);
            var shortestNamespace = string.IsNullOrEmpty(referenceId) ? codeNamespaceFromParent : rootNamespace.FindOrAddNamespace(shortestNamespaceName);
            className = (currentSchema.GetSchemaName() ?? currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, suffix: classNameSuffix, schema: schema, requestBody: isRequestBody)).CleanupSymbolName();
            codeDeclaration = AddModelDeclarationIfDoesntExist(currentNode, currentSchema, className, shortestNamespace, codeDeclaration as CodeClass);
        }

        return new CodeType {
            TypeDefinition = codeDeclaration,
            Name = className,
        };
    }
    private static string GetReferenceIdFromOriginalSchema(OpenApiSchema schema, OpenApiSchema parentSchema) {
        var title = schema.Title;
        if(!string.IsNullOrEmpty(schema.Reference?.Id)) return schema.Reference.Id;
        if (string.IsNullOrEmpty(title)) return string.Empty;
        if(parentSchema.Reference?.Id?.EndsWith(title, StringComparison.OrdinalIgnoreCase) ?? false) return parentSchema.Reference.Id;
        if(parentSchema.Items?.Reference?.Id?.EndsWith(title, StringComparison.OrdinalIgnoreCase) ?? false) return parentSchema.Items.Reference.Id;
        return (parentSchema.
                        AllOf
                        .FirstOrDefault(x => x.Reference?.Id?.EndsWith(title, StringComparison.OrdinalIgnoreCase) ?? false) ??
                parentSchema.
                        AnyOf
                        .FirstOrDefault(x => x.Reference?.Id?.EndsWith(title, StringComparison.OrdinalIgnoreCase) ?? false) ??
                parentSchema.
                        OneOf
                        .FirstOrDefault(x => x.Reference?.Id?.EndsWith(title, StringComparison.OrdinalIgnoreCase) ?? false))
            ?.Reference?.Id;
    }
    private CodeTypeBase CreateComposedModelDeclaration(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, string suffixForInlineSchema, CodeNamespace codeNamespace, bool isRequestBody) {
        var typeName = currentNode.GetClassName(config.StructuredMimeTypes, operation: operation, suffix: suffixForInlineSchema, schema: schema, requestBody: isRequestBody).CleanupSymbolName();
        var typesCount = schema.AnyOf?.Count ?? schema.OneOf?.Count ?? 0;
        if ((typesCount == 1 && schema.Nullable && schema.IsAnyOf()) || // nullable on the root schema outside of anyOf
            typesCount == 2 && schema.AnyOf.Any(static x => // nullable on a schema in the anyOf
                                                        x.Nullable &&
                                                        !x.Properties.Any() &&
                                                        !x.IsOneOf() &&
                                                        !x.IsAnyOf() &&
                                                        !x.IsAllOf() &&
                                                        !x.IsArray() &&
                                                        !x.IsReferencedSchema())) { // once openAPI 3.1 is supported, there will be a third case oneOf with Ref and type null.
            var targetSchema = schema.AnyOf.First(static x => !string.IsNullOrEmpty(x.GetSchemaName()));
            var className = targetSchema.GetSchemaName().CleanupSymbolName();
            var shortestNamespace = GetShortestNamespace(codeNamespace, targetSchema);
            return new CodeType {
                TypeDefinition = AddModelDeclarationIfDoesntExist(currentNode, targetSchema, className, shortestNamespace),
                Name = className,
            };// so we don't create unnecessary union types when anyOf was used only for nullable.
        }
        var (unionType, schemas) = (schema.IsOneOf(), schema.IsAnyOf()) switch {
            (true, false) => (new CodeUnionType {
                Name = typeName,
            } as CodeComposedTypeBase, schema.OneOf),
            (false, true) => (new CodeIntersectionType {
                Name = typeName,
            }, schema.AnyOf),
            (_, _) => throw new InvalidOperationException("Schema is not oneOf nor anyOf"),
        };
        if(!string.IsNullOrEmpty(schema.Reference?.Id))
            unionType.TargetNamespace = codeNamespace.GetRootNamespace().FindOrAddNamespace(GetModelsNamespaceNameFromReferenceId(schema.Reference.Id));
        unionType.DiscriminatorInformation.DiscriminatorPropertyName = GetDiscriminatorPropertyName(schema);
        GetDiscriminatorMappings(currentNode, schema, codeNamespace, null)
            ?.ToList()
            .ForEach(x => unionType.DiscriminatorInformation.AddDiscriminatorMapping(x.Key, x.Value));
        var membersWithNoName = 0;
        foreach(var currentSchema in schemas) {
            var shortestNamespace = GetShortestNamespace(codeNamespace,currentSchema);
            var className = currentSchema.GetSchemaName().CleanupSymbolName();
            if (string.IsNullOrEmpty(className))
                if(GetPrimitiveType(currentSchema) is CodeType primitiveType && !string.IsNullOrEmpty(primitiveType.Name)) {
                    unionType.AddType(primitiveType);
                    continue;
                } else
                    className = $"{unionType.Name}Member{++membersWithNoName}";
            var codeDeclaration = AddModelDeclarationIfDoesntExist(currentNode, currentSchema, className, shortestNamespace);
            unionType.AddType(new CodeType {
                TypeDefinition = codeDeclaration,
                Name = className,
            });
        }
        return unionType;
    }
    private CodeTypeBase CreateModelDeclarations(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeElement parentElement, string suffixForInlineSchema, OpenApiResponse response = default, string typeNameForInlineSchema = default, bool isRequestBody = false)
    {
        var (codeNamespace, responseValue, suffix) = schema.IsReferencedSchema() switch {
            true => (GetShortestNamespace(parentElement.GetImmediateParentOfType<CodeNamespace>(), schema), response, string.Empty), // referenced schema
            false => (parentElement.GetImmediateParentOfType<CodeNamespace>(), null, suffixForInlineSchema), // Inline schema, i.e. specific to the Operation
        };

        if(schema.IsAllOf()) {
            return CreateInheritedModelDeclaration(currentNode, schema, operation, suffix, codeNamespace, isRequestBody);
        }

        if((schema.IsAnyOf() || schema.IsOneOf()) && string.IsNullOrEmpty(schema.Format)
            && !schema.IsODataPrimitiveType()) { // OData types are oneOf string, type + format, enum
            return CreateComposedModelDeclaration(currentNode, schema, operation, suffix, codeNamespace, isRequestBody);
        }

        if(schema.IsObject() || schema.Properties.Any() || schema.IsEnum() || !string.IsNullOrEmpty(schema.AdditionalProperties?.Type)) {
            // no inheritance or union type, often empty definitions with only additional properties are used as property bags.
            return CreateModelDeclarationAndType(currentNode, schema, operation, codeNamespace, suffix, response: responseValue, typeNameForInlineSchema: typeNameForInlineSchema, isRequestBody);
        }

        if (schema.IsArray()) {
            // collections at root
            return CreateCollectionModelDeclaration(currentNode, schema, operation, codeNamespace, typeNameForInlineSchema, isRequestBody);
        }

        if(!string.IsNullOrEmpty(schema.Type) || !string.IsNullOrEmpty(schema.Format))
            return GetPrimitiveType(schema, string.Empty);
        if(schema.AnyOf.Any() || schema.OneOf.Any() || schema.AllOf.Any()) // we have an empty node because of some local override for schema properties and need to unwrap it.
            return CreateModelDeclarations(currentNode, schema.AnyOf.FirstOrDefault() ?? schema.OneOf.FirstOrDefault() ?? schema.AllOf.FirstOrDefault(), operation, parentElement, suffixForInlineSchema, response, typeNameForInlineSchema, isRequestBody);
        throw new InvalidSchemaException("unhandled case, might be object type or array type");
    }
    private CodeTypeBase CreateCollectionModelDeclaration(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, OpenApiOperation operation, CodeNamespace codeNamespace, string typeNameForInlineSchema, bool isRequestBody)
    {
        CodeTypeBase type = GetPrimitiveType(schema?.Items, string.Empty);
        bool isEnumOrComposedCollectionType =    (schema?.Items.IsEnum() ?? false) //the collection could be an enum type so override with strong type instead of string type.
                                    || ((schema?.Items.IsComposedEnum() ?? false) && string.IsNullOrEmpty(schema?.Items.Format));//the collection could be a composed type with an enum type so override with strong type instead of string type.
        if (   string.IsNullOrEmpty(type?.Name)
               || isEnumOrComposedCollectionType)
        {
            var targetNamespace = schema?.Items == null ? codeNamespace : GetShortestNamespace(codeNamespace, schema.Items);
            type = CreateModelDeclarations(currentNode, schema?.Items, operation, targetNamespace, default , typeNameForInlineSchema: typeNameForInlineSchema, isRequestBody: isRequestBody);
        }
        type.CollectionKind = CodeTypeBase.CodeTypeCollectionKind.Complex;
        return type;
    }
    private CodeElement GetExistingDeclaration(CodeNamespace currentNamespace, OpenApiUrlTreeNode currentNode, string declarationName) {
        var localNameSpace = GetSearchNamespace(currentNode, currentNamespace);
        return localNameSpace.FindChildByName<ITypeDefinition>(declarationName, false) as CodeElement;
    }
    private CodeNamespace GetSearchNamespace(OpenApiUrlTreeNode currentNode, CodeNamespace currentNamespace)
    {
        if (currentNode.DoesNodeBelongToItemSubnamespace() && !currentNamespace.Name.Contains(modelsNamespace.Name))
            return currentNamespace.EnsureItemNamespace();
        return currentNamespace;
    }
    private CodeElement AddModelDeclarationIfDoesntExist(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, string declarationName, CodeNamespace currentNamespace, CodeClass inheritsFrom = null) {
        var existingDeclaration = GetExistingDeclaration(currentNamespace, currentNode, declarationName);
        if(existingDeclaration == null) // we can find it in the components
        {
            if(schema.IsEnum()) {
                var newEnum = new CodeEnum {
                    Name = declarationName,//TODO set the flag property
                    Description = currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel),
                };
                SetEnumOptions(schema, newEnum);
                return currentNamespace.AddEnum(newEnum).First();
            }

            return AddModelClass(currentNode, schema, declarationName, currentNamespace, inheritsFrom);
        }

        return existingDeclaration;
    }
    private static void SetEnumOptions(OpenApiSchema schema, CodeEnum target) {
        OpenApiEnumValuesDescriptionExtension extensionInformation = null;
        if (schema.Extensions.TryGetValue(OpenApiEnumValuesDescriptionExtension.Name, out var rawExtension) && rawExtension is OpenApiEnumValuesDescriptionExtension localExtInfo)
            extensionInformation = localExtInfo;
        var entries = schema.Enum.OfType<OpenApiString>().Where(static x => !x.Value.Equals("null", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(x.Value)).Select(static x => x.Value);
        foreach(var enumValue in entries) {
            var optionDescription = extensionInformation?.ValuesDescriptions.FirstOrDefault(x => x.Value.Equals(enumValue, StringComparison.OrdinalIgnoreCase));
            var newOption = new CodeEnumOption {
                Name = (optionDescription?.Name ?? enumValue).CleanupSymbolName(),
                SerializationName = !string.IsNullOrEmpty(optionDescription?.Name) ? enumValue : null,
                Description = optionDescription?.Description,
            };
            if(!string.IsNullOrEmpty(newOption.Name))
                target.AddOption(newOption);
        }
    }
    private CodeNamespace GetShortestNamespace(CodeNamespace currentNamespace, OpenApiSchema currentSchema) {
        if(!string.IsNullOrEmpty(currentSchema.Reference?.Id)) {
            var parentClassNamespaceName = GetModelsNamespaceNameFromReferenceId(currentSchema.Reference.Id);
            return rootNamespace.AddNamespace(parentClassNamespaceName);
        }
        return currentNamespace;
    }
    private CodeClass AddModelClass(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, string declarationName, CodeNamespace currentNamespace, CodeClass inheritsFrom = null) {
        var referencedAllOfs = schema.AllOf.Where(x => x.Reference != null);
        if(inheritsFrom == null && referencedAllOfs.Any()) {// any non-reference would be the current class in some description styles
            var parentSchema = referencedAllOfs.FirstOrDefault();
            if(parentSchema != null) {
                var parentClassNamespace = GetShortestNamespace(currentNamespace, parentSchema);
                inheritsFrom = AddModelDeclarationIfDoesntExist(currentNode, parentSchema, parentSchema.GetSchemaName().CleanupSymbolName(), parentClassNamespace) as CodeClass;
            }
        }
        var newClass = currentNamespace.AddClass(new CodeClass {
            Name = declarationName,
            Kind = CodeClassKind.Model,
            Description = schema.Description.CleanupDescription() ?? (string.IsNullOrEmpty(schema.Reference?.Id) ?
                                                    currentNode.GetPathItemDescription(Constants.DefaultOpenApiLabel) :
                                                    null),// if it's a referenced component, we shouldn't use the path item description as it makes it indeterministic
        }).First();
        if(inheritsFrom != null)
            newClass.StartBlock.Inherits = new CodeType { TypeDefinition = inheritsFrom, Name = inheritsFrom.Name };
        CreatePropertiesForModelClass(currentNode, schema, currentNamespace, newClass); // order matters since we might be recursively generating ancestors for discriminator mappings and duplicating additional data/backing store properties

        var mappings = GetDiscriminatorMappings(currentNode, schema, currentNamespace, newClass)
                        .Where(x => x.Value is CodeType type &&
                                    type.TypeDefinition != null &&
                                    type.TypeDefinition is CodeClass definition &&
                                    definition.DerivesFrom(newClass)); // only the mappings that derive from the current class

        AddDiscriminatorMethod(newClass, GetDiscriminatorPropertyName(schema), mappings);
        return newClass;
    }
    private static string GetDiscriminatorPropertyName(OpenApiSchema schema) {
        if(schema == null)
            return string.Empty;

        if (!string.IsNullOrEmpty(schema.Discriminator?.PropertyName))
            return schema.Discriminator.PropertyName;

        if(schema.OneOf.Any())
            return schema.OneOf.Select(static x => GetDiscriminatorPropertyName(x)).FirstOrDefault(static x => !string.IsNullOrEmpty(x));
        if (schema.AnyOf.Any())
            return schema.AnyOf.Select(static x => GetDiscriminatorPropertyName(x)).FirstOrDefault(static x => !string.IsNullOrEmpty(x));
        if (schema.AllOf.Any())
            return GetDiscriminatorPropertyName(schema.AllOf.Last());

        return string.Empty;
    }
    private static readonly Func<OpenApiSchema, bool> allOfEvaluatorForMappings = static x => x.Discriminator?.Mapping.Any() ?? false;
    private IEnumerable<KeyValuePair<string, CodeTypeBase>> GetDiscriminatorMappings(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, CodeNamespace currentNamespace, CodeClass baseClass) {
        if(schema == null)
            return Enumerable.Empty<KeyValuePair<string, CodeTypeBase>>();
        if(!(schema.Discriminator?.Mapping?.Any() ?? false))
            if(schema.OneOf.Any())
                return schema.OneOf.SelectMany(x => GetDiscriminatorMappings(currentNode, x, currentNamespace, baseClass));
            else if (schema.AnyOf.Any())
                return schema.AnyOf.SelectMany(x => GetDiscriminatorMappings(currentNode, x, currentNamespace, baseClass));
            else if (schema.AllOf.Any(allOfEvaluatorForMappings) && schema.AllOf.Last().Equals(schema.AllOf.Last(allOfEvaluatorForMappings)))
                // ensure the matched AllOf entry is the last in the list
                return GetDiscriminatorMappings(currentNode, schema.AllOf.Last(allOfEvaluatorForMappings), currentNamespace, baseClass);
            else if (!string.IsNullOrEmpty(schema.Reference?.Id)) {
                var result = GetAllInheritanceSchemaReferences(schema.Reference?.Id)
                            .Select(x => KeyValuePair.Create(x, GetCodeTypeForMapping(currentNode, x, currentNamespace, baseClass, schema)))
                            .Where(x => x.Value != null)
                            .ToList();
                if(GetCodeTypeForMapping(currentNode, schema.Reference?.Id, currentNamespace, baseClass, schema) is CodeTypeBase currentType)
                    result.Add(KeyValuePair.Create(schema.Reference?.Id, currentType));
                return result;
            } else
                return Enumerable.Empty<KeyValuePair<string, CodeTypeBase>>();

        return schema.Discriminator
                .Mapping
                .Select(x => KeyValuePair.Create(x.Key, GetCodeTypeForMapping(currentNode, x.Value, currentNamespace, baseClass, schema)))
                .Where(static x => x.Value != null);
    }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> inheritanceIndex = new ();
    private void InitializeInheritanceIndex() {
        if(!inheritanceIndex.Any() && openApiDocument?.Components?.Schemas != null) {
            Parallel.ForEach(openApiDocument.Components.Schemas, entry => {
                inheritanceIndex.TryAdd(entry.Key, new(StringComparer.OrdinalIgnoreCase));
                if(entry.Value.AllOf != null)
                    foreach(var allOfEntry in entry.Value.AllOf.Where(static x => !string.IsNullOrEmpty(x.Reference?.Id))) {
                        var dependents = inheritanceIndex.GetOrAdd(allOfEntry.Reference.Id, new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase));
                        dependents.TryAdd(entry.Key, false);
                    }
            });
        }
    }
    private IEnumerable<string> GetAllInheritanceSchemaReferences(string currentReferenceId)
    {
        if (inheritanceIndex.TryGetValue(currentReferenceId, out var dependents))
            return dependents.Keys.Union(dependents.Keys.SelectMany(x => GetAllInheritanceSchemaReferences(x))).Distinct(StringComparer.OrdinalIgnoreCase);
        return Enumerable.Empty<string>();
    }
    public static void AddDiscriminatorMethod(CodeClass newClass, string discriminatorPropertyName, IEnumerable<KeyValuePair<string, CodeTypeBase>> discriminatorMappings) {
        var factoryMethod = new CodeMethod {
            Name = "CreateFromDiscriminatorValue",
            Description = "Creates a new instance of the appropriate class based on discriminator value",
            ReturnType = new CodeType { TypeDefinition = newClass, Name = newClass.Name, IsNullable = false },
            Kind = CodeMethodKind.Factory,
            IsStatic = true,
            IsAsync = false,
            Parent = newClass,
        };
        discriminatorMappings?.ToList()
                .ForEach(x => newClass.DiscriminatorInformation.AddDiscriminatorMapping(x.Key, x.Value));
        factoryMethod.AddParameter(new CodeParameter {
            Name = "parseNode",
            Kind = CodeParameterKind.ParseNode,
            Description = "The parse node to use to read the discriminator value and create the object",
            Optional = false,
            Type = new CodeType { Name = ParseNodeInterface, IsExternal = true },
        });
        newClass.DiscriminatorInformation.DiscriminatorPropertyName = discriminatorPropertyName;
        newClass.AddMethod(factoryMethod);
    }
    private CodeTypeBase GetCodeTypeForMapping(OpenApiUrlTreeNode currentNode, string referenceId, CodeNamespace currentNamespace, CodeClass baseClass, OpenApiSchema currentSchema) {
        var componentKey = referenceId?.Replace("#/components/schemas/", string.Empty);
        if(!openApiDocument.Components.Schemas.TryGetValue(componentKey, out var discriminatorSchema)) {
            logger.LogWarning("Discriminator {componentKey} not found in the OpenAPI document.", componentKey);
            return null;
        }
        var className = currentNode.GetClassName(config.StructuredMimeTypes, schema: discriminatorSchema).CleanupSymbolName();
        var shouldInherit = discriminatorSchema.AllOf.Any(x => currentSchema.Reference?.Id.Equals(x.Reference?.Id, StringComparison.OrdinalIgnoreCase) ?? false);
        var codeClass = AddModelDeclarationIfDoesntExist(currentNode, discriminatorSchema, className, GetShortestNamespace(currentNamespace, discriminatorSchema), shouldInherit ? baseClass : null);
        return new CodeType {
            Name = codeClass.Name,
            TypeDefinition = codeClass,
        };
    }
    private void CreatePropertiesForModelClass(OpenApiUrlTreeNode currentNode, OpenApiSchema schema, CodeNamespace ns, CodeClass model) {

        var includeAdditionalDataProperties = config.IncludeAdditionalData &&
            (schema?.AdditionalPropertiesAllowed ?? false);

        AddSerializationMembers(model, includeAdditionalDataProperties, config.UsesBackingStore);
        if(schema?.Properties?.Any() ?? false)
        {
            model.AddProperty(schema
                                .Properties
                                .Select(x => {
                                    var propertySchema = x.Value;
                                    var className = propertySchema.GetSchemaName().CleanupSymbolName();
                                    if(string.IsNullOrEmpty(className))
                                        className = $"{model.Name}_{x.Key}";
                                    var shortestNamespaceName = GetModelsNamespaceNameFromReferenceId(propertySchema.Reference?.Id);
                                    var targetNamespace = string.IsNullOrEmpty(shortestNamespaceName) ? ns :
                                                        rootNamespace.FindOrAddNamespace(shortestNamespaceName);
                                    #if RELEASE
                                    try {
                                    #endif
                                        var definition = CreateModelDeclarations(currentNode, propertySchema, default, targetNamespace, default, typeNameForInlineSchema: className);
                                        return CreateProperty(x.Key, definition.Name, typeSchema: propertySchema, existingType: definition);
                                    #if RELEASE
                                    } catch (InvalidSchemaException ex) {
                                        throw new InvalidOperationException($"Error creating property {x.Key} for model {model.Name} in API path {currentNode.Path}, the schema is invalid.", ex);
                                    }
                                    #endif
                                })
                                .ToArray());
        }
        else if(schema?.AllOf?.Any(x => x.IsObject()) ?? false)
            CreatePropertiesForModelClass(currentNode, schema.AllOf.Last(x => x.IsObject()), ns, model);
    }
    private const string FieldDeserializersMethodName = "GetFieldDeserializers";
    private const string SerializeMethodName = "Serialize";
    private const string AdditionalDataPropName = "AdditionalData";
    private const string BackingStorePropertyName = "BackingStore";
    private const string BackingStoreInterface = "IBackingStore";
    private const string BackedModelInterface = "IBackedModel";
    private const string ParseNodeInterface = "IParseNode";
    internal const string AdditionalHolderInterface = "IAdditionalDataHolder";
    internal static void AddSerializationMembers(CodeClass model, bool includeAdditionalProperties, bool usesBackingStore) {
        var serializationPropsType = $"IDictionary<string, Action<{ParseNodeInterface}>>";
        if(!model.ContainsMember(FieldDeserializersMethodName)) {
            var deserializeProp = new CodeMethod {
                Name = FieldDeserializersMethodName,
                Kind = CodeMethodKind.Deserializer,
                Access = AccessModifier.Public,
                Description = "The deserialization information for the current model",
                IsAsync = false,
                ReturnType = new CodeType {
                    Name = serializationPropsType,
                    IsNullable = false,
                    IsExternal = true,
                },
                Parent = model,
            };
            model.AddMethod(deserializeProp);
        }
        if(!model.ContainsMember(SerializeMethodName)) {
            var serializeMethod = new CodeMethod {
                Name = SerializeMethodName,
                Kind = CodeMethodKind.Serializer,
                IsAsync = false,
                Description = "Serializes information the current object",
                ReturnType = new CodeType { Name = voidType, IsNullable = false, IsExternal = true },
                Parent = model,
            };
            var parameter = new CodeParameter {
                Name = "writer",
                Description = "Serialization writer to use to serialize this model",
                Kind = CodeParameterKind.Serializer,
                Type = new CodeType { Name = "ISerializationWriter", IsExternal = true, IsNullable = false },
            };
            serializeMethod.AddParameter(parameter);

            model.AddMethod(serializeMethod);
        }
        if(!model.ContainsMember(AdditionalDataPropName) &&
            includeAdditionalProperties &&
            !(model.GetGreatestGrandparent(model)?.ContainsMember(AdditionalDataPropName) ?? false)) {
            // we don't want to add the property if the parent already has it
            var additionalDataProp = new CodeProperty {
                Name = AdditionalDataPropName,
                Access = AccessModifier.Public,
                DefaultValue = "new Dictionary<string, object>()",
                Kind = CodePropertyKind.AdditionalData,
                Description = "Stores additional data not described in the OpenAPI description found when deserializing. Can be used for serialization as well.",
                Type = new CodeType {
                    Name = "IDictionary<string, object>",
                    IsNullable = false,
                    IsExternal = true,
                },
            };
            model.AddProperty(additionalDataProp);
            model.StartBlock.AddImplements(new CodeType {
                Name = AdditionalHolderInterface,
                IsExternal = true,
            });
        }
        if(!model.ContainsMember(BackingStorePropertyName) &&
            usesBackingStore &&
            !(model.GetGreatestGrandparent(model)?.ContainsMember(BackingStorePropertyName) ?? false)) {
            var backingStoreProperty = new CodeProperty {
                Name = BackingStorePropertyName,
                Access = AccessModifier.Public,
                DefaultValue = "BackingStoreFactorySingleton.Instance.CreateBackingStore()",
                Kind = CodePropertyKind.BackingStore,
                Description = "Stores model information.",
                ReadOnly = true,
                Type = new CodeType {
                    Name = BackingStoreInterface,
                    IsNullable = false,
                    IsExternal = true,
                },
            };
            model.AddProperty(backingStoreProperty);
            model.StartBlock.AddImplements(new CodeType {
                Name = BackedModelInterface,
                IsExternal = true,
            });
        }
    }
    private CodeClass CreateOperationParameterClass(OpenApiUrlTreeNode node, OperationType operationType, OpenApiOperation operation, CodeClass parentClass)
    {
        var parameters = node.PathItems[Constants.DefaultOpenApiLabel].Parameters.Union(operation.Parameters).Where(static p => p.In == ParameterLocation.Query);
        if(parameters.Any()) {
            var parameterClass = parentClass.AddInnerClass(new CodeClass
            {
                Name = $"{parentClass.Name}{operationType}QueryParameters",
                Kind = CodeClassKind.QueryParameters,
                Description = (operation.Description ?? operation.Summary).CleanupDescription(),
            }).First();
            foreach (var parameter in parameters)
                AddPropertyForParameter(parameter, parameterClass);

            return parameterClass;
        }

        return null;
    }
    private void AddPropertyForParameter(OpenApiParameter parameter, CodeClass parameterClass) {
        var prop = new CodeProperty
        {
            Name = parameter.Name.SanitizeParameterNameForCodeSymbols(),
            Description = parameter.Description.CleanupDescription(),
            Kind = CodePropertyKind.QueryParameter,
            Type = GetPrimitiveType(parameter.Schema),
        };
        prop.Type.CollectionKind = parameter.Schema.IsArray() ? CodeTypeBase.CodeTypeCollectionKind.Array : default;
        if(string.IsNullOrEmpty(prop.Type.Name) && prop.Type is CodeType parameterType) {
            // since its a query parameter default to string if there is no schema
            // it also be an object type, but we'd need to create the model in that case and there's no standard on how to serialize those as query parameters
            parameterType.Name = "string";
            parameterType.IsExternal = true;
        }

        if(!parameter.Name.Equals(prop.Name))
        {
            prop.SerializationName = parameter.Name.SanitizeParameterNameForUrlTemplate();
        }

        if (!parameterClass.ContainsMember(parameter.Name))
        {
            parameterClass.AddProperty(prop);
        }
        else
        {
            logger.LogWarning("Ignoring duplicate parameter {name}", parameter.Name);
        }
    }
    private static CodeType GetQueryParameterType(OpenApiSchema schema) =>
        new()
        {
            IsExternal = true,
            Name = schema.Items?.Type ?? schema.Type,
            CollectionKind = schema.IsArray() ? CodeTypeBase.CodeTypeCollectionKind.Array : default,
        };
}
