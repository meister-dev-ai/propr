// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

/// <summary>
///     The prompts the thread-memory keyword extractor sends, rendered from template files.
/// </summary>
/// <remarks>
///     Addressed by path rather than through the review stage catalog: these are not review stages, so no prompt
///     override, experiment or per-client customisation applies to them.
/// </remarks>
internal static class ThreadMemoryPrompts
{
    private const string KeywordsSystemTemplate = "thread-memory/memory-keywords-system.hbs";

    private const string KeywordsUserTemplate = "thread-memory/memory-keywords-user.hbs";

    internal static string KeywordsSystem(KeywordsSystemModel model)
        => PromptTemplateRuntime.RenderTemplateFile(KeywordsSystemTemplate, model);

    internal static string KeywordsUser(KeywordsUserModel model)
        => PromptTemplateRuntime.RenderTemplateFile(KeywordsUserTemplate, model);

    /// <summary>What the system template is rendered against. Camel-case members, as the template names them.</summary>
    internal sealed record KeywordsSystemModel(int maxKeywords);

    /// <summary>What the user template is rendered against. Camel-case members, as the template names them.</summary>
    internal sealed record KeywordsUserModel(string resolution, bool hasChangeExcerpt, string changeExcerpt);
}
