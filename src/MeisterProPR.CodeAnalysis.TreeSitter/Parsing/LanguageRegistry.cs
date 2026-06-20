// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using TS = TreeSitter;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Parsing;

/// <summary>
///     Maps file extensions to <see cref="SupportedLanguage" />, owns the per-language
///     Tree-sitter <c>Language</c> handle, and exposes the per-language definition-kind
///     node-type sets used by the enclosing-definition ancestor walk.
/// </summary>
/// <remarks>
///     <para>
///         C# is intentionally excluded - the existing Roslyn path continues
///         to own C# review context. Unknown extensions resolve to <c>null</c> and the
///         analyzer returns <see cref="FallbackReason.UnsupportedLanguage" />.
///     </para>
///     <para>
///         <see cref="TryResolveByExtension" /> and <see cref="TryGetLanguage" /> are
///         thread-safe for concurrent readers once the registry is built; the
///         <c>TreeSitter.Language</c> handles are loaded lazily on first access
///         per language and cached.
///     </para>
/// </remarks>
internal static class LanguageRegistry
{
    private static readonly FrozenDictionary<string, SupportedLanguage> ExtensionToLanguage =
        new Dictionary<string, SupportedLanguage>(StringComparer.OrdinalIgnoreCase)
        {
            [".ts"] = SupportedLanguage.TypeScript,
            [".tsx"] = SupportedLanguage.Tsx,
            [".js"] = SupportedLanguage.JavaScript,
            [".jsx"] = SupportedLanguage.JavaScript,
            [".mjs"] = SupportedLanguage.JavaScript,
            [".cjs"] = SupportedLanguage.JavaScript,
            [".py"] = SupportedLanguage.Python,
            [".pyi"] = SupportedLanguage.Python,
            [".go"] = SupportedLanguage.Go,
            [".java"] = SupportedLanguage.Java,
            [".rb"] = SupportedLanguage.Ruby,
        }.ToFrozenDictionary();

    /// <summary>
    ///     Tree-sitter language-id strings accepted by <c>new TreeSitter.Language(string)</c>
    ///     (mariusgreuel/TreeSitter.DotNet). See <c>Language.MapLanguageId</c> in the
    ///     upstream binding - case-insensitive, special-cased only for C++/C#.
    /// </summary>
    private static readonly FrozenDictionary<SupportedLanguage, string> UpstreamLanguageIds =
        new Dictionary<SupportedLanguage, string>
        {
            [SupportedLanguage.TypeScript] = "TypeScript",
            [SupportedLanguage.Tsx] = "TSX",
            [SupportedLanguage.JavaScript] = "JavaScript",
            [SupportedLanguage.Python] = "Python",
            [SupportedLanguage.Go] = "Go",
            [SupportedLanguage.Java] = "Java",
            [SupportedLanguage.Ruby] = "Ruby",
        }.ToFrozenDictionary();

    /// <summary>
    ///     Per-language node-type names the ancestor walk treats as definitions.
    ///     Sourced from each grammar's <c>tags.scm</c> + node-types catalogue, versioned
    ///     against the pinned <c>TreeSitter.DotNet</c> grammars.
    /// </summary>
    private static readonly FrozenDictionary<SupportedLanguage, FrozenSet<string>> DefinitionNodeTypes =
        new Dictionary<SupportedLanguage, FrozenSet<string>>
        {
            [SupportedLanguage.TypeScript] = new HashSet<string>(StringComparer.Ordinal)
            {
                "function_declaration",
                "function_signature",
                "generator_function_declaration",
                "method_definition",
                "method_signature",
                "class_declaration",
                "abstract_class_declaration",
                "interface_declaration",
                "enum_declaration",
                "internal_module",
                "module",
                "type_alias_declaration",
            }.ToFrozenStringSet(),
            [SupportedLanguage.Tsx] = new HashSet<string>(StringComparer.Ordinal)
            {
                "function_declaration",
                "function_signature",
                "generator_function_declaration",
                "method_definition",
                "method_signature",
                "class_declaration",
                "abstract_class_declaration",
                "interface_declaration",
                "enum_declaration",
                "internal_module",
                "module",
                "type_alias_declaration",
            }.ToFrozenStringSet(),
            [SupportedLanguage.JavaScript] = new HashSet<string>(StringComparer.Ordinal)
            {
                "function_declaration",
                "generator_function_declaration",
                "class_declaration",
                "method_definition",
                "export_statement",
            }.ToFrozenStringSet(),
            [SupportedLanguage.Python] = new HashSet<string>(StringComparer.Ordinal)
            {
                "function_definition",
                "class_definition",
                "decorated_definition",
            }.ToFrozenStringSet(),
            [SupportedLanguage.Go] = new HashSet<string>(StringComparer.Ordinal)
            {
                "function_declaration",
                "method_declaration",
                "type_declaration",
            }.ToFrozenStringSet(),
            [SupportedLanguage.Java] = new HashSet<string>(StringComparer.Ordinal)
            {
                "method_declaration",
                "constructor_declaration",
                "class_declaration",
                "interface_declaration",
                "enum_declaration",
                "record_declaration",
                "annotation_type_declaration",
            }.ToFrozenStringSet(),
            [SupportedLanguage.Ruby] = new HashSet<string>(StringComparer.Ordinal)
            {
                "method",
                "class",
                "module",
                "singleton_class",
            }.ToFrozenStringSet(),
        }.ToFrozenDictionary();

