// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Classification.Prompts;

/// <summary>
///     Where each insight prompt template lives, relative to the shipped prompt root.
/// </summary>
/// <remarks>
///     Addressed by path rather than through the review stage catalog, because these are not review stages: no
///     prompt override, experiment, or per-client customisation applies to them, and the templates ship from this
///     project rather than from the review pipeline's.
/// </remarks>
internal static class InsightPromptTemplates
{
    internal const string FindingTypeSystem = "code-insights/finding-type-system.hbs";

    internal const string FindingTypeUser = "code-insights/finding-type-user.hbs";

    internal const string DisregardedFindingSystem = "code-insights/disregarded-finding-system.hbs";

    internal const string DisregardedFindingUser = "code-insights/disregarded-finding-user.hbs";

    internal const string HumanMissSystem = "code-insights/human-miss-system.hbs";

    internal const string HumanMissUser = "code-insights/human-miss-user.hbs";
}
