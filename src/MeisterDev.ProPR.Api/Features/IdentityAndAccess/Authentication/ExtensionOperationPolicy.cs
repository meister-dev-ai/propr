// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Reviewing.Intake.Controllers;
using MeisterDev.ProPR.CodeInsights.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>Positive API action boundary for extension access tokens.</summary>
public static class ExtensionOperationPolicy
{
    public const string ItemKey = "ProPRExtensionCaller";

    private static readonly FrozenSet<(string Controller, string Action)> Allowed = new (string, string)[]
    {
        Action<AuthController>(nameof(AuthController.GetMe)),
        Action<PullRequestResolutionController>(nameof(PullRequestResolutionController.ResolvePullRequest)),
        Action<PrReviewViewController>(nameof(PrReviewViewController.GetPrView)),
        Action<JobsController>(nameof(JobsController.GetJob)),
        Action<JobsController>(nameof(JobsController.GetJobResult)),
        Action<CodeQualityController>(nameof(CodeQualityController.GetFindings)),
        Action<CodeQualityController>(nameof(CodeQualityController.GetHotspots)),
        Action<ReviewJobsController>(nameof(ReviewJobsController.SubmitReviewByCoordinates)),
        Action<ReviewJobsController>(nameof(ReviewJobsController.RestartReview)),
        Action<ReviewJobsController>(nameof(ReviewJobsController.StopReview)),
        Action<FindingDismissalController>(nameof(FindingDismissalController.DismissFinding)),
    }.ToFrozenSet();

    private static (string, string) Action<T>(string action) => (typeof(T).Name[..^"Controller".Length], action);
    public static bool IsAllowed(string controller, string action) => Allowed.Contains((controller, action));

    public static bool Authorize(HttpContext context) => context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } descriptor
                                                         && IsAllowed(descriptor.ControllerName, descriptor.ActionName);

    public static void ValidateRegisteredActions(IEnumerable<ControllerActionDescriptor> descriptors)
    {
        if (!Allowed.IsSubsetOf(descriptors.Select(value => (value.ControllerName, value.ActionName)).ToHashSet()))
        {
            throw new InvalidOperationException("An extension permission has no registered MVC action.");
        }
    }
}
