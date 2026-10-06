// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

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
    public static ProviderSourceIdentity FromReviewJob(ReviewJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var source = job.Provider == ScmProvider.AzureDevOps
            ? job.OrganizationUrl
            : job.HostBaseUrl ?? job.OrganizationUrl;
        return FromReviewSource(job.Provider, source);
    }

    /// <summary>Uses authority identity except for self-hosted Azure DevOps collection paths.</summary>
    public static ProviderSourceIdentity FromReviewSource(ScmProvider provider, string sourcePath)
    {
        if (!Uri.TryCreate(sourcePath, UriKind.Absolute, out var uri))
        {
            return new(provider, string.Empty);
        }

        var host = new ProviderHostRef(provider, sourcePath).HostBaseUrl;
        if (provider == ScmProvider.AzureDevOps && !IsHostedAzureDevOps(uri))
        {
            host = sourcePath;
        }

        return FromConfiguredHost(provider, host);
    }

    /// <summary>Retains the configured base path for provider-native identifiers.</summary>
    public static ProviderSourceIdentity FromConfiguredHost(ScmProvider provider, string host)
    {
        var value = Uri.TryCreate(host, UriKind.Absolute, out var uri)
            ? $"{provider}:{uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/')}"
            : string.Empty;
        return new(provider, value);
    }

    private static bool IsHostedAzureDevOps(Uri uri)
    {
        return string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }
}
