// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Answers whether the installation is licensed for the capability a provider family declared, without
///     reaching the database on every call.
/// </summary>
/// <remarks>
///     <para>
///         This is the licence check on the review path, and it is the one the others do not cover: the checks
///         made when a connection is written, when an action is dispatched and when a credential an action
///         produced is stored all fire on configuration. Without a check on use, an installation whose
///         entitlement lapsed keeps running reviews on the credentials it already stored.
///     </para>
///     <para>
///         A credential is read once per outbound provider request and a review issues those in parallel, so the
///         answer is held for a short window rather than resolved per request. The capability service is scoped
///         because it holds a database context, and this outlives any scope, so it resolves one of its own for
///         each refresh — the same shape the cached licence state uses, and for the same reason.
///     </para>
///     <para>
///         A key the installation's catalogue does not carry is unavailable rather than an error. A family
///         declares its capability key itself, so an unknown one is a family asking for something this
///         installation cannot be entitled to, and refusing keeps a credential from being handed out on a claim
///         the host cannot check.
///     </para>
/// </remarks>
/// <param name="scopeFactory">Resolves the capability service per refresh.</param>
/// <param name="timeProvider">Drives the cache expiry.</param>
public sealed class ProviderAddInCapabilityGate(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null) : IProviderAddInCapabilityGate
{
    /// <summary>
    ///     How long an answer is reused. Long enough that a review's parallel passes share one read, short
    ///     enough that an entitlement that lapses stops reviews inside a minute.
    /// </summary>
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CachedAnswer> _answers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    /// <inheritdoc />
    public async ValueTask<bool> IsAvailableAsync(string? capabilityKey, CancellationToken ct = default)
    {
        // A family that declares no capability requires none, which is every family the product ships.
        if (string.IsNullOrWhiteSpace(capabilityKey))
        {
            return true;
        }

        var now = this._timeProvider.GetUtcNow();
        if (this._answers.TryGetValue(capabilityKey, out var cached) && cached.FreshUntil > now)
        {
            return cached.IsAvailable;
        }

        // Not gated: two callers arriving together both read, which costs one extra query and cannot produce
        // a wrong answer. A gate here would put every parallel pass of a review behind one refresh.
        return await this.RefreshAsync(capabilityKey, now, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsAvailableNowAsync(string? capabilityKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(capabilityKey))
        {
            return true;
        }

        return await this.RefreshAsync(capabilityKey, this._timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
    }

    /// <summary>Resolves one answer and records it, unless a later read has already recorded one.</summary>
    /// <remarks>
    ///     Ordered by when the read started rather than when it finished, so a slow read that began before a
    ///     revocation cannot overwrite the answer of a read that began after it. Two refreshes of one key overlap
    ///     whenever a held answer expires while several callers are in flight, and without the ordering the one
    ///     that happened to return last would stand for the next minute.
    /// </remarks>
    /// <param name="capabilityKey">The key being resolved.</param>
    /// <param name="startedAt">When this read began, and that orders it against another.</param>
    /// <param name="ct">Cancels the read.</param>
    private async ValueTask<bool> RefreshAsync(string capabilityKey, DateTimeOffset startedAt, CancellationToken ct)
    {
        var available = await this.ResolveAsync(capabilityKey, ct).ConfigureAwait(false);
        var resolved = new CachedAnswer(available, startedAt + CacheDuration, startedAt);

        this._answers.AddOrUpdate(
            capabilityKey,
            resolved,
            (_, existing) => existing.StartedAt > resolved.StartedAt ? existing : resolved);

        return available;
    }

    private async Task<bool> ResolveAsync(string capabilityKey, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var capabilities = scope.ServiceProvider.GetRequiredService<ILicensingCapabilityService>();

        try
        {
            return await capabilities.IsEnabledAsync(capabilityKey, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private readonly record struct CachedAnswer(
        bool IsAvailable,
        DateTimeOffset FreshUntil,
        DateTimeOffset StartedAt);
}
