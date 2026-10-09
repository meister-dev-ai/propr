// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>Projects stored source references with the historical repository fallback.</summary>
public static class CanonicalSourceProjection
{
    public static CanonicalSourceReferenceDto Resolve(string? provider, string? value, string repositoryId) =>
        !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(value)
            ? new CanonicalSourceReferenceDto(provider, value)
            : new CanonicalSourceReferenceDto("azureDevOps", repositoryId);
}
