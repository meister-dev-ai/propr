// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.CodeAnalysis.ProCursor;

/// <summary>
///     Roslyn-backed symbol extractor for C# ProCursor snapshots.
/// </summary>
public sealed class RoslynProCursorSymbolExtractor(ILogger<RoslynProCursorSymbolExtractor> logger)
    : IProCursorSymbolExtractor
{
    private static readonly IReadOnlyList<MetadataReference> MetadataReferences = BuildMetadataReferences();

    /// <inheritdoc />
    public async Task<ProCursorSymbolExtractionResult> ExtractAsync(
        ProCursorMaterializedSource materializedSource,
        Guid snapshotId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(materializedSource);

        if (snapshotId == Guid.Empty)
        {
            throw new ArgumentException("SnapshotId must not be empty.", nameof(snapshotId));
        }

        var sourceFiles = materializedSource.MaterializedPaths
            .Where(path => string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase))
            .Select(path => new MaterializedSourceFile(
                NormalizeRepositoryPath(path),
                Path.Combine(
                    materializedSource.RootDirectory,
                    path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar))))
            .Where(file => File.Exists(file.AbsolutePath))
            .ToList();

        if (sourceFiles.Count == 0)
        {
            return new ProCursorSymbolExtractionResult([], [], false, "unsupported_language");
        }

        var syntaxTrees = new List<SyntaxTree>(sourceFiles.Count);
        var repositoryPathByTree = new Dictionary<SyntaxTree, string>();

        foreach (var sourceFile in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();

            var content = await File.ReadAllTextAsync(sourceFile.AbsolutePath, ct);
            var syntaxTree = CSharpSyntaxTree.ParseText(
                content,
                new CSharpParseOptions(LanguageVersion.Latest),
                sourceFile.AbsolutePath);

            syntaxTrees.Add(syntaxTree);
            repositoryPathByTree[syntaxTree] = sourceFile.RepositoryPath;
        }

        var compilation = CSharpCompilation.Create(
            $"ProCursor_{snapshotId:N}",
            syntaxTrees,
            MetadataReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var symbolsByKey = new Dictionary<string, ProCursorSymbolRecord>(StringComparer.Ordinal);
        var keyBySymbol = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
        var edgesByKey = new Dictionary<string, ProCursorSymbolEdge>(StringComparer.Ordinal);

        foreach (var syntaxTree in syntaxTrees)
        {
            ct.ThrowIfCancellationRequested();

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(ct);
            var repositoryPath = repositoryPathByTree[syntaxTree];

            this.CollectDeclaredSymbols(
                snapshotId,
                semanticModel,
                root,
                repositoryPath,
                symbolsByKey,
                keyBySymbol,
                edgesByKey);
        }

        foreach (var syntaxTree in syntaxTrees)
        {
            ct.ThrowIfCancellationRequested();

            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(ct);
            var repositoryPath = repositoryPathByTree[syntaxTree];

            this.CollectRelationships(snapshotId, semanticModel, root, repositoryPath, keyBySymbol, edgesByKey);
        }

        logger.LogInformation(
            "Extracted {SymbolCount} ProCursor symbols and {EdgeCount} symbol edges from {FileCount} C# files.",
            symbolsByKey.Count,
            edgesByKey.Count,
            sourceFiles.Count);

        return new ProCursorSymbolExtractionResult(
            symbolsByKey.Values.ToList().AsReadOnly(),
            edgesByKey.Values.ToList().AsReadOnly(),
            true);
    }

    private void CollectDeclaredSymbols(
        Guid snapshotId,
        SemanticModel semanticModel,
        SyntaxNode root,
        string repositoryPath,
        IDictionary<string, ProCursorSymbolRecord> symbolsByKey,
        IDictionary<ISymbol, string> keyBySymbol,
        IDictionary<string, ProCursorSymbolEdge> edgesByKey)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            var declaredSymbol = GetDeclaredSymbol(semanticModel, node);
            if (!TryCreateSymbolRecord(snapshotId, declaredSymbol, repositoryPath, out var symbolRecord))
            {
                continue;
            }

            if (!symbolsByKey.ContainsKey(symbolRecord.SymbolKey))
            {
                symbolsByKey[symbolRecord.SymbolKey] = symbolRecord;
                keyBySymbol[Canonicalize(declaredSymbol!)] = symbolRecord.SymbolKey;

                if (!string.IsNullOrWhiteSpace(symbolRecord.ContainingSymbolKey))
                {
                    AddEdge(
                        snapshotId,
                        symbolRecord.ContainingSymbolKey,
                        symbolRecord.SymbolKey,
                        "containment",
                        repositoryPath,
                        declaredSymbol!.Locations.First(location => location.IsInSource).GetLineSpan(),
                        edgesByKey);
                }
            }
        }
    }

    private void CollectRelationships(
        Guid snapshotId,
        SemanticModel semanticModel,
        SyntaxNode root,
        string repositoryPath,
        IReadOnlyDictionary<ISymbol, string> keyBySymbol,
        IDictionary<string, ProCursorSymbolEdge> edgesByKey)
    {
        foreach (var baseType in root.DescendantNodes().OfType<BaseTypeSyntax>())
        {
            if (baseType.Parent?.Parent is not TypeDeclarationSyntax typeDeclaration)
            {
                continue;
            }

            var fromSymbol = semanticModel.GetDeclaredSymbol(typeDeclaration);
            var toSymbol = semanticModel.GetSymbolInfo(baseType.Type).Symbol as INamedTypeSymbol;
            if (!TryGetSymbolKey(fromSymbol, keyBySymbol, out var fromKey) ||
                !TryGetSymbolKey(toSymbol, keyBySymbol, out var toKey))
            {
                continue;
            }

            var relationKind = toSymbol!.TypeKind == TypeKind.Interface &&
                               typeDeclaration.Kind() != SyntaxKind.InterfaceDeclaration
                ? "implementation"
                : "inheritance";
            AddEdge(
                snapshotId,
                fromKey,
                toKey,
                relationKind,
                repositoryPath,
                baseType.GetLocation().GetLineSpan(),
                edgesByKey);
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var fromSymbol = semanticModel.GetEnclosingSymbol(invocation.SpanStart);
            var toSymbol = semanticModel.GetSymbolInfo(invocation).Symbol;
            if (!TryGetSymbolKey(fromSymbol, keyBySymbol, out var fromKey) ||
                !TryGetSymbolKey(toSymbol, keyBySymbol, out var toKey))
            {
                continue;
            }

            AddEdge(
                snapshotId,
                fromKey,
                toKey,
                "call",
                repositoryPath,
                invocation.GetLocation().GetLineSpan(),
                edgesByKey);
        }

        foreach (var referenceNode in root.DescendantNodes().Where(IsReferenceCandidate))
        {
            if (IsInvocationTarget(referenceNode))
            {
                continue;
            }

            var fromSymbol = semanticModel.GetEnclosingSymbol(referenceNode.SpanStart);
            var toSymbol = semanticModel.GetSymbolInfo(referenceNode).Symbol;
            if (!TryGetSymbolKey(fromSymbol, keyBySymbol, out var fromKey) ||
                !TryGetSymbolKey(toSymbol, keyBySymbol, out var toKey) ||
                string.Equals(fromKey, toKey, StringComparison.Ordinal))
            {
                continue;
            }

            AddEdge(
                snapshotId,
                fromKey,
                toKey,
                "reference",
                repositoryPath,
                referenceNode.GetLocation().GetLineSpan(),
                edgesByKey);
        }
    }

    private static bool TryCreateSymbolRecord(
        Guid snapshotId,
        ISymbol? symbol,
        string repositoryPath,
        out ProCursorSymbolRecord symbolRecord)
    {
        symbolRecord = null!;

        if (!TryGetSupportedSymbol(symbol, out var supportedSymbol, out var symbolKind))
        {
            return false;
        }

        var location = supportedSymbol.Locations.FirstOrDefault(current => current.IsInSource);
        if (location is null)
        {
            return false;
        }

        var lineSpan = location.GetLineSpan();
        var symbolKey = BuildSymbolKey(supportedSymbol);
        if (string.IsNullOrWhiteSpace(symbolKey))
        {
            return false;
        }

        symbolRecord = new ProCursorSymbolRecord(
            Guid.NewGuid(),
            snapshotId,
            "csharp",
            symbolKey,
            BuildDisplayName(supportedSymbol),
            symbolKind,
            TryGetContainingSymbolKey(supportedSymbol),
            repositoryPath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.EndLinePosition.Line + 1,
            supportedSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            BuildSearchText(supportedSymbol, repositoryPath));
        return true;
    }

    private static bool TryGetSymbolKey(
        ISymbol? symbol,
        IReadOnlyDictionary<ISymbol, string> keyBySymbol,
        out string symbolKey)
    {
        symbolKey = string.Empty;
        if (symbol is null || !TryGetSupportedSymbol(symbol, out var supportedSymbol, out _))
        {
            return false;
        }

        if (!keyBySymbol.TryGetValue(Canonicalize(supportedSymbol), out var resolvedSymbolKey) ||
            string.IsNullOrWhiteSpace(resolvedSymbolKey))
        {
            return false;
        }

        symbolKey = resolvedSymbolKey;
        return true;
    }

    private static void AddEdge(
        Guid snapshotId,
        string fromSymbolKey,
        string toSymbolKey,
        string edgeKind,
        string repositoryPath,
        FileLinePositionSpan lineSpan,
        IDictionary<string, ProCursorSymbolEdge> edgesByKey)
    {
        var lineStart = lineSpan.StartLinePosition.Line + 1;
        var lineEnd = lineSpan.EndLinePosition.Line + 1;
        var key = string.Join(
            "|",
            snapshotId.ToString("N"),
            fromSymbolKey,
            toSymbolKey,
            edgeKind,
            repositoryPath,
            lineStart,
            lineEnd);

        if (edgesByKey.ContainsKey(key))
        {
            return;
        }

        edgesByKey[key] = new ProCursorSymbolEdge(
            Guid.NewGuid(),
            snapshotId,
            fromSymbolKey,
            toSymbolKey,
            edgeKind,
            repositoryPath,
            lineStart,
            lineEnd);
    }

    private static bool IsReferenceCandidate(SyntaxNode node)
    {
        return node is IdentifierNameSyntax or MemberAccessExpressionSyntax or ObjectCreationExpressionSyntax;
    }

    private static bool IsInvocationTarget(SyntaxNode node)
    {
        return node.Parent is InvocationExpressionSyntax invocation && ReferenceEquals(invocation.Expression, node);
    }

    private static ISymbol? GetDeclaredSymbol(SemanticModel semanticModel, SyntaxNode node)
    {
        return node switch
        {
            BaseNamespaceDeclarationSyntax namespaceDeclaration =>
                semanticModel.GetDeclaredSymbol(namespaceDeclaration),
            BaseTypeDeclarationSyntax typeDeclaration => semanticModel.GetDeclaredSymbol(typeDeclaration),
            DelegateDeclarationSyntax delegateDeclaration => semanticModel.GetDeclaredSymbol(delegateDeclaration),
            MethodDeclarationSyntax methodDeclaration => semanticModel.GetDeclaredSymbol(methodDeclaration),
            ConstructorDeclarationSyntax constructorDeclaration => semanticModel.GetDeclaredSymbol(constructorDeclaration),
            DestructorDeclarationSyntax destructorDeclaration => semanticModel.GetDeclaredSymbol(destructorDeclaration),
            PropertyDeclarationSyntax propertyDeclaration => semanticModel.GetDeclaredSymbol(propertyDeclaration),
            EventDeclarationSyntax eventDeclaration => semanticModel.GetDeclaredSymbol(eventDeclaration),
            IndexerDeclarationSyntax indexerDeclaration => semanticModel.GetDeclaredSymbol(indexerDeclaration),
            VariableDeclaratorSyntax variableDeclarator when variableDeclarator.Parent?.Parent is FieldDeclarationSyntax
                    or EventFieldDeclarationSyntax
                => semanticModel.GetDeclaredSymbol(variableDeclarator),
            _ => null,
        };
    }

    private static bool TryGetSupportedSymbol(ISymbol? symbol, out ISymbol supportedSymbol, out string symbolKind)
    {
        supportedSymbol = null!;
        symbolKind = string.Empty;

        if (symbol is null)
        {
            return false;
        }

        symbol = Canonicalize(symbol);
        switch (symbol)
        {
            case INamespaceSymbol namespaceSymbol when !namespaceSymbol.IsGlobalNamespace:
                supportedSymbol = namespaceSymbol;
                symbolKind = "namespace";
                return true;
            case INamedTypeSymbol:
                supportedSymbol = symbol;
                symbolKind = "type";
                return true;
            case IMethodSymbol methodSymbol
                when methodSymbol.MethodKind is MethodKind.Ordinary or MethodKind.Constructor
                    or MethodKind.StaticConstructor:
                supportedSymbol = methodSymbol;
                symbolKind = "method";
                return true;
            case IPropertySymbol:
                supportedSymbol = symbol;
                symbolKind = "property";
                return true;
            case IFieldSymbol fieldSymbol when !fieldSymbol.IsImplicitlyDeclared:
                supportedSymbol = fieldSymbol;
                symbolKind = "field";
                return true;
            case IEventSymbol:
                supportedSymbol = symbol;
                symbolKind = "event";
                return true;
            default:
                return false;
        }
    }

    private static ISymbol Canonicalize(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol methodSymbol => methodSymbol.OriginalDefinition,
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol.OriginalDefinition,
            _ => symbol,
        };
    }

    private static string NormalizeRepositoryPath(string path)
    {
        return path.TrimStart('/').Replace('\\', '/');
    }

    private static string BuildSymbolKey(ISymbol symbol)
    {
        return symbol.GetDocumentationCommentId()
               ?? symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    }

    private static string BuildDisplayName(ISymbol symbol)
    {
        return symbol switch
        {
            IMethodSymbol methodSymbol when methodSymbol.MethodKind is MethodKind.Constructor
                    or MethodKind.StaticConstructor
                => methodSymbol.ContainingType.Name,
            _ => symbol.Name,
        };
    }

    private static string? TryGetContainingSymbolKey(ISymbol symbol)
    {
        if (symbol.ContainingSymbol is null || !TryGetSupportedSymbol(
                symbol.ContainingSymbol,
                out var containingSymbol,
                out _))
        {
            return null;
        }

        return BuildSymbolKey(containingSymbol);
    }

    private static string BuildSearchText(ISymbol symbol, string repositoryPath)
    {
        return string.Join(
            ' ',
            new[]
            {
                BuildDisplayName(symbol),
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                BuildSymbolKey(symbol),
                repositoryPath,
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static IReadOnlyList<MetadataReference> BuildMetadataReferences()
    {
        var trustedPlatformAssemblies =
            (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string)?.Split(Path.PathSeparator)
            ?? [];

        return trustedPlatformAssemblies
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToList()
            .AsReadOnly();
    }

    private sealed record MaterializedSourceFile(string RepositoryPath, string AbsolutePath);
}
