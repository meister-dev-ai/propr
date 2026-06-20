// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Languages the unified code-analysis abstraction can parse. The seven Tree-sitter
///     languages are served syntactically by the Tree-sitter backend; <see cref="CSharp" />
///     is served by the Roslyn-syntax backend.
/// </summary>
public enum SupportedLanguage
{
    TypeScript,
    Tsx,
    JavaScript,
    Python,
    Go,
    Java,
    Ruby,
    CSharp,
}
