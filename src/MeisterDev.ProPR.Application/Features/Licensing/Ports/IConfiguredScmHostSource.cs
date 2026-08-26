// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Reads the SCM hosts the installation is configured against, across every client.
/// </summary>
public interface IConfiguredScmHostSource
{
    /// <summary>
    ///     Returns the base URL of every configured SCM connection. Connections are included whether or not
    ///     they are currently active, so switching one off does not read as the installation having moved.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The configured connection URLs, which the caller normalizes and hashes.</returns>
    Task<IReadOnlyList<string>> ListHostBaseUrlsAsync(CancellationToken cancellationToken = default);
}
