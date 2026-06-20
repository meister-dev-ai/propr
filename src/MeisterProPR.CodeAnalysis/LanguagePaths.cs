// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Maps a file path's extension to a <see cref="SupportedLanguage" />. Shared by every
///     review-time consumer (prefetch, the cross-file tools, the deterministic feed) so the
///     extension→language mapping lives in one place behind the abstraction.
/// </summary>
public static class LanguagePaths
{
    /// <summary>
    ///     Resolves the <see cref="SupportedLanguage" /> for a path by extension, or <c>null</c>
    ///     when the extension maps to no supported language.
    /// </summary>
    public static SupportedLanguage? TryResolve(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".ts" => SupportedLanguage.TypeScript,
            ".tsx" => SupportedLanguage.Tsx,
            ".js" or ".jsx" or ".mjs" or ".cjs" => SupportedLanguage.JavaScript,
            ".py" or ".pyi" => SupportedLanguage.Python,
            ".go" => SupportedLanguage.Go,
            ".java" => SupportedLanguage.Java,
            ".rb" => SupportedLanguage.Ruby,
            ".cs" => SupportedLanguage.CSharp,
            _ => null,
        };
    }
}