    /// <summary>
    ///     Per-language mapping from a definition node-type to a coarse
    ///     <see cref="DefinitionKind" /> used by the trace and the prefetch payload.
    /// </summary>
    private static readonly FrozenDictionary<SupportedLanguage, FrozenDictionary<string, DefinitionKind>> DefinitionKindByNodeType =
        new Dictionary<SupportedLanguage, FrozenDictionary<string, DefinitionKind>>
        {
            [SupportedLanguage.TypeScript] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["function_declaration"] = DefinitionKind.Function,
                ["function_signature"] = DefinitionKind.Function,
                ["generator_function_declaration"] = DefinitionKind.Function,
                ["method_definition"] = DefinitionKind.Method,
                ["method_signature"] = DefinitionKind.Method,
                ["class_declaration"] = DefinitionKind.Class,
                ["abstract_class_declaration"] = DefinitionKind.Class,
                ["interface_declaration"] = DefinitionKind.Interface,
                ["enum_declaration"] = DefinitionKind.Enum,
                ["internal_module"] = DefinitionKind.Module,
                ["module"] = DefinitionKind.Module,
                ["type_alias_declaration"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.Tsx] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["function_declaration"] = DefinitionKind.Function,
                ["function_signature"] = DefinitionKind.Function,
                ["generator_function_declaration"] = DefinitionKind.Function,
                ["method_definition"] = DefinitionKind.Method,
                ["method_signature"] = DefinitionKind.Method,
                ["class_declaration"] = DefinitionKind.Class,
                ["abstract_class_declaration"] = DefinitionKind.Class,
                ["interface_declaration"] = DefinitionKind.Interface,
                ["enum_declaration"] = DefinitionKind.Enum,
                ["internal_module"] = DefinitionKind.Module,
                ["module"] = DefinitionKind.Module,
                ["type_alias_declaration"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.JavaScript] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["function_declaration"] = DefinitionKind.Function,
                ["generator_function_declaration"] = DefinitionKind.Function,
                ["class_declaration"] = DefinitionKind.Class,
                ["method_definition"] = DefinitionKind.Method,
                ["export_statement"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.Python] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["function_definition"] = DefinitionKind.Function,
                ["class_definition"] = DefinitionKind.Class,
                ["decorated_definition"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.Go] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["function_declaration"] = DefinitionKind.Function,
                ["method_declaration"] = DefinitionKind.Method,
                ["type_declaration"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.Java] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["method_declaration"] = DefinitionKind.Method,
                ["constructor_declaration"] = DefinitionKind.Method,
                ["class_declaration"] = DefinitionKind.Class,
                ["interface_declaration"] = DefinitionKind.Interface,
                ["enum_declaration"] = DefinitionKind.Enum,
                ["record_declaration"] = DefinitionKind.Class,
                ["annotation_type_declaration"] = DefinitionKind.Other,
            }.ToFrozenDictionary(),
            [SupportedLanguage.Ruby] = new Dictionary<string, DefinitionKind>(StringComparer.Ordinal)
            {
                ["method"] = DefinitionKind.Function,
                ["class"] = DefinitionKind.Class,
                ["module"] = DefinitionKind.Module,
                ["singleton_class"] = DefinitionKind.Class,
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary();

    /// <summary>Supported languages in stable enum order.</summary>
    public static IReadOnlyCollection<SupportedLanguage> SupportedLanguages { get; } =
        new[]
        {
            SupportedLanguage.TypeScript, SupportedLanguage.Tsx, SupportedLanguage.JavaScript,
            SupportedLanguage.Python, SupportedLanguage.Go, SupportedLanguage.Java, SupportedLanguage.Ruby,
        };

    /// <summary>
    ///     Resolves a <see cref="SupportedLanguage" /> from a file path's extension.
    ///     Returns <c>null</c> for unsupported extensions (including <c>.cs</c>, R7).
    /// </summary>
    public static SupportedLanguage? TryResolveByExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var dotIndex = path.LastIndexOf('.');
        if (dotIndex < 0 || dotIndex == path.Length - 1)
        {
            return null;
        }

        var ext = path[dotIndex..];
        return ExtensionToLanguage.TryGetValue(ext, out var language) ? language : null;
    }

