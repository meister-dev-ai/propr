// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Telemetry;

/// <summary>ActivitySource used for tracing review job operations.</summary>
public static class ReviewJobTelemetry
{
    /// <summary>Shared activity-tag name for the owning client identifier.</summary>
    public const string ClientIdTagName = "client_id";

    /// <summary>Shared activity-tag name for the normalized SCM provider identifier.</summary>
    public const string ScmProviderTagName = "scm_provider";

    /// <summary>Main activity source for review job spans.</summary>
    public static readonly ActivitySource Source = new("MeisterProPR.ReviewJobs", "1.0.0");

    /// <summary>Starts a review-job activity and applies shared provider and client tags when available.</summary>
    public static Activity? StartActivity(
        string name,
        ActivityKind kind = ActivityKind.Internal,
        ScmProvider? provider = null,
        Guid? clientId = null)
    {
        var activity = Source.StartActivity(name, kind);
        if (activity is null)
        {
            return null;
        }

        if (provider.HasValue)
        {
            activity.SetTag(ScmProviderTagName, ToProviderTag(provider.Value));
        }

        if (clientId.HasValue && clientId.Value != Guid.Empty)
        {
            activity.SetTag(ClientIdTagName, clientId.Value.ToString("D"));
        }

        return activity;
    }

    /// <summary>Formats a provider enum into a stable telemetry tag value.</summary>
    public static string ToProviderTag(ScmProvider provider)
    {
        return provider switch
        {
            ScmProvider.AzureDevOps => "azuredevops",
            ScmProvider.GitHub => "github",
            ScmProvider.GitLab => "gitlab",
            ScmProvider.Forgejo => "forgejo",
            _ => provider.ToString().ToLowerInvariant(),
        };
    }

    /// <summary>Normalizes a provider set into a single telemetry scope value.</summary>
    public static string DescribeProviderScope(IEnumerable<ScmProvider> providers)
    {
        var distinctProviders = providers
            .Distinct()
            .ToArray();

        return distinctProviders.Length switch
        {
            0 => "none",
            1 => ToProviderTag(distinctProviders[0]),
            _ => "mixed",
        };
    }
}
