// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

/// <summary>Decodes retained provider identifiers without resolving operational capabilities.</summary>
public sealed class ScmProviderCompatibilityCodec : IScmProviderCompatibilityCodec
{
    private static readonly IReadOnlyDictionary<ScmProvider, string> Names = Enum.GetValues<ScmProvider>().ToDictionary(
        provider => provider, provider => typeof(ScmProvider).GetField(provider.ToString())!
            .GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? provider.ToString());

    public CodeReviewSourceContext PrepareSynchronizationJobSource(CodeReviewRef? capturedReview, string scopePath, string projectKey, int reviewNumber) =>
        capturedReview is not null
            ? CodeReviewSourceContext.FromReview(capturedReview)
            : new(
                ScmProvider.AzureDevOps, scopePath, projectKey, projectKey,
                CodeReviewPlatformKind.PullRequest, reviewNumber.ToString());

    public MeisterDev.ProPR.ProCursor.Contracts.Sources.CanonicalSourceReferenceDto ResolveCanonicalSourceReference(
        string? provider, string? value, string repositoryId) =>
        MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.CanonicalSourceProjection.Resolve(provider, value, repositoryId);

    public bool TryFromWebhook(WebhookProviderType provider, out ScmProvider mapped) => TryMapWebhook(provider, out mapped);

    public static bool TryMapWebhook(WebhookProviderType provider, out ScmProvider mapped)
    {
        ScmProvider? candidate = provider switch
        {
            WebhookProviderType.AzureDevOps => ScmProvider.AzureDevOps,
            WebhookProviderType.GitHub => ScmProvider.GitHub,
            WebhookProviderType.GitLab => ScmProvider.GitLab,
            WebhookProviderType.Forgejo => ScmProvider.Forgejo,
            _ => null,
        };
        mapped = candidate.GetValueOrDefault();
        return candidate.HasValue;
    }

    public ScmProvider ResolveCoverageProvider(WebhookProviderType provider) =>
        this.TryFromWebhook(provider, out var mapped) ? mapped : ScmProvider.AzureDevOps;

    public bool TryParseRecordedCanonicalProvider(string? value, out ScmProvider provider)
    {
        foreach (var entry in Names)
        {
            if (string.Equals(value, entry.Value, StringComparison.OrdinalIgnoreCase))
            {
                provider = entry.Key;
                return true;
            }
        }

        provider = default;
        return false;
    }

    public ThreadResolutionIntent DecodeStoredThreadResolution(string? status) => SavedThreadStatusCompatibilityDecoder.Decode(status);
}
