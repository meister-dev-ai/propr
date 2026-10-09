// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Decodes retained boundary identifiers and providerless evidence into normalized data.</summary>
public interface IScmProviderCompatibilityCodec
{
    CodeReviewSourceContext PrepareSynchronizationJobSource(CodeReviewRef? capturedReview, string scopePath, string projectKey, int reviewNumber);
    CanonicalSourceReferenceDto ResolveCanonicalSourceReference(string? provider, string? value, string repositoryId);
    bool TryFromWebhook(WebhookProviderType provider, out ScmProvider mapped);
    bool TryParseRecordedCanonicalProvider(string? value, out ScmProvider provider);
    ScmProvider ResolveCoverageProvider(WebhookProviderType provider);
    ThreadResolutionIntent DecodeStoredThreadResolution(string? status);
}
