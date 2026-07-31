// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.AI;

namespace MeisterDev.ProPR.CodeInsights.Classification.Prompts;

/// <summary>
///     The prompts the insight classifiers send, rendered from the same template files and the same renderer the
///     review passes use.
/// </summary>
/// <remarks>
///     Held as templates rather than as string literals in the classifiers for the reason every other prompt in
///     this codebase is: the wording is the thing that gets tuned, and a wording change should be a diff on one
///     text file rather than on a C# builder method. What each classifier still owns is the data it puts in front
///     of the model, and the bounds it applies before doing so.
///     <para>
///         The templates ship from this project and are addressed by path, so adding one touches nothing in the
///         review pipeline.
///     </para>
/// </remarks>
internal static class InsightPrompts
{
    internal static string FindingTypeSystem(InsightPromptModels.FindingTypeSystemModel model)
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.FindingTypeSystem, model);

    internal static string FindingTypeUser(InsightPromptModels.FindingTypeUserModel model)
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.FindingTypeUser, model);

    internal static string DisregardedFindingSystem()
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.DisregardedFindingSystem);

    internal static string DisregardedFindingUser(InsightPromptModels.DisregardedFindingUserModel model)
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.DisregardedFindingUser, model);

    internal static string HumanMissSystem()
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.HumanMissSystem);

    internal static string HumanMissUser(InsightPromptModels.HumanMissUserModel model)
        => PromptTemplateRuntime.RenderTemplateFile(InsightPromptTemplates.HumanMissUser, model);
}
