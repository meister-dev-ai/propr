// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterProPR.Application.Options;
using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;
using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TS = TreeSitter;

namespace MeisterProPR.CodeAnalysis.TreeSitter;

/// <summary>
///     Tree-sitter adapter for <see cref="IStructuralCodeAnalyzer" /> (US2 / US1).
/// </summary>
/// <remarks>
///     <para>
///         Parses a supported-language source buffer via the pooled native parser, then either
///         walks ancestors to find the enclosing definition of a changed range or runs the per-language <c>tags.scm</c> query to list definitions.
///     </para>
///     <para>
///         <b>Invariants (contract):</b> never throws to the caller for input/parse problems -
///         returns empty so the prefetch stage falls back deterministically.
///     </para>
/// </remarks>
internal sealed class TreeSitterStructuralCodeAnalyzer : IStructuralCodeAnalyzer
{
    private static readonly FrozenDictionary<string, DefinitionKind> CaptureNameKindMap =
        new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
        {
            ["definition.function"] = DefinitionKind.Function,
            ["definition.method"] = DefinitionKind.Method,
            ["definition.class"] = DefinitionKind.Class,
            ["definition.interface"] = DefinitionKind.Interface,
            ["definition.enum"] = DefinitionKind.Enum,
            ["definition.module"] = DefinitionKind.Module,
            ["definition.annotation"] = DefinitionKind.Other,
            ["definition.type"] = DefinitionKind.Other,
            ["definition.constant"] = DefinitionKind.Other,
        }.ToFrozenDictionary();

    private readonly ILogger<TreeSitterStructuralCodeAnalyzer> _logger;
    private readonly IOptions<AiReviewOptions> _options;
    private readonly ParserPool _pool;

    private readonly IStructuralAnalyzerProbe _probe;

    public TreeSitterStructuralCodeAnalyzer(
        IStructuralAnalyzerProbe probe,
        ParserPool pool,
        IOptions<AiReviewOptions> options,
        ILogger<TreeSitterStructuralCodeAnalyzer> logger)
    {
        this._probe = probe;
        this._pool = pool;
        this._options = options;
        this._logger = logger;
    }

    /// <inheritdoc />
    public bool IsAvailable => this._probe.IsAvailable;

