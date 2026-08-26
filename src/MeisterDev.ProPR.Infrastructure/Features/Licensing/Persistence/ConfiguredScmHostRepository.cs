// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>
///     Reads the SCM connection base URLs configured across every client.
///     <para>
///         The caller normalizes and hashes them. Nothing here is stored, and the plain URL does not leave this
///         read.
///     </para>
/// </summary>
public sealed class ConfiguredScmHostRepository(MeisterProPRDbContext dbContext) : IConfiguredScmHostSource
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListHostBaseUrlsAsync(CancellationToken cancellationToken = default)
    {
        var hostBaseUrls = await dbContext.ClientScmConnections
            .AsNoTracking()
            .Select(connection => connection.HostBaseUrl)
            .Distinct()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return hostBaseUrls.AsReadOnly();
    }
}
