// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.ProCursor.Core;

/// <summary>Projects saved canonical-source fields with the existing providerless compatibility fallback.</summary>
public static class HistoricalProCursorSourceProjection
{
    public static CanonicalSourceReferenceDto GetCanonicalReference(string? provider, string? value, string repositoryId) =>
        !string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(value)
            ? new(provider, value)
            : new("azureDevOps", repositoryId);
}
