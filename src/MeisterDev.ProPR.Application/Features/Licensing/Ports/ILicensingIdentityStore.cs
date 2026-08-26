// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the identifier an installation reports itself under.
///     <para>
///         The identifier exists so that usage reports from different installations can be told apart and so
///         that a support conversation has something stable to quote. It grants nothing: no license
///         verification, resolution or activation reads it, no license is issued against it, and deleting the
///         row leaves every stored license and every entitlement exactly as it was. An installation that wants
///         to report itself as a different one deletes the row; the next read creates a new identifier.
///     </para>
///     <para>
///         It is generated locally from random bytes and is never derived from the machine, the network, the
///         database or anything else about the environment, so it carries nothing about the host it runs on.
///     </para>
/// </summary>
public interface ILicensingIdentityStore
{
    /// <summary>
    ///     Returns the installation's identifier, creating it on the first read.
    ///     <para>
    ///         Replicas reading for the first time at the same moment settle on one identifier, so an
    ///         installation reports itself under a single value however many replicas start together.
    ///     </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifier this installation reports itself under.</returns>
    Task<Guid> GetOrCreateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Returns when the identifier was created, which is the instant the installation was first seen. Unlike
    ///     <see cref="GetOrCreateAsync" /> this creates nothing, so a caller that only wants to describe the
    ///     installation does not mint an identifier for one that has none.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The instant, or <see langword="null" /> when the installation has no identifier yet.</returns>
    Task<DateTimeOffset?> GetCreatedAtAsync(CancellationToken cancellationToken = default);
}
