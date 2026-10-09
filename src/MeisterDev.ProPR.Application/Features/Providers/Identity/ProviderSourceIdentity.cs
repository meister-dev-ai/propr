// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Features.Providers.Identity;

/// <summary>A captured provider namespace without credentials, queries or fragments.</summary>
public sealed record ProviderSourceIdentity
{
    private ProviderSourceIdentity(ScmProvider provider, string value)
    {
        this.Provider = provider;
        this.Value = value;
    }

    public ScmProvider Provider { get; }
    public string Value { get; }

    /// <summary>Uses the immutable source captured by the review job.</summary>
    public static ProviderSourceIdentity FromReviewJob(ReviewJob job, IReviewSourcePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(job);
        var source = policy.SelectCapturedSource(job.OrganizationUrl, job.HostBaseUrl);
        return FromReviewSource(job.Provider, source, policy);
    }

    /// <summary>Uses the provider's local namespace projection.</summary>
    public static ProviderSourceIdentity FromReviewSource(ScmProvider provider, string sourcePath, IReviewSourcePolicy policy)
    {
        return FromConfiguredHost(provider, policy.NormalizeNamespace(sourcePath));
    }

    /// <summary>Retains the configured base path for provider-native identifiers.</summary>
    public static ProviderSourceIdentity FromConfiguredHost(ScmProvider provider, string host)
    {
        var value = Uri.TryCreate(host, UriKind.Absolute, out var uri)
            ? $"{provider}:{uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/')}"
            : string.Empty;
        return new(provider, value);
    }
}
