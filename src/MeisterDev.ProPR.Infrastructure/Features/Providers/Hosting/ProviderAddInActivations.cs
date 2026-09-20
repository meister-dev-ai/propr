// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.Ai.Providers.AddIns;
using MeisterDev.ProPR.Infrastructure.Data.Models;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     The add-in binaries this installation's administrators have allowed the host to run.
/// </summary>
/// <remarks>
///     <para>
///         Held in memory, because the loader asks for every candidate and a database round trip per file would
///         put the discovery pass behind the connection pool. The set is small: one row per add-in version an
///         administrator has ever approved.
///     </para>
///     <para>
///         It starts empty and is filled by the startup step that runs after the schema has been brought up to
///         date. The discovery pass happens before that, deliberately: it reads no database, so a composition
///         error is reported before a live installation's schema is touched. Every external add-in is therefore
///         described and none is loaded at that point, and the step below loads the ones with a row.
///     </para>
///     <para>
///         Activating an add-in after the host has started adds to the set here and loads that one add-in, so
///         the answer and the registry change together and neither waits for a restart.
///     </para>
/// </remarks>
public sealed class ProviderAddInActivations : IProviderAddInActivations
{
    private readonly Lock _gate = new();
    private FrozenSet<string> _activated;

    /// <summary>Reads the activations this installation holds.</summary>
    /// <param name="hashes">The content hashes that have been activated.</param>
    public ProviderAddInActivations(IEnumerable<string> hashes)
    {
        ArgumentNullException.ThrowIfNull(hashes);

        this._activated = hashes.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsActivated(string? contentHash)
    {
        if (contentHash is null)
        {
            return false;
        }

        lock (this._gate)
        {
            return this._activated.Contains(contentHash);
        }
    }

    /// <summary>Records that these bytes are now activated, so the gate answers for them from here on.</summary>
    /// <param name="contentHash">The activated hash.</param>
    internal void Include(string contentHash)
    {
        lock (this._gate)
        {
            this._activated = this._activated.Append(contentHash).ToFrozenSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Records that these bytes are no longer activated.</summary>
    /// <param name="contentHash">The revoked hash.</param>
    /// <remarks>
    ///     The add-in stays loaded until the host restarts. An assembly cannot be unloaded from under the
    ///     connections using it, and a review holding a client built by that family is mid-flight. What revoking
    ///     does is stop the next start taking it, which the page says.
    /// </remarks>
    internal void Exclude(string contentHash)
    {
        lock (this._gate)
        {
            this._activated = this._activated.Where(hash => !string.Equals(hash, contentHash, StringComparison.Ordinal))
                .ToFrozenSet(StringComparer.Ordinal);
        }
    }
}
