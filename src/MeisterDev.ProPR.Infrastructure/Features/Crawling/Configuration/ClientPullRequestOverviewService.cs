// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.DataProtection;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using System.Security.Cryptography;
using System.Text.Json;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration;

/// <summary>Client-scoped rich discovery pages.</summary>
public sealed class ClientPullRequestOverviewService(
    IClientPullRequestOverviewStore store,
    IClientRegistry clients,
    ICrawlConfigurationRepository configurations,
    IClientScmConnectionRepository connections,
    IClientScmScopeRepository scopes,
    IScmProviderRegistry providers,
    IServiceScopeFactory scopeFactory,
    IDataProtectionProvider protection,
    TimeProvider clock) : IClientPullRequestOverviewService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDataProtector CursorProtector => protection.CreateProtector("ProPR.ClientPullRequestOverview.Cursor.v1");

    public async Task<ClientPullRequestOverviewPage> ReadAsync(Guid clientId, ClientPullRequestOverviewRequest request, CancellationToken ct)
    {
        ValidateRequest(request);

        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await ReadSnapshotAsync(clientId, request, ct);
            }
            catch (SnapshotInvalidatedException)
            {
                await AuthorizeAsync(clientId, SelectSources(request), ct);
                ct.ThrowIfCancellationRequested();
                if (request.Cursor is not null || attempt >= 1)
                {
                    throw new ClientPullRequestOverviewException("obsolete");
                }
                // A completed denial changes cursor ownership. Retry once using its persisted cooldowns and cleared observations.
            }
            catch (ClientPullRequestOverviewException)
            {
                await AuthorizeAsync(clientId, SelectSources(request), ct);
                ct.ThrowIfCancellationRequested();
                _ = clock.GetUtcNow();
                throw;
            }
        }
    }

    private static void ValidateRequest(ClientPullRequestOverviewRequest request)
    {
        if (request.Sources is null || request.Sources.Count > 10000 ||
            request.Sources.Any(source => source is null || source.TargetId == Guid.Empty || source.ConnectionId == Guid.Empty))
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        if (request.Page < 1 || request.PageSize is < 1 or > 100 || (long)(request.Page - 1) * request.PageSize > 10000 ||
            request.Page > 1 && request.Cursor is null)
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        if (string.IsNullOrWhiteSpace(request.Binding) || request.Binding.Length > 128 || request.Cursor?.Length > 4096)
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        if (request.Sources.Distinct().Count() != request.Sources.Count)
        {
            throw new ClientPullRequestOverviewException("invalid");
        }
    }

    private static ClientPullRequestOverviewSource[] SelectSources(ClientPullRequestOverviewRequest request) =>
        request.Sources.OrderBy(source => source.TargetId).ThenBy(source => source.ConnectionId).Take(100).ToArray();

    private async Task<ClientPullRequestOverviewPage> ReadSnapshotAsync(Guid clientId, ClientPullRequestOverviewRequest request, CancellationToken ct)
    {
        var selected = SelectSources(request);
        var authority = await AuthorizeAsync(clientId, selected, ct);
        var invalidation = await store.ReadInvalidationAsync(clientId, ct);
        var binding = Hash(
            new { request.Binding, Sources = authority.Select(source => new { source.Pair, source.Fingerprint }), request.Sources.Count, invalidation });
        var now = clock.GetUtcNow();
        Snapshot? previous = null;
        Guid generation;
        if (request.Cursor is not null)
        {
            generation = UnprotectGeneration(request.Cursor);
            previous = await RestoreSnapshotAsync(clientId, generation, binding, now, ct);

            if (!request.LoadMore && !request.Reload)
            {
                await RecheckAsync(clientId, selected, authority, ct);
                return await ReadPageAsync(
                    clientId, previous, request.Cursor, generation, binding, request.Page, request.PageSize, authority, invalidation, ct);
            }
        }

        var offset = request.LoadMore ? previous?.AttemptedSources ?? throw new ClientPullRequestOverviewException("invalid") : 0;
        if (offset > authority.Count || request.LoadMore && request.Reload)
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        var batch = authority.Skip(offset).Take(8).ToArray();
        var results = await ReadSourceBatchAsync(clientId, batch, ct);
        await RecheckAsync(clientId, selected, authority, ct);
        await RecheckInvalidationAsync(clientId, invalidation, ct);
        if (previous?.ExpiresAt <= clock.GetUtcNow())
        {
            if (request.Cursor is not null)
            {
                throw new ClientPullRequestOverviewException("obsolete");
            }

            previous = null;
        }

        var (observations, sourceOutcomes) = MergeObservations(previous, batch, results, request.Reload);

        var map = authority.ToDictionary(source => source.Pair);
        if (observations.Count > 10000)
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        var rows = SelectRows(observations, map);
        now = clock.GetUtcNow();
        var snapshot = CreateSnapshot(request, previous, observations, sourceOutcomes, rows, offset + batch.Length, now);
        var equivalence = Hash(
            new
                { binding, snapshot.Items, snapshot.Observations, snapshot.Sources, snapshot.TotalSources, snapshot.AttemptedSources, snapshot.Status });
        var retained = await store.GetOrCreateGenerationAsync(
            clientId, binding, equivalence, JsonSerializer.Serialize(snapshot, Json), snapshot.ExpiresAt, now, ct);
        if (retained is null)
        {
            await FinalCheckAsync(clientId, authority, invalidation, snapshot.ExpiresAt, ct);
            return BoundedResponse(Capacity(Page(snapshot, "", 1, request.PageSize, clock.GetUtcNow())));
        }

        generation = retained.Id;
        snapshot = SnapshotFrom(retained);
        return await ReadPageAsync(
            clientId, snapshot, CursorProtector.Protect(generation.ToString("N")), generation, binding, 1, request.PageSize, authority, invalidation, ct);
    }

    private Guid UnprotectGeneration(string cursor)
    {
        try
        {
            return Guid.ParseExact(CursorProtector.Unprotect(cursor), "N");
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException)
        {
            throw new ClientPullRequestOverviewException("obsolete");
        }
    }

    private async Task<Snapshot> RestoreSnapshotAsync(Guid clientId, Guid generation, string binding, DateTimeOffset now, CancellationToken ct)
    {
        var saved = await store.ReadGenerationStateAsync(clientId, generation, binding, now, ct)
                    ?? throw new ClientPullRequestOverviewException("obsolete");
        var snapshot = SnapshotFrom(saved);
        if (snapshot.Observations is null)
        {
            throw new ClientPullRequestOverviewException("obsolete");
        }

        return snapshot;
    }

    private async Task<SourceResult[]> ReadSourceBatchAsync(Guid clientId, IReadOnlyList<AuthorizedSource> batch, CancellationToken ct)
    {
        // Each refresh worker owns a separate DI scope. ORM reads in the request remain sequential.
        using var concurrency = new SemaphoreSlim(4);
        return await Task.WhenAll(
            batch.Select(async source =>
            {
                await concurrency.WaitAsync(ct);
                try
                {
                    return await ReadSourceAsync(clientId, source, ct);
                }
                finally
                {
                    concurrency.Release();
                }
            }));
    }

    private (List<ClientPullRequestOverviewRow> Observations, ClientPullRequestOverviewOutcome[] Outcomes) MergeObservations(
        Snapshot? previous, IReadOnlyList<AuthorizedSource> batch, IReadOnlyList<SourceResult> results, bool reload)
    {
        var refreshedPairs = batch.Select(source => source.Pair).ToHashSet();
        var observations = new List<ClientPullRequestOverviewRow>();
        if (previous is not null)
        {
            foreach (var row in previous.Observations.Where(row => row.ListedAt > clock.GetUtcNow().AddMinutes(-15)))
            {
                if (!refreshedPairs.Contains(new(row.TargetId, row.ConnectionId)))
                {
                    observations.Add(row);
                }
            }
        }

        var outcomes = previous?.Sources.ToDictionary(
            outcome => new ClientPullRequestOverviewSource(outcome.TargetId, outcome.ConnectionId),
            outcome => reload ? outcome with { Status = "pending" } : outcome) ?? [];
        foreach (var result in results)
        {
            observations.AddRange(result.Rows);
            outcomes[new(result.Outcome.TargetId, result.Outcome.ConnectionId)] = result.Outcome;
        }

        return (observations, outcomes.Values.OrderBy(outcome => outcome.TargetId).ThenBy(outcome => outcome.ConnectionId).ToArray());
    }

    private List<ClientPullRequestOverviewRow> SelectRows(
        IReadOnlyList<ClientPullRequestOverviewRow> observations, IReadOnlyDictionary<ClientPullRequestOverviewSource, AuthorizedSource> map) =>
        observations.GroupBy(row => RepositoryKey(map[new(row.TargetId, row.ConnectionId)], row.Number))
            .Select(group => SelectRepresentative(group, map))
            .OrderBy(row => map[new(row.TargetId, row.ConnectionId)].Target.RepoFilters[0].RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => RepositoryKey(map[new(row.TargetId, row.ConnectionId)], 0), StringComparer.Ordinal)
            .ThenByDescending(row => row.Number).Take(10000).ToList();

    private static ClientPullRequestOverviewRow SelectRepresentative(
        IEnumerable<ClientPullRequestOverviewRow> group, IReadOnlyDictionary<ClientPullRequestOverviewSource, AuthorizedSource> map)
    {
        var selected = group.OrderByDescending(row => row.ListedAt).ThenBy(row => row.TargetId).ThenBy(row => row.ConnectionId)
            .ThenBy(row => row.ProviderRevisionId, StringComparer.Ordinal).ThenBy(row => row.HeadSha, StringComparer.Ordinal)
            .ThenBy(row => row.Title, StringComparer.Ordinal).ThenBy(row => row.SourceBranch, StringComparer.Ordinal)
            .ThenBy(row => row.TargetBranch, StringComparer.Ordinal).ThenBy(row => row.AuthorName, StringComparer.Ordinal)
            .ThenBy(row => row.State, StringComparer.Ordinal).ThenBy(row => row.WebUrl, StringComparer.Ordinal).First();
        var associations = group.SelectMany(row => row.Associations.Count == 0
                ? new[] { new ClientPullRequestOverviewSource(row.TargetId, row.ConnectionId) }
                : row.Associations)
            .Where(map.ContainsKey).Distinct().OrderBy(pair => pair.TargetId).ThenBy(pair => pair.ConnectionId).ToArray();
        return selected with { Associations = associations };
    }

    private static Snapshot CreateSnapshot(
        ClientPullRequestOverviewRequest request, Snapshot? previous, IReadOnlyList<ClientPullRequestOverviewRow> observations,
        IReadOnlyList<ClientPullRequestOverviewOutcome> sourceOutcomes, IReadOnlyList<ClientPullRequestOverviewRow> rows,
        int attemptedSources, DateTimeOffset now)
    {
        var listingDeadline = sourceOutcomes.Where(outcome => outcome.ListedAt.HasValue).Select(outcome => outcome.ListedAt!.Value)
            .Concat(observations.Select(row => row.ListedAt)).Select(observed => observed.AddMinutes(15))
            .Append(previous?.ExpiresAt ?? now.AddMinutes(15)).Min();
        var status = "available";
        if (request.Sources.Count > 100)
        {
            status = "capacity";
        }
        else if (attemptedSources < request.Sources.Count || sourceOutcomes.Any(outcome => outcome.Status != "loaded"))
        {
            status = "incomplete";
        }

        return new Snapshot(rows, observations, sourceOutcomes, request.Sources.Count, attemptedSources, now, listingDeadline, status);
    }

    private async Task<IReadOnlyList<AuthorizedSource>> AuthorizeAsync(
        Guid clientId, IReadOnlyList<ClientPullRequestOverviewSource> pairs, CancellationToken ct)
    {
        var tenant = await clients.GetTenantIdAsync(clientId, ct) ?? throw new ClientPullRequestOverviewException("accessDenied");
        var result = new List<AuthorizedSource>();
        var targetReads = new Dictionary<Guid, CrawlConfigurationDto>();
        var connectionReads = new Dictionary<Guid, ClientScmConnectionDto>();
        var scopeReads = new Dictionary<Guid, IReadOnlyList<ClientScmScopeDto>>();
        foreach (var pair in pairs)
        {
            if (!targetReads.TryGetValue(pair.TargetId, out var target))
            {
                targetReads[pair.TargetId] = target = await configurations.GetReviewTargetPolicySnapshotAsync(pair.TargetId, clientId, ct)
                                                      ?? throw new ClientPullRequestOverviewException("accessDenied");
            }

            if (!connectionReads.TryGetValue(pair.ConnectionId, out var connection))
            {
                connectionReads[pair.ConnectionId] = connection = await connections.GetByIdAsync(clientId, pair.ConnectionId, ct)
                                                                  ?? throw new ClientPullRequestOverviewException("accessDenied");
            }

            if (!scopeReads.TryGetValue(pair.ConnectionId, out var configuredScopes))
            {
                scopeReads[pair.ConnectionId] = configuredScopes = await scopes.GetByConnectionIdAsync(clientId, pair.ConnectionId, ct);
            }

            result.Add(AuthorizeSource(tenant, clientId, pair, target, connection, configuredScopes));
        }

        return result;
    }

    private AuthorizedSource AuthorizeSource(
        Guid tenant, Guid clientId, ClientPullRequestOverviewSource pair, CrawlConfigurationDto target,
        ClientScmConnectionDto connection, IReadOnlyList<ClientScmScopeDto> configuredScopes)
    {
        if (target.Id != pair.TargetId || target.ClientId != clientId || target.ReviewTargetLifecycle != ReviewTargetLifecycle.Enabled)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        if (target.RepoFilters.Count != 1 || target.RepoFilters[0].CanonicalSourceRef is not { } reference)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        if (connection.Id != pair.ConnectionId || connection.ClientId != clientId || !connection.IsActive)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        if (!string.Equals(connection.VerificationStatus, "verified", StringComparison.OrdinalIgnoreCase) || connection.ProviderFamily != target.Provider)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        IReviewSourcePolicy sourcePolicy;
        try
        {
            sourcePolicy = providers.GetReviewSourcePolicy(target.Provider);
        }
        catch (InvalidOperationException)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        if (!sourcePolicy.IsCacheScopeCompatible(connection.HostBaseUrl, target.ProviderScopePath))
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        if (!DestinationBranchPolicy.TryCreate(target.RepoFilters[0].TargetBranchPatterns, out var policy))
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        var grants = configuredScopes.Where(scope => scope.ClientId == clientId && scope.ConnectionId == connection.Id && scope.IsEnabled &&
                                                     sourcePolicy.MatchesGrant(target, scope))
            .OrderBy(scope => scope.Id).ToArray();
        if (grants.Length == 0)
        {
            throw new ClientPullRequestOverviewException("accessDenied");
        }

        var fingerprint = SourceFingerprint(tenant, clientId, target, connection, reference.Value, policy!, grants);
        return new(pair, target, connection, policy!, fingerprint, Hash(new { Tenant = tenant, clientId, pair.TargetId, pair.ConnectionId }));
    }

    private static string SourceFingerprint(
        Guid tenant, Guid clientId, CrawlConfigurationDto target, ClientScmConnectionDto connection,
        string reference, DestinationBranchPolicy policy, IReadOnlyList<ClientScmScopeDto> grants) =>
        Hash(
            new
            {
                Tenant = tenant, clientId, target.Id, target.Provider, target.ProviderScopePath, target.ProviderProjectKey,
                target.ReviewTargetRevision, target.ReviewTargetLifecycle, Reference = reference, Patterns = policy.Patterns,
                ConnectionId = connection.Id, connection.HostBaseUrl, connection.AuthenticationKind, connection.UpdatedAt,
                connection.VerificationStatus, connection.IsActive,
                Scopes = grants.Select(scope => new { scope.Id, scope.UpdatedAt, scope.ScopeType, scope.ScopePath, scope.ExternalScopeId })
            });

    private async Task RecheckAsync(
        Guid clientId, IReadOnlyList<ClientPullRequestOverviewSource> pairs, IReadOnlyList<AuthorizedSource> before, CancellationToken ct)
    {
        var after = await AuthorizeAsync(clientId, pairs, ct);
        if (!before.Select(source => source.Fingerprint).SequenceEqual(after.Select(source => source.Fingerprint)))
        {
            throw new ClientPullRequestOverviewException("obsolete");
        }
    }

    private async Task RecheckInvalidationAsync(Guid clientId, string? before, CancellationToken ct)
    {
        if (before != await store.ReadInvalidationAsync(clientId, ct))
        {
            throw new SnapshotInvalidatedException();
        }
    }

    private async Task FinalCheckAsync(
        Guid clientId, IReadOnlyList<AuthorizedSource> authority, string? invalidation, DateTimeOffset expiresAt, CancellationToken ct)
    {
        await RecheckAsync(clientId, authority.Select(source => source.Pair).ToArray(), authority, ct);
        await RecheckInvalidationAsync(clientId, invalidation, ct);
        if (expiresAt <= clock.GetUtcNow())
        {
            throw new ClientPullRequestOverviewException("obsolete");
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<SourceResult> ReadSourceAsync(Guid clientId, AuthorizedSource source, CancellationToken ct)
    {
        var claim = await store.AcquireAsync(clientId, source.Key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
        if (claim.Owner.HasValue)
        {
            var work = RunSharedRefreshAsync(scopeFactory, clientId, source, claim.Owner.Value);
            await work.WaitAsync(ct);
            claim = await store.AcquireAsync(clientId, source.Key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
        }
        else if (claim.LeaseUntil > clock.GetUtcNow())
        {
            // A bounded poll also coalesces requests handled by another backend instance.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (claim.LeaseUntil > clock.GetUtcNow() && wait.Elapsed < TimeSpan.FromSeconds(16))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                claim = await store.AcquireAsync(clientId, source.Key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
                if (claim.Owner.HasValue)
                {
                    await RunSharedRefreshAsync(scopeFactory, clientId, source, claim.Owner.Value).WaitAsync(ct);
                    claim = await store.AcquireAsync(clientId, source.Key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
                }
            }
        }

        var payload = claim.Content is null ? null : JsonSerializer.Deserialize<SourcePayload>(claim.Content, Json);
        if (payload?.ListedAt <= clock.GetUtcNow().AddMinutes(-15))
        {
            payload = null;
        }

        var status = claim.Failure is null && payload is not null ? "loaded" : claim.LeaseUntil.HasValue ? "pending" : "failed";
        return new(
            payload?.Rows ?? [], new(
                source.Pair.TargetId, source.Pair.ConnectionId, status, claim.Failure == "authenticationDenied" ? "accessDenied" : claim.Failure,
                payload?.ListedAt, claim.NextRefreshAt));
    }

    private static async Task RunSharedRefreshAsync(IServiceScopeFactory factory, Guid clientId, AuthorizedSource source, Guid owner)
    {
        await using var scope = factory.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>();
        await worker.RefreshAsync(clientId, source, owner);
    }

    private async Task RefreshAsync(Guid clientId, AuthorizedSource source, Guid owner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        string? content = null;
        string? failure = null;
        try
        {
            await RecheckAsync(clientId, [source.Pair], [source], timeout.Token);
            var reference = source.Target.RepoFilters[0].CanonicalSourceRef!;
            var repository = providers.GetReviewSourcePolicy(source.Target.Provider).CreateRepository(
                source.Connection.HostBaseUrl, reference.Value, source.Target.ProviderProjectKey, source.Target.RepoFilters[0].RepositoryName);
            var context = new ReviewDiscoveryContext(source.Pair.ConnectionId, source.Target.ProviderScopePath);
            var discovered = await AwaitProviderAsync(
                providers.GetReviewDiscoveryProvider(source.Target.Provider)
                    .ListOpenReviewsAsync(clientId, repository, null, timeout.Token, context), timeout.Token);
            if (discovered.Any(review => review.CodeReview.Number <= 0 || string.IsNullOrWhiteSpace(review.Title)))
            {
                throw new JsonException("The provider returned invalid pull request discovery data.");
            }

            var listedAt = clock.GetUtcNow();
            var rows = new List<ClientPullRequestOverviewRow>();
            foreach (var review in discovered.Where(review => review.Provider == source.Target.Provider &&
                                                              review.ReviewState is CodeReviewState.Open or CodeReviewState.Draft &&
                                                              review.Repository.ExternalRepositoryId.Equals(
                                                                  reference.Value, StringComparison.OrdinalIgnoreCase) &&
                                                              source.Policy.Matches(review.TargetBranch))
                         .OrderByDescending(review => review.CodeReview.Number).Take(100))
            {
                rows.Add(
                    new(
                        source.Pair.TargetId, source.Pair.ConnectionId, review.CodeReview.Number, Bound(review.Title, 512) ?? "",
                        SafeWebUrl(review.WebUrl), review.ReviewState == CodeReviewState.Draft ? "draft" : "open", Bound(review.SourceBranch, 512),
                        Bound(review.TargetBranch, 512), Bound(review.AuthorName?.Contains('@') == true ? null : review.AuthorName, 256),
                        Bound(review.ReviewRevision?.HeadSha, 128), Bound(review.ReviewRevision?.ProviderRevisionId, 128), null,
                        "unavailable", listedAt, null));
            }

            // Publication checks use their own bounded token after the provider deadline has elapsed.
            using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RecheckAsync(clientId, [source.Pair], [source], publication.Token);
            content = JsonSerializer.Serialize(new SourcePayload(rows, listedAt), Json);
        }
        catch (ClientPullRequestOverviewException)
        {
            failure = "configurationChanged";
        }
        catch (OperationCanceledException)
        {
            failure = "timeout";
        }
        catch (Exception exception) when (ProviderReadFailures.DeniedStatus(exception).HasValue)
        {
            failure = ProviderReadFailures.IsConnectionDenied(exception) ? "authenticationDenied" : "accessDenied";
        }
        catch (Exception exception) when (ProviderThrottleSignal.IsThrottled(exception))
        {
            failure = "throttled";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ArgumentException or JsonException)
        {
            failure = "unavailable";
        }

        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await store.CompleteAsync(clientId, source.Key, owner, source.Fingerprint, content, failure, clock.GetUtcNow(), completion.Token);
    }

    private async Task<ClientPullRequestOverviewPage> ReadPageAsync(
        Guid clientId, Snapshot snapshot, string cursor, Guid generation, string binding,
        int page, int pageSize, IReadOnlyList<AuthorizedSource> authority, string? invalidation, CancellationToken ct)
    {
        var current = await store.ReadGenerationStateAsync(clientId, generation, binding, clock.GetUtcNow(), ct)
                      ?? throw new ClientPullRequestOverviewException("obsolete");
        snapshot = snapshot with { ExpiresAt = current.ExpiresAt < snapshot.ExpiresAt ? current.ExpiresAt : snapshot.ExpiresAt };
        var response = Page(snapshot, cursor, page, pageSize, clock.GetUtcNow());
        var key = $"page:{generation:N}:{pageSize}:{page}";
        var saved = await store.ReadPageAsync(clientId, key, binding, clock.GetUtcNow(), ct);
        if (saved is not null)
        {
            response = JsonSerializer.Deserialize<ClientPullRequestOverviewPage>(saved, Json) ?? throw new ClientPullRequestOverviewException("obsolete");
        }
        else
        {
            response = await EnrichPageAsync(clientId, response, snapshot.ExpiresAt, authority, ct);
            if (!await store.SavePageAsync(clientId, key, binding, JsonSerializer.Serialize(response, Json), response.ExpiresAt, clock.GetUtcNow(), ct))
            {
                // Another waiter may have recorded this immutable page first.
                saved = await store.ReadPageAsync(clientId, key, binding, clock.GetUtcNow(), ct);
                response = saved is null
                    ? Capacity(response)
                    : JsonSerializer.Deserialize<ClientPullRequestOverviewPage>(saved, Json) ?? throw new ClientPullRequestOverviewException("obsolete");
            }
        }

        return await FinalizePageAsync(clientId, snapshot, response, generation, binding, authority, invalidation, ct);
    }

    private async Task<ClientPullRequestOverviewPage> EnrichPageAsync(
        Guid clientId, ClientPullRequestOverviewPage response, DateTimeOffset snapshotExpiresAt,
        IReadOnlyList<AuthorizedSource> authority, CancellationToken ct)
    {
        var map = authority.ToDictionary(source => source.Pair);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var workers = new SemaphoreSlim(4);
        var observed = await Task.WhenAll(
            response.Items.Select(async row =>
            {
                var acquired = false;
                try
                {
                    await workers.WaitAsync(deadline.Token);
                    acquired = true;
                    return await ReadMetadataAsync(clientId, map[new(row.TargetId, row.ConnectionId)], row, deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return row with { MetadataStatus = "unavailable" };
                }
                finally
                {
                    if (acquired)
                    {
                        workers.Release();
                    }
                }
            }));
        return response with
        {
            Items = observed,
            ExpiresAt = observed.Where(row => row.MetadataAt.HasValue).Select(row => row.MetadataAt!.Value.AddMinutes(15))
                .Append(snapshotExpiresAt).Min(),
            Status = response.Status == "available" && observed.Any(row => row.MetadataStatus != "available") ? "incomplete" : response.Status
        };
    }

    private async Task<ClientPullRequestOverviewPage> FinalizePageAsync(
        Guid clientId, Snapshot snapshot, ClientPullRequestOverviewPage response, Guid generation, string binding,
        IReadOnlyList<AuthorizedSource> authority, string? invalidation, CancellationToken ct)
    {
        await FinalCheckAsync(clientId, authority, invalidation, response.ExpiresAt, ct);
        var current = await store.ReadGenerationStateAsync(clientId, generation, binding, clock.GetUtcNow(), ct)
                      ?? throw new ClientPullRequestOverviewException("obsolete");
        response = response with { ExpiresAt = current.ExpiresAt < response.ExpiresAt ? current.ExpiresAt : response.ExpiresAt };
        await FinalCheckAsync(clientId, authority, invalidation, response.ExpiresAt, ct);
        var now = clock.GetUtcNow();
        if (snapshot.ExpiresAt <= now || response.ExpiresAt <= now)
        {
            throw new ClientPullRequestOverviewException("obsolete");
        }

        return BoundedResponse(
            response with
            {
                IsStale = response.Sources.Any(source => source.ListedAt <= now.AddSeconds(-60)) ||
                          response.Items.Any(row => row.ListedAt <= now.AddSeconds(-60) || row.MetadataAt <= now.AddSeconds(-60))
            });
    }

    private async Task<ClientPullRequestOverviewRow> ReadMetadataAsync(
        Guid clientId, AuthorizedSource source, ClientPullRequestOverviewRow row, CancellationToken ct)
    {
        var key = "m:" + Hash(new { Source = source.Key, row.Number, row.HeadSha, row.ProviderRevisionId });
        var claim = await store.AcquireAsync(clientId, key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
        if (claim.Owner.HasValue)
        {
            await RunSharedMetadataAsync(scopeFactory, clientId, source, row, key, claim.Owner.Value).WaitAsync(ct);
            claim = await store.AcquireAsync(clientId, key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
        }
        else
        {
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (claim.LeaseUntil > clock.GetUtcNow() && wait.Elapsed < TimeSpan.FromSeconds(16))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                claim = await store.AcquireAsync(clientId, key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
                if (claim.Owner.HasValue)
                {
                    await RunSharedMetadataAsync(scopeFactory, clientId, source, row, key, claim.Owner.Value).WaitAsync(ct);
                    claim = await store.AcquireAsync(clientId, key, source.Fingerprint, clock.GetUtcNow(), ct, source.CacheScope);
                }
            }
        }

        var payload = claim.Content is null ? null : JsonSerializer.Deserialize<MetadataPayload>(claim.Content, Json);
        if (payload?.ObservedAt <= clock.GetUtcNow().AddMinutes(-15))
        {
            payload = null;
        }

        return row with
        {
            Metadata = payload?.Value, MetadataAt = payload?.ObservedAt,
            MetadataStatus = claim.Failure is not null ? "unavailable" : payload is null ? "unavailable" : payload.Value.IsComplete ? "available" : "incomplete"
        };
    }

    private static async Task RunSharedMetadataAsync(
        IServiceScopeFactory factory, Guid clientId, AuthorizedSource source,
        ClientPullRequestOverviewRow row, string key, Guid owner)
    {
        await using var scope = factory.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ClientPullRequestOverviewService>().RefreshMetadataAsync(clientId, source, row, key, owner);
    }

    private async Task RefreshMetadataAsync(Guid clientId, AuthorizedSource source, ClientPullRequestOverviewRow row, string key, Guid owner)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), clock);
        string? content = null;
        string? failure = null;
        try
        {
            await RecheckAsync(clientId, [source.Pair], [source], deadline.Token);
            var filter = source.Target.RepoFilters[0];
            var repository = providers.GetReviewSourcePolicy(source.Target.Provider).CreateRepository(
                source.Connection.HostBaseUrl, filter.CanonicalSourceRef!.Value, source.Target.ProviderProjectKey, filter.RepositoryName);
            var review = new CodeReviewRef(
                repository, CodeReviewPlatformKind.PullRequest, row.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Number);
            var value = await AwaitProviderAsync(
                providers.GetReviewOverviewProvider(source.Target.Provider).GetOverviewAsync(
                    clientId, review,
                    new(source.Pair.ConnectionId, source.Target.ProviderScopePath), deadline.Token), deadline.Token);
            if (!ValidMetadata(value))
            {
                throw new JsonException("The provider returned inconsistent metadata.");
            }

            var observed = clock.GetUtcNow();
            using var publication = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RecheckAsync(clientId, [source.Pair], [source], publication.Token);
            content = JsonSerializer.Serialize(new MetadataPayload(value, observed), Json);
        }
        catch (ClientPullRequestOverviewException)
        {
            failure = "configurationChanged";
        }
        catch (OperationCanceledException)
        {
            failure = "timeout";
        }
        catch (Exception exception) when (ProviderReadFailures.DeniedStatus(exception).HasValue)
        {
            failure = ProviderReadFailures.IsConnectionDenied(exception) ? "authenticationDenied" : "accessDenied";
        }
        catch (Exception exception) when (ProviderThrottleSignal.IsThrottled(exception))
        {
            failure = "throttled";
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or ArgumentException or JsonException)
        {
            failure = "unavailable";
        }

        using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await store.CompleteAsync(clientId, key, owner, source.Fingerprint, content, failure, clock.GetUtcNow(), completion.Token);
    }

    private static async Task<T> AwaitProviderAsync<T>(Task<T> work, CancellationToken deadline)
    {
        // Observe failures from providers that continue after the owned wait has expired.
        _ = work.ContinueWith(
            completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var result = await work.WaitAsync(deadline);
        deadline.ThrowIfCancellationRequested();
        return result;
    }

    private static ClientPullRequestOverviewPage Page(Snapshot snapshot, string cursor, int page, int pageSize, DateTimeOffset now)
    {
        if (page > Math.Max(1, (snapshot.Items.Count + pageSize - 1) / pageSize))
        {
            throw new ClientPullRequestOverviewException("invalid");
        }

        var stale = snapshot.Sources.Any(source => source.ListedAt <= now.AddSeconds(-60)) ||
                    snapshot.Items.Any(row => row.ListedAt <= now.AddSeconds(-60) || row.MetadataAt <= now.AddSeconds(-60));
        var next = snapshot.Sources.Count == 0 ? (DateTimeOffset?)null : snapshot.Sources.Min(source => source.NextRefreshAt);
        return new(
            snapshot.Items.Skip((page - 1) * pageSize).Take(pageSize).ToArray(), snapshot.Sources, cursor,
            snapshot.AttemptedSources < Math.Min(snapshot.TotalSources, 100) && cursor.Length > 0 ? cursor : null,
            page, pageSize, snapshot.Items.Count, snapshot.TotalSources, snapshot.AttemptedSources, snapshot.CreatedAt,
            snapshot.ExpiresAt, stale, next, snapshot.Status);
    }

    private static ClientPullRequestOverviewPage Capacity(ClientPullRequestOverviewPage page) =>
        page with { Items = [], Cursor = "", NextCursor = null, TotalRows = null, Status = "capacity" };

    private static ClientPullRequestOverviewPage BoundedResponse(ClientPullRequestOverviewPage page) =>
        JsonSerializer.SerializeToUtf8Bytes(page, Json).Length <= 2 * 1024 * 1024 ? page : Capacity(page);

    private static bool ValidMetadata(ReviewOverviewDto metadata)
    {
        if (metadata.TotalComments is < 0 || metadata.ResolvedDiscussions is < 0 || metadata.UnresolvedDiscussions is < 0)
        {
            return false;
        }

        if (!metadata.ResolutionSupported && (metadata.ResolvedDiscussions is not null || metadata.UnresolvedDiscussions is not null))
        {
            return false;
        }

        if (!metadata.IsComplete)
        {
            return true;
        }

        return metadata.TotalComments.HasValue &&
               (!metadata.ResolutionSupported || metadata.ResolvedDiscussions.HasValue && metadata.UnresolvedDiscussions.HasValue);
    }

    private static string? Bound(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    private static string? SafeWebUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? Bound(text, 2048) : null;

    private static string Hash(object value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));

    private string RepositoryKey(AuthorizedSource source, int number)
    {
        var coordinates = providers.GetReviewSourcePolicy(source.Target.Provider)
            .GetCacheCoordinates(source.Target.ProviderScopePath, source.Target.ProviderProjectKey);
        return JsonSerializer.Serialize(
            new object[]
            {
                source.Target.Provider, coordinates.Authority,
                coordinates.ScopePath, coordinates.ProjectKey,
                source.Target.RepoFilters[0].CanonicalSourceRef!.Value, number
            });
    }

    private sealed record AuthorizedSource(
        ClientPullRequestOverviewSource Pair,
        CrawlConfigurationDto Target,
        ClientScmConnectionDto Connection,
        DestinationBranchPolicy Policy,
        string Fingerprint,
        string Key)
    {
        public OverviewCacheScope CacheScope => new(Pair.ConnectionId, Key);
    }

    private sealed class SnapshotInvalidatedException : Exception;

    private sealed record SourcePayload(IReadOnlyList<ClientPullRequestOverviewRow> Rows, DateTimeOffset ListedAt);

    private sealed record MetadataPayload(ReviewOverviewDto Value, DateTimeOffset ObservedAt);

    private sealed record SourceResult(IReadOnlyList<ClientPullRequestOverviewRow> Rows, ClientPullRequestOverviewOutcome Outcome);

    private sealed record Snapshot(
        IReadOnlyList<ClientPullRequestOverviewRow> Items,
        IReadOnlyList<ClientPullRequestOverviewRow> Observations,
        IReadOnlyList<ClientPullRequestOverviewOutcome> Sources,
        int TotalSources,
        int AttemptedSources,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt,
        string Status);

    private static Snapshot SnapshotFrom(OverviewGeneration generation)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(generation.Content, Json) ?? throw new ClientPullRequestOverviewException("obsolete");
        return snapshot with { ExpiresAt = generation.ExpiresAt < snapshot.ExpiresAt ? generation.ExpiresAt : snapshot.ExpiresAt };
    }
}
