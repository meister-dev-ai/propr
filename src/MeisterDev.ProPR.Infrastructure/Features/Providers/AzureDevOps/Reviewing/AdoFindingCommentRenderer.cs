// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Renders finding evidence in the host's closed assessment section.</summary>
internal static class AdoFindingCommentRenderer
{
    internal static string Format(
        ReviewComment comment, bool applicableFix = false, string suggestionInfo = "suggestion",
        FindingBadgeStyle badgeStyle = FindingBadgeStyle.ShieldsIo) =>
        ReviewFindingTextFormatter.Format(
            comment, renderedMessage: ReviewSuggestedFixRenderer.RenderMessage(comment, applicableFix, suggestionInfo),
            collapseAssessment: true, renderText: RenderText, htmlAssessment: true, badgeStyle: badgeStyle);

    internal static string RenderText(string text) => ReviewFindingTextFormatter.RenderPlainText(text);
}