    /// <inheritdoc />
    public bool CanAnalyze(string path)
    {
        if (!this.IsAvailable)
        {
            return false;
        }

        return LanguageRegistry.TryResolveByExtension(path) is not null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EnclosingDefinition>> ResolveEnclosingDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        if (!this.IsAvailable)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        var language = this.ResolveLanguage(request);
        if (language is null)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        if (string.IsNullOrEmpty(request.SourceText))
        {
            return Array.Empty<EnclosingDefinition>();
        }

        if (request.ChangedLineRanges.Count == 0)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        PooledParseResult parseResult;
        try
        {
            parseResult = await this._pool.ParseAsync(language.Value, request.SourceText, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<EnclosingDefinition>();
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<EnclosingDefinition>();
        }

        if (parseResult.Tree is null)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        using var tree = parseResult.Tree;
        try
        {
            return this.ResolveEnclosingDefinitions(language.Value, request, tree);
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<EnclosingDefinition>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DefinitionSummary>> GetDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        if (!this.IsAvailable)
        {
            return Array.Empty<DefinitionSummary>();
        }

        var language = this.ResolveLanguage(request);
        if (language is null)
        {
            return Array.Empty<DefinitionSummary>();
        }

        if (string.IsNullOrEmpty(request.SourceText))
        {
            return Array.Empty<DefinitionSummary>();
        }

        PooledParseResult parseResult;
        try
        {
            parseResult = await this._pool.ParseAsync(language.Value, request.SourceText, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<DefinitionSummary>();
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<DefinitionSummary>();
        }

        if (parseResult.Tree is null)
        {
            return Array.Empty<DefinitionSummary>();
        }

        using var tree = parseResult.Tree;
        try
        {
            return this.ListDefinitions(language.Value, tree);
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<DefinitionSummary>();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ConfirmReferenceLinesAsync(
        StructuralParseRequest request,
        string symbol,
        CancellationToken ct)
    {
        if (!this.IsAvailable)
        {
            return Array.Empty<int>();
        }

        var language = this.ResolveLanguage(request);
        if (language is null)
        {
            return Array.Empty<int>();
        }

        if (string.IsNullOrEmpty(request.SourceText) || string.IsNullOrEmpty(symbol))
        {
            return Array.Empty<int>();
        }

        PooledParseResult parseResult;
        try
        {
            parseResult = await this._pool.ParseAsync(language.Value, request.SourceText, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<int>();
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<int>();
        }

        if (parseResult.Tree is null)
        {
            return Array.Empty<int>();
        }

        using var tree = parseResult.Tree;
        try
        {
            return ConfirmReferenceLines(tree, symbol);
        }
        catch (Exception ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return Array.Empty<int>();
        }
    }

    public async Task<string> ExtractCodeTextAsync(StructuralParseRequest request, CancellationToken ct)
    {
        if (!this.IsAvailable)
        {
            return string.Empty;
        }

        var language = this.ResolveLanguage(request);
        if (language is null || string.IsNullOrEmpty(request.SourceText))
        {
            return string.Empty;
        }

        PooledParseResult parseResult;
        try
        {
            parseResult = await this._pool.ParseAsync(language.Value, request.SourceText, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
        catch (ArgumentException ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return string.Empty;
        }

        if (parseResult.Tree is null)
        {
            return string.Empty;
        }

        using var tree = parseResult.Tree;
        try
        {
            return BlankCommentsAndStrings(tree, request.SourceText);
        }
        catch (ArgumentException ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return string.Empty;
        }
        catch (InvalidOperationException ex)
        {
            this.LogParseFault(language.Value, request.Path, ex);
            return string.Empty;
        }
    }

    private static string BlankCommentsAndStrings(TS.Tree tree, string source)
    {
        // Blank comment and string/char literal node spans by overwriting their characters with spaces while
        // preserving newlines — line numbers and line count stay intact. StartIndex/EndIndex are character
        // indices into the source (the binding converts from byte offsets). The outermost matching node is
        // blanked whole (children skipped), which also removes interpolation holes; fine for marker matching.
        var chars = source.ToCharArray();
        var stack = new Stack<TS.Node>();
        stack.Push(tree.RootNode);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node.IsNamed && IsCommentOrStringKind(node.Type))
            {
                var start = Math.Clamp(node.StartIndex, 0, chars.Length);
                var end = Math.Clamp(node.EndIndex, start, chars.Length);
                for (var i = start; i < end; i++)
                {
                    if (chars[i] != '\n')
                    {
                        chars[i] = ' ';
                    }
                }

                continue;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return new string(chars);
    }

    private static bool IsCommentOrStringKind(string nodeType)
    {
        // `comment` plus string/char/rune/heredoc literal node kinds across the seven kept grammars
        // (string, string_literal, template_string, raw_string_literal, interpreted_string_literal,
        // char_literal, character_literal, rune_literal, heredocs). Identifier nodes never match.
        return string.Equals(nodeType, "comment", StringComparison.Ordinal)
               || nodeType.Contains("string", StringComparison.Ordinal)
               || nodeType.Contains("char", StringComparison.Ordinal)
               || nodeType.Contains("rune", StringComparison.Ordinal)
               || nodeType.Contains("heredoc", StringComparison.Ordinal);
    }

    private static IReadOnlyList<int> ConfirmReferenceLines(TS.Tree tree, string symbol)
    {
        // Walk the parse tree and record the 1-based line of every identifier node whose text
        // matches the symbol. Identifiers inside comments and string/char literals are NOT
        // tokenized into identifier nodes by the grammar (a comment is a `comment` node, a string
        // is a `string`/`string_literal` node whose content is opaque), so matching on identifier
        // node kinds inherently excludes comment/string occurrences.
        var lines = new SortedSet<int>();
        var stack = new Stack<TS.Node>();
        stack.Push(tree.RootNode);

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (node.IsNamed
                && IsIdentifierKind(node.Type)
                && string.Equals(node.Text, symbol, StringComparison.Ordinal))
            {
                lines.Add(node.StartPosition.Row + 1);
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }

        return lines.Count == 0 ? Array.Empty<int>() : lines.ToArray();
    }

    private static bool IsIdentifierKind(string nodeType)
    {
        // Covers `identifier` plus the grammar variants (`property_identifier`, `type_identifier`,
        // `field_identifier`, `constant`, `shorthand_property_identifier`, ...) across the seven
        // kept grammars. `comment`/`string`/`string_literal`/`template_string` never satisfy this.
        return nodeType.EndsWith("identifier", StringComparison.Ordinal)
               || string.Equals(nodeType, "constant", StringComparison.Ordinal);
    }

    private SupportedLanguage? ResolveLanguage(StructuralParseRequest request)
    {
        // The contract is by-extension (R7): the path's extension determines the language.
        // When the path resolves to an unsupported extension (including .cs), the analyzer
        // refuses - the caller falls back to the heuristic. request.Language is informational
        // only and does not override the path's resolution.
        return LanguageRegistry.TryResolveByExtension(request.Path);
    }

    private IReadOnlyList<DefinitionSummary> ListDefinitions(SupportedLanguage language, TS.Tree tree)
    {
        var tagsSource = LanguageRegistry.TryGetTagsQuerySource(language);
        if (string.IsNullOrWhiteSpace(tagsSource))
        {
            return Array.Empty<DefinitionSummary>();
        }

        var tsLanguage = LanguageRegistry.TryGetLanguage(language);
        if (tsLanguage is null)
        {
            return Array.Empty<DefinitionSummary>();
        }

        using var query = tsLanguage.CreateQuery(tagsSource);
        using var cursor = query.Execute(tree.RootNode);

        var results = ExtractDefinitions(cursor)
            .Select(d => new DefinitionSummary(d.Kind, d.Name, d.StartLine, d.EndLine))
            .ToList();

        results.Sort((a, b) =>
        {
            var byStart = a.StartLine.CompareTo(b.StartLine);
            return byStart != 0 ? byStart : a.EndLine.CompareTo(b.EndLine);
        });

        return results;
    }

    /// <summary>
    ///     Runs the tags query's matches into deduplicated definition entries (one per distinct
    ///     definition node, keeping the first capture in document order).
    /// </summary>
    private static List<(TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine)> ExtractDefinitions(TS.QueryCursor cursor)
    {
        var definitions = new List<(TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine)>();
        var seenIds = new HashSet<IntPtr>();

        foreach (var match in cursor.Matches)
        {
            TS.Node? defNode = null;
            string? defCaptureName = null;
            TS.Node? nameNode = null;

            foreach (var capture in match.Captures)
            {
                if (capture.Name.StartsWith("definition.", StringComparison.Ordinal))
                {
                    defNode = capture.Node;
                    defCaptureName = capture.Name;
                }
                else if (capture.Name == "name")
                {
                    nameNode = capture.Node;
                }
            }

            if (defNode is null || !seenIds.Add(defNode.Id))
            {
                continue;
            }

            var kind = CaptureNameToKind(defCaptureName);
            var name = nameNode?.Text;
            var startLine = defNode.StartPosition.Row + 1;
            var endLine = defNode.EndPosition.Row + 1;
            definitions.Add((defNode, name, kind, startLine, endLine));
        }

        return definitions;
    }

    private IReadOnlyList<EnclosingDefinition> ResolveEnclosingDefinitions(
        SupportedLanguage language,
        StructuralParseRequest request,
        TS.Tree tree)
    {
        var tagsSource = LanguageRegistry.TryGetTagsQuerySource(language);
        if (string.IsNullOrWhiteSpace(tagsSource))
        {
            return Array.Empty<EnclosingDefinition>();
        }

        var tsLanguage = LanguageRegistry.TryGetLanguage(language);
        if (tsLanguage is null)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        // Capture all definitions once via the tags query, then for each changed range find
        // the definitions whose line span overlaps the range. For a change fully inside one
        // definition, only that definition overlaps. For a change spanning adjacent definitions,
        // each spans-into definition overlaps.
        using var query = tsLanguage.CreateQuery(tagsSource);
        using var cursor = query.Execute(tree.RootNode);

        var definitions = ExtractDefinitions(cursor);
        if (definitions.Count == 0)
        {
            return Array.Empty<EnclosingDefinition>();
        }

        var totalLines = CountLines(request.SourceText);
        var enclosing = new List<EnclosingDefinition>();
        var enclosedSeen = new HashSet<IntPtr>();

        foreach (var range in request.ChangedLineRanges)
        {
            if (range.IsEmpty)
            {
                continue;
            }

            CollectEnclosingForRange(range, definitions, totalLines, enclosedSeen, enclosing);
        }

        enclosing.Sort((a, b) =>
        {
            var byStart = a.StartLine.CompareTo(b.StartLine);
            return byStart != 0 ? byStart : a.EndLine.CompareTo(b.EndLine);
        });

        return enclosing;
    }

    private static void CollectEnclosingForRange(
        ChangedLineRange range,
        List<(TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine)> definitions,
        int totalLines,
        HashSet<IntPtr> enclosedSeen,
        List<EnclosingDefinition> enclosing)
    {
        // 1-based inclusive overlap: def.Start <= range.End && def.End >= range.Start.
        var overlapping = new List<(TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine)>();
        foreach (var entry in definitions)
        {
            if (entry.StartLine > range.End || entry.EndLine < range.Start)
            {
                continue;
            }

            overlapping.Add(entry);
        }

        // Innermost filter: when multiple definitions overlap a single range, drop
        // any that strictly contain another overlapping definition. This keeps the
        // tightest enclosing scope (a method, not its containing class) while still
        // returning sibling definitions when a change spans two adjacent functions.
        foreach (var candidate in overlapping)
        {
            if (IsStrictlyOuterDefinition(candidate, overlapping))
            {
                continue;
            }

            if (!enclosedSeen.Add(candidate.Node.Id))
            {
                continue;
            }

            enclosing.Add(new EnclosingDefinition(candidate.Kind, candidate.Name, candidate.StartLine, candidate.EndLine, totalLines));
        }
    }

    private static bool IsStrictlyOuterDefinition(
        (TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine) candidate,
        List<(TS.Node Node, string? Name, DefinitionKind Kind, int StartLine, int EndLine)> overlapping)
    {
        foreach (var other in overlapping)
        {
            if (other.Node.Id == candidate.Node.Id)
            {
                continue;
            }

            // `other` is strictly inside `candidate`?
            if (other.StartLine >= candidate.StartLine
                && other.EndLine <= candidate.EndLine
                && (other.StartLine != candidate.StartLine || other.EndLine != candidate.EndLine))
            {
                return true;
            }
        }

        return false;
    }

    private static DefinitionKind CaptureNameToKind(string? captureName)
    {
        if (string.IsNullOrEmpty(captureName))
        {
            return DefinitionKind.Other;
        }

        return CaptureNameKindMap.TryGetValue(captureName, out var kind) ? kind : DefinitionKind.Other;
    }

    private static int CountLines(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return 0;
        }

        var count = 1;
        var i = 0;
        while (i < source.Length)
        {
            var ch = source[i];
            if (ch == '\n')
            {
                count++;
            }
            else if (ch == '\r')
            {
                count++;
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    i++;
                }
            }

            i++;
        }

        return count;
    }

    private void LogParseFault(SupportedLanguage language, string path, Exception ex)
    {
        this._logger.LogWarning(
            ex,
            "Structural parse fault for {Language} file {Path}. Falling back to heuristic.",
            language, path);
    }
}
