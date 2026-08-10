// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>
///     Offer candidate selection against PostgreSQL.
///     <para>
///         Scope, tags, and fairness are all decided in the one statement that reads the candidates. Doing
///         any of it in memory after a LIMIT would silently starve whatever the limit cut off, which for the
///         scope filter would not be a fairness problem but a correctness one.
///     </para>
/// </summary>
public sealed class RunnerLeaseOfferStore(MeisterProPRDbContext dbContext) : IRunnerLeaseOfferStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetOfferCandidatesAsync(
        Guid tenantId,
        IReadOnlyList<Guid> clientScope,
        IReadOnlyList<string> runnerTags,
        int limit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientScope);
        ArgumentNullException.ThrowIfNull(runnerTags);

        // A non-positive limit is a request for nothing, which is what JobRepository's claim candidates
        // already answer. Passed through it would reach LIMIT and raise a database error instead.
        if (limit <= 0)
        {
            return [];
        }

        // An empty stamped scope means every client the runner may serve, so the scope predicate is written
        // to be satisfied by cardinality rather than by a separate query shape.
        //
        // The tenant predicate is the boundary the whole enrollment design exists to keep, and a runner
        // enrolled in a tenant never escapes it. The System tenant is the one exception, and it is not a
        // hole in the rule so much as the rule applied to a tenant that belongs to the installation rather
        // than to a customer: only platform administrators can enroll into it, so a host there is the
        // operator's own, and one shared pool is what an installation running many small tenants can
        // actually staff. Such a runner is offered every tenant's work; its stamped scope, if it has one,
        // still narrows which clients.
        var servesEveryTenant = TenantCatalog.IsSystemTenant(tenantId);
        var scope = clientScope.ToArray();
        var tags = runnerTags.Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToArray();

        // row_number per client, ordered by that client's own queue, then read back in (rank, age) order:
        // every client's oldest job comes before any client's second-oldest. A client that queues two
        // hundred pull requests therefore takes one slot per round rather than the whole pool.
        var ids = await dbContext.Database
            .SqlQueryRaw<Guid>(
                """
                SELECT id AS "Value" FROM (
                    SELECT j.id,
                           j.submitted_at,
                           row_number() OVER (PARTITION BY j.client_id ORDER BY j.submitted_at, j.id) AS client_rank
                    FROM review_jobs j
                    JOIN clients c ON c.id = j.client_id
                    WHERE j.status = 'Pending'
                      AND c.is_active
                      AND ({4} OR c.tenant_id = {0})
                      AND (cardinality({1}::uuid[]) = 0 OR j.client_id = ANY({1}::uuid[]))
                      AND (
                            coalesce(btrim(c.required_runner_tags), '') = ''
                            OR (
                                SELECT bool_and(btrim(lower(required)) = ANY({2}::text[]))
                                FROM unnest(string_to_array(lower(c.required_runner_tags), ',')) AS required
                                WHERE btrim(required) <> ''
                            )
                          )
                ) ranked
                ORDER BY ranked.client_rank, ranked.submitted_at, ranked.id
                LIMIT {3}
                """,
                tenantId,
                scope,
                tags,
                limit,
                servesEveryTenant)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            return [];
        }

        // Read back through the entity set so callers get the same shape the in-process claim path works
        // with, then restore the order the ranking decided, which the IN lookup does not preserve.
        var jobs = await dbContext.ReviewJobs
            .AsNoTracking()
            .Where(j => ids.Contains(j.Id))
            .ToListAsync(ct);

        var byId = jobs.ToDictionary(j => j.Id);
        return [.. ids.Where(byId.ContainsKey).Select(id => byId[id])];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnroutableJob>> GetUnroutableJobsAsync(
        DateTimeOffset activeSince,
        int limit,
        CancellationToken ct = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        // The mirror image of the candidate query: a pending job whose client requires tags, where no
        // active runner in the tenant declares all of them. Written as NOT EXISTS rather than by comparing
        // counts, because a tenant with no active runners at all should report its tagged jobs as
        // unroutable too, and a count comparison would quietly report nothing.
        var rows = await dbContext.Database
            .SqlQueryRaw<UnroutableJobRow>(
                """
                SELECT j.id AS "JobId",
                       j.client_id AS "ClientId",
                       c.required_runner_tags AS "RequiredTags",
                       j.submitted_at AS "PendingSince"
                FROM review_jobs j
                JOIN clients c ON c.id = j.client_id
                WHERE j.status = 'Pending'
                  AND coalesce(btrim(c.required_runner_tags), '') <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM review_runners r
                      WHERE (r.tenant_id = c.tenant_id OR r.tenant_id = {2})
                        AND r.state = 'Enrolled'
                        AND r.last_seen_at >= {0}
                        AND (cardinality(r.client_scope) = 0 OR c.id = ANY(r.client_scope))
                        AND (
                            SELECT bool_and(btrim(lower(required)) = ANY(
                                SELECT lower(btrim(declared)) FROM unnest(r.tags) AS declared))
                            FROM unnest(string_to_array(lower(c.required_runner_tags), ',')) AS required
                            WHERE btrim(required) <> ''
                        )
                  )
                ORDER BY j.submitted_at
                LIMIT {1}
                """,
                activeSince,
                limit,
                TenantCatalog.SystemTenantId)
            .ToListAsync(ct);

        return
        [
            .. rows.Select(r => new UnroutableJob(
                r.JobId,
                r.ClientId,
                [.. (r.RequiredTags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                r.PendingSince)),
        ];
    }

    /// <inheritdoc />
    public async Task<RunnerFleetSnapshot> GetFleetSnapshotAsync(
        DateTimeOffset activeSince,
        int oldestSupportedContractVersion,
        int newestSupportedContractVersion,
        CancellationToken ct = default)
    {
        // Both counts come from one scan so they describe the same instant. Read as two statements, a
        // runner enrolling between them is enough to show zero enrolled and a positive active count at
        // once, and the caller concludes the fleet is empty while it is not.
        var counts = await dbContext.Database
            .SqlQueryRaw<FleetCountsRow>(
                """
                SELECT count(*)::int AS "RegisteredRunnerCount",
                       count(*) FILTER (
                           WHERE r.last_seen_at >= {0}
                             AND r.contract_version BETWEEN {1} AND {2}
                       )::int AS "ActiveRunnerCount"
                FROM review_runners r
                WHERE r.state = 'Enrolled'
                """,
                activeSince,
                oldestSupportedContractVersion,
                newestSupportedContractVersion)
            .ToListAsync(ct);

        var registered = counts.Count == 0 ? 0 : counts[0].RegisteredRunnerCount;
        var active = counts.Count == 0 ? 0 : counts[0].ActiveRunnerCount;

        if (active == 0)
        {
            return new RunnerFleetSnapshot(registered, 0, new HashSet<Guid>());
        }

        // Which clients an active runner could actually be offered work for: tenant and stamped scope, and
        // deliberately not tags. A job whose tags nothing declares must stay pending and surface as
        // unroutable rather than quietly falling back into the control plane.
        var eligibleClients = await dbContext.Database
            .SqlQueryRaw<Guid>(
                """
                SELECT DISTINCT c.id AS "Value"
                FROM clients c
                JOIN review_runners r ON (r.tenant_id = c.tenant_id OR r.tenant_id = {3})
                WHERE c.is_active
                  AND r.state = 'Enrolled'
                  AND r.last_seen_at >= {0}
                  AND r.contract_version BETWEEN {1} AND {2}
                  AND (cardinality(r.client_scope) = 0 OR c.id = ANY(r.client_scope))
                """,
                activeSince,
                oldestSupportedContractVersion,
                newestSupportedContractVersion,
                TenantCatalog.SystemTenantId)
            .ToListAsync(ct);

        return new RunnerFleetSnapshot(registered, active, eligibleClients.ToHashSet());
    }

    /// <inheritdoc />
    public async Task<int> CountRunnersHoldingLeasesAsync(CancellationToken ct = default)
    {
        // Distinct owners, not leased jobs: a runner given several jobs consumes one slot, which is what an
        // entitlement counted in runners has to mean.
        var counts = await dbContext.Database
            .SqlQueryRaw<int>(
                """
                SELECT count(DISTINCT j.lease_owner)::int AS "Value"
                FROM review_jobs j
                WHERE j.status = 'Processing'
                  AND j.lease_owner IS NOT NULL
                  AND j.lease_expires_at > now()
                  AND EXISTS (SELECT 1 FROM review_runners r WHERE r.id::text = j.lease_owner)
                """)
            .ToListAsync(ct);

        return counts.Count == 0 ? 0 : counts[0];
    }

    /// <summary>Row shape for the one-statement fleet count.</summary>
    private sealed record FleetCountsRow(int RegisteredRunnerCount, int ActiveRunnerCount);

    /// <summary>Row shape for the unroutable-job query. Split out because the tags arrive as stored text.</summary>
    private sealed record UnroutableJobRow(Guid JobId, Guid ClientId, string? RequiredTags, DateTimeOffset PendingSince);
}