    /// <summary>
    ///     Loads (and caches) the <c>TreeSitter.Language</c> for the given language.
    ///     Returns <c>null</c> when the upstream native library cannot be loaded.
    /// </summary>
    /// <remarks>
    ///     Language handles are cached per-language in a thread-safe lazy. The
    ///     <c>TreeSitter.Language</c> object owns a native pointer and is not disposed
    ///     for the lifetime of the registry (handles are immutable, single-loaded).
    /// </remarks>
    public static TS.Language? TryGetLanguage(SupportedLanguage language)
    {
        if (!UpstreamLanguageIds.TryGetValue(language, out var id))
        {
            return null;
        }

        if (!LanguageCache.Instance.TryGetValue(language, out var cached))
        {
            return null;
        }

        return cached;
    }

    /// <summary>
    ///     Returns the per-language set of node-type names the ancestor walk treats
    ///     as definitions. Returns an empty set when the language has no mapping.
    /// </summary>
    public static FrozenSet<string> GetDefinitionNodeTypes(SupportedLanguage language)
    {
        return DefinitionNodeTypes.TryGetValue(language, out var set) ? set : FrozenSet<string>.Empty;
    }

    /// <summary>
    ///     Maps a definition node-type to a coarse <see cref="DefinitionKind" /> for
    ///     the trace/payload. Returns <see cref="DefinitionKind.Other" /> when unknown.
    /// </summary>
    public static DefinitionKind GetDefinitionKind(SupportedLanguage language, string nodeType)
    {
        if (string.IsNullOrEmpty(nodeType))
        {
            return DefinitionKind.Other;
        }

        return DefinitionKindByNodeType.TryGetValue(language, out var map)
               && map.TryGetValue(nodeType, out var kind)
            ? kind
            : DefinitionKind.Other;
    }

    /// <summary>
    ///     Returns the embedded <c>tags.scm</c> definition-query source for the given
    ///     language, or <c>null</c> when none is vendored. The caller is responsible for
    ///     constructing a <c>Query</c> against the loaded <c>TreeSitter.Language</c>.
    /// </summary>
    public static string? TryGetTagsQuerySource(SupportedLanguage language)
    {
        var resourceName = $"MeisterProPR.CodeAnalysis.TreeSitter.Queries.{LanguageFolder(language)}.tags.scm";
        using var stream = typeof(LanguageRegistry).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string LanguageFolder(SupportedLanguage language)
    {
        return language switch
        {
            SupportedLanguage.TypeScript => "typescript",
            SupportedLanguage.Tsx => "tsx",
            SupportedLanguage.JavaScript => "javascript",
            SupportedLanguage.Python => "python",
            SupportedLanguage.Go => "go",
            SupportedLanguage.Java => "java",
            SupportedLanguage.Ruby => "ruby",
            _ => string.Empty,
        };
    }

    /// <summary>
    ///     Thread-safe, lazy per-language cache of the loaded <c>TreeSitter.Language</c>
    ///     handles. The first call for a language loads the native library; failures are
    ///     cached as <c>null</c> so subsequent callers don't re-attempt a failing load.
    /// </summary>
    private static class LanguageCache
    {
        public static readonly IReadOnlyDictionary<SupportedLanguage, TS.Language?> Instance = Build();

        private static IReadOnlyDictionary<SupportedLanguage, TS.Language?> Build()
        {
            var dict = new Dictionary<SupportedLanguage, TS.Language?>();
            foreach (var language in SupportedLanguages)
            {
                if (!UpstreamLanguageIds.TryGetValue(language, out var id))
                {
                    dict[language] = null;
                    continue;
                }

                try
                {
                    dict[language] = new TS.Language(id);
                }
                catch
                {
                    // Native lib missing / incompatible - fail-soft: the probe surfaces this
                    // at startup; the analyzer reports IsAvailable=false and callers fall back.
                    dict[language] = null;
                }
            }

            return dict;
        }
    }
}

internal static class FrozenStringSetExtensions
{
    public static FrozenSet<string> ToFrozenStringSet(this HashSet<string> set)
    {
        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
