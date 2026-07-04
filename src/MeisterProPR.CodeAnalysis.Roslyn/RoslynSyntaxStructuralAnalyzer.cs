// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Options;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.CodeAnalysis.Roslyn;

/// <summary>
///     Roslyn-syntax backend for <see cref="IStructuralCodeAnalyzer" />. Parses C#
///     per-file via <see cref="CSharpSyntaxTree.ParseText(string, CSharpParseOptions, string, Encoding, CancellationToken)" />.
/// </summary>
/// <remarks>
///     Invariants mirror the Tree-sitter backend: filesystem-free (caller supplies source), never
///     throws for input/parse problems (returns empty), bounded by <c>MaxStructuralParseBytes</c> and
///     <c>StructuralParseTimeoutMs</c>. <see cref="IsAvailable" /> is always true (managed).
/// </remarks>
public sealed class RoslynSyntaxStructuralAnalyzer : IStructuralCodeAnalyzer
{
    private readonly ILogger<RoslynSyntaxStructuralAnalyzer> _logger;
    private readonly IOptions<AiReviewOptions> _options;

    /// <summary>Creates the Roslyn-syntax analyzer.</summary>
    public RoslynSyntaxStructuralAnalyzer(
        IOptions<AiReviewOptions> options,
        ILogger<RoslynSyntaxStructuralAnalyzer> logger)
    {
        this._options = options;
        this._logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public bool CanAnalyze(string path)
    {
        return !string.IsNullOrEmpty(path)
               && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EnclosingDefinition>> ResolveEnclosingDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SourceText) || request.ChangedLineRanges.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
        }

        var root = this.TryParse(request.SourceText, request.Path, ct);
        if (root is null)
        {
            return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
        }

        try
        {
            var totalLines = root.GetText().Lines.Count;
            var declarations = CollectDeclarations(root);
            if (declarations.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
            }

            var enclosing = new List<EnclosingDefinition>();
            var seen = new HashSet<(int, int, string?)>();

            foreach (var range in request.ChangedLineRanges)
            {
                if (range.IsEmpty)
                {
                    continue;
                }

                CollectEnclosingForRange(range, declarations, totalLines, seen, enclosing);
            }

            enclosing.Sort(static (a, b) =>
            {
                var byStart = a.StartLine.CompareTo(b.StartLine);
                return byStart != 0 ? byStart : a.EndLine.CompareTo(b.EndLine);
            });

            return Task.FromResult<IReadOnlyList<EnclosingDefinition>>(enclosing);
        }
        catch (Exception ex)
        {
            this.LogFault(request.Path, ex);
            return Task.FromResult<IReadOnlyList<EnclosingDefinition>>([]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DefinitionSummary>> GetDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SourceText))
        {
            return Task.FromResult<IReadOnlyList<DefinitionSummary>>([]);
        }

        var root = this.TryParse(request.SourceText, request.Path, ct);
        if (root is null)
        {
            return Task.FromResult<IReadOnlyList<DefinitionSummary>>([]);
        }

        try
        {
            var results = CollectDeclarations(root)
                .Select(d => new DefinitionSummary(d.Kind, d.Name, d.StartLine, d.EndLine))
                .OrderBy(static d => d.StartLine)
                .ThenBy(static d => d.EndLine)
                .ToList();

            return Task.FromResult<IReadOnlyList<DefinitionSummary>>(results);
        }
        catch (Exception ex)
        {
            this.LogFault(request.Path, ex);
            return Task.FromResult<IReadOnlyList<DefinitionSummary>>([]);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ConfirmReferenceLinesAsync(
        StructuralParseRequest request,
        string symbol,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SourceText) || string.IsNullOrEmpty(symbol))
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        var root = this.TryParse(request.SourceText, request.Path, ct);
        if (root is null)
        {
            return Task.FromResult<IReadOnlyList<int>>([]);
        }

        try
        {
            var lines = new SortedSet<int>();

            // Identifier tokens are the real references. Tokens inside comments are trivia (not tokens),
            // and string/char/verbatim/interpolated text is a single literal token (not an IdentifierToken),
            // so iterating identifier tokens inherently excludes comment/string occurrences.
            // Identifier holes inside interpolated strings ARE IdentifierTokens and correctly count.
            foreach (var token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken)
                    && string.Equals(token.ValueText, symbol, StringComparison.Ordinal))
                {
                    lines.Add(token.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                }
            }

