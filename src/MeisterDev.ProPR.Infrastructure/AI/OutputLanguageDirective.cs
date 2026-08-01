// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     The one place the output-language rule is worded. Every prompt that emits reviewer-facing prose appends
///     the same fragment, which is what keeps a review's per-file findings, its summary, its thread replies, its
///     mention answers and its memory prose in one language.
/// </summary>
internal static class OutputLanguageDirective
{
    /// <summary>
    ///     Appends the output-language rule for <paramref name="languageTag" /> to <paramref name="prompt" />.
    ///     A blank tag leaves the prompt untouched, which is how contexts built outside a review job (the offline
    ///     harness, for example) keep their prompts byte-identical.
    /// </summary>
    internal static string Append(string prompt, string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return prompt;
        }

        var directive = PromptTemplateRuntime.RenderOutputLanguage(languageTag.Trim());

        return string.IsNullOrEmpty(prompt)
            ? directive
            : string.Concat(prompt, Environment.NewLine, Environment.NewLine, directive);
    }

    /// <summary>Appends the output-language rule configured on <paramref name="context" />.</summary>
    internal static string Append(string prompt, ReviewSystemContext? context)
    {
        return Append(prompt, context?.OutputLanguage);
    }
}
