// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Classification.Prompts;

/// <summary>
///     What each insight template is rendered against. Camel-case members, as the templates reference them.
/// </summary>
internal static class InsightPromptModels
{
    internal sealed record FindingTypeSystemModel(
        IReadOnlyList<InsightTagModel> coreTags,
        bool hasCustomTags,
        IReadOnlyList<InsightTagModel> customTags);

    internal sealed record InsightTagModel(string slug, string definition);

    internal sealed record FindingTypeUserModel(
        string anchor,
        string severity,
        string originPassKind,
        string message);

    internal sealed record DisregardedFindingUserModel(
        string location,
        string finding,
        string discussion,
        bool hasChangeExcerpt,
        string changeExcerpt);

    internal sealed record HumanMissUserModel(string location, string threadStatus, string discussion);
}
