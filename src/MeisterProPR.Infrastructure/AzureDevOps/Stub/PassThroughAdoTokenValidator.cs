// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     Development-only validator that accepts any non-empty ADO token without
///     calling the real VSS endpoint. Activated by setting
///     <c>ADO_SKIP_TOKEN_VALIDATION=true</c> in configuration (e.g. user secrets).
///     Never register this in production.
/// </summary>
internal sealed class PassThroughAdoTokenValidator : IAdoTokenValidator
{
    public Task<bool> IsValidAsync(string adoToken, string? orgUrl = null, CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrWhiteSpace(adoToken));
    }
}
