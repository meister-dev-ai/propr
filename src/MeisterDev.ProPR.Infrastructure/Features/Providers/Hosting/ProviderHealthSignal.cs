// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Records what a provider family reported about the credential of the one connection it was handed.
/// </summary>
/// <remarks>
///     <para>
///         The value set is the host's and is closed, so a family names a member and cannot invent one. What is
///         written here is the family's report and not the connection's answer: a fresh verification outranks it
///         and the precedence is applied when the health is read.
///     </para>
///     <para>
///         A report never activates a connection. Activation still needs a verification the host ran itself,
///         which is the one thing a family's word cannot stand in for.
///     </para>
/// </remarks>
/// <param name="binding">The connection this handle reports against.</param>
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
/// <param name="secrets">
///     The credential values held for this connection, which the cause is scrubbed against before it is stored.
/// </param>
/// <param name="timeProvider">Supplies the instant the report is stamped with.</param>
public sealed class ProviderHealthSignal(
    ProviderAddInBinding binding,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    Func<CancellationToken, Task<IReadOnlyCollection<string>>> secrets,
    TimeProvider timeProvider) : IProviderHealthSignal
{
    /// <inheritdoc />
    public async Task ReportAsync(
        AiCredentialHealth health,
        string? cause = null,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(health))
        {
            throw new ArgumentOutOfRangeException(
                nameof(health),
                health,
                "Credential health is a member of the host's set. A value outside it would put a state in the "
                + "column that no host logic can key on and no console can render.");
        }

        var scrubbed = ProviderMessageGuard.Sanitize(
            cause,
            await secrets(ct).ConfigureAwait(false));
        var reportedAt = timeProvider.GetUtcNow();
        var stored = health.ToString();

        // Conditional on what is recorded, so a report cannot replace one made after it. A family reports from
        // whichever replica served its call and the reports arrive in whatever order the calls finished; an
        // unconditional write lets a slow report of a state the connection has left overwrite the current one,
        // and the column is what the console reads and what the health resolver weighs against a verification.
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AiConnectionProfiles
            .Where(profile => profile.Id == binding.ConnectionProfileId
                              && (profile.CredentialHealthChangedAt == null
                                  || profile.CredentialHealthChangedAt <= reportedAt))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(profile => profile.CredentialHealth, stored)
                    .SetProperty(profile => profile.CredentialHealthCause, scrubbed)
                    .SetProperty(profile => profile.CredentialHealthChangedAt, reportedAt),
                ct)
            .ConfigureAwait(false);
    }
}
