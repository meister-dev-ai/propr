// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>Used solely for identity verification (FR-015). Never for ADO API operations.</summary>
public interface IAdoTokenValidator
{
    /// <summary>
    ///     Validates the given ADO token for basic identity checks.
    ///     When <paramref name="orgUrl" /> is provided the token is validated against that
    ///     organisation's VSSPS endpoint (required for browser-extension session tokens).
    ///     Omit it for PAT-based clients, which validate against the global VSSPS endpoint.
    /// </summary>
    Task<bool> IsValidAsync(string adoToken, string? orgUrl = null, CancellationToken ct = default);
}