            return Task.FromResult<IReadOnlyList<int>>(lines.Count == 0 ? [] : lines.ToArray());
        }
        catch (Exception ex)
        {
            this.LogFault(request.Path, ex);
            return Task.FromResult<IReadOnlyList<int>>([]);
        }
    }

    /// <inheritdoc />
    public Task<string> ExtractCodeTextAsync(StructuralParseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.SourceText))
        {
            return Task.FromResult(string.Empty);
        }

        var root = this.TryParse(request.SourceText, request.Path, ct);
        if (root is null)
        {
            return Task.FromResult(string.Empty);
        }

        try
        {
            var chars = request.SourceText.ToCharArray();

            // Blank string/char/raw/utf8/interpolated-text literal tokens. Interpolation holes (`{x}`) are
            // separate tokens and stay as code. Newlines are preserved so line numbers are unchanged.
            var literalTokens = root.DescendantTokens().Where(token =>
                token.IsKind(SyntaxKind.StringLiteralToken)
                || token.IsKind(SyntaxKind.CharacterLiteralToken)
                || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.Utf8StringLiteralToken)
                || token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
                || token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken));
            foreach (var token in literalTokens)
            {
                BlankSpan(chars, token.Span);
            }

            // Comments are trivia, not tokens.
            var commentTrivia = root.DescendantTrivia().Where(trivia =>
                trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
            foreach (var trivia in commentTrivia)
            {
                BlankSpan(chars, trivia.Span);
            }

            return Task.FromResult(new string(chars));
        }
        catch (ArgumentException ex)
        {
            this.LogFault(request.Path, ex);
            return Task.FromResult(string.Empty);
        }
        catch (InvalidOperationException ex)
        {
            this.LogFault(request.Path, ex);
            return Task.FromResult(string.Empty);
        }
    }

    private static void CollectEnclosingForRange(
        ChangedLineRange range,
        List<DeclarationInfo> declarations,
        int totalLines,
        HashSet<(int, int, string?)> seen,
        List<EnclosingDefinition> enclosing)
    {
        // 1-based inclusive overlap.
        var overlapping = declarations
            .Where(d => d.StartLine <= range.End && d.EndLine >= range.Start)
            .ToList();

        // Keep only the innermost: drop any declaration that strictly contains another overlapping one.
        foreach (var candidate in overlapping)
        {
            if (IsStrictlyOuterDeclaration(candidate, overlapping))
            {
                continue;
            }

            if (seen.Add((candidate.StartLine, candidate.EndLine, candidate.Name)))
            {
                enclosing.Add(new EnclosingDefinition(candidate.Kind, candidate.Name, candidate.StartLine, candidate.EndLine, totalLines));
            }
        }
    }

    private static bool IsStrictlyOuterDeclaration(DeclarationInfo candidate, List<DeclarationInfo> overlapping)
    {
        return overlapping.Any(other =>
            !ReferenceEquals(other.Node, candidate.Node)
            && other.StartLine >= candidate.StartLine
            && other.EndLine <= candidate.EndLine
            && (other.StartLine != candidate.StartLine || other.EndLine != candidate.EndLine));
    }

    private static void BlankSpan(char[] chars, TextSpan span)
    {
        var end = Math.Min(chars.Length, span.End);
        for (var i = Math.Max(0, span.Start); i < end; i++)
        {
            if (chars[i] != '\n')
            {
                chars[i] = ' ';
            }
        }
    }

    private SyntaxNode? TryParse(string source, string path, CancellationToken ct)
    {
        var options = this._options.Value;

        int byteCount;
        try
        {
            byteCount = Encoding.UTF8.GetByteCount(source);
        }
        catch (Exception ex)
        {
            this.LogFault(path, ex);
            return null;
        }

        // Size guard. Mirrors the Tree-sitter ParserPool bound so C# is bounded identically.
        if (byteCount > options.MaxStructuralParseBytes)
        {
            return null;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(Math.Max(1, options.StructuralParseTimeoutMs));

        try
        {
            var tree = CSharpSyntaxTree.ParseText(
                source,
                cancellationToken: timeoutCts.Token);
            return tree.GetRoot(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout or caller cancellation — fail soft to empty.
            return null;
        }
        catch (Exception ex)
        {
            // Malformed/truncated source never throws to the caller.
            this.LogFault(path, ex);
            return null;
        }
    }

    private static List<DeclarationInfo> CollectDeclarations(SyntaxNode root)
    {
        var declarations = new List<DeclarationInfo>();

        foreach (var node in root.DescendantNodes())
        {
            var (kind, name) = Classify(node);
            if (kind is null)
            {
                continue;
            }

            var span = node.GetLocation().GetLineSpan();
            var startLine = span.StartLinePosition.Line + 1;
            var endLine = span.EndLinePosition.Line + 1;
            declarations.Add(new DeclarationInfo(node, kind.Value, name, startLine, endLine));
        }

        return declarations;
    }

    private static (DefinitionKind? Kind, string? Name) Classify(SyntaxNode node)
    {
        return node switch
        {
            ClassDeclarationSyntax c => (DefinitionKind.Class, c.Identifier.Text),
            RecordDeclarationSyntax r => (DefinitionKind.Class, r.Identifier.Text),
            StructDeclarationSyntax s => (DefinitionKind.Class, s.Identifier.Text),
            InterfaceDeclarationSyntax i => (DefinitionKind.Interface, i.Identifier.Text),
            EnumDeclarationSyntax e => (DefinitionKind.Enum, e.Identifier.Text),
            DelegateDeclarationSyntax d => (DefinitionKind.Other, d.Identifier.Text),
            MethodDeclarationSyntax m => (DefinitionKind.Method, m.Identifier.Text),
            ConstructorDeclarationSyntax ctor => (DefinitionKind.Method, ctor.Identifier.Text),
            DestructorDeclarationSyntax dtor => (DefinitionKind.Method, dtor.Identifier.Text),
            OperatorDeclarationSyntax op => (DefinitionKind.Method, op.OperatorToken.Text),
            LocalFunctionStatementSyntax lf => (DefinitionKind.Function, lf.Identifier.Text),
            PropertyDeclarationSyntax p => (DefinitionKind.Other, p.Identifier.Text),
            EventDeclarationSyntax ev => (DefinitionKind.Other, ev.Identifier.Text),
            IndexerDeclarationSyntax => (DefinitionKind.Other, "this[]"),
            EnumMemberDeclarationSyntax em => (DefinitionKind.Other, em.Identifier.Text),
            NamespaceDeclarationSyntax ns => (DefinitionKind.Module, ns.Name.ToString()),
            FileScopedNamespaceDeclarationSyntax fns => (DefinitionKind.Module, fns.Name.ToString()),
            _ => (null, null),
        };
    }

    private void LogFault(string path, Exception ex)
    {
        this._logger.LogWarning(
            ex,
            "Roslyn-syntax analysis fault for C# file {Path}. Returning empty (fail-soft).",
            path);
    }

    private sealed record DeclarationInfo(
        SyntaxNode Node,
        DefinitionKind Kind,
        string? Name,
        int StartLine,
        int EndLine);
}
