// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.TenantData;

/// <summary>Stops, deletes, exports and retains the data of a tenant, and deletes single clients.</summary>
/// <remarks>
///     Most tables reach a tenant through a foreign key to <c>clients</c> or <c>tenants</c> that cascades. The deletion
///     removes the rows of the tables that carry the identifier without such a key before it deletes the clients and the
///     tenant. The ProCursor data is stored in the ProCursor database and is deleted through
///     <see cref="IProCursorTenantData" />.
/// </remarks>
public sealed class TenantDataLifecycle(
    MeisterProPRDbContext database,
    IReviewJobCancellationRegistry? cancellations = null,
    TimeProvider? clock = null,
    IProCursorTenantData? proCursor = null,
    IProCursorConfigurationNotifier? proCursorNotifier = null,
    ILogger<TenantDataLifecycle>? logger = null) : ITenantDataLifecycle
{
    /// <summary>The export format this class writes.</summary>
    public const string ExportFormat = "propr.tenant-export";

    public const int ExportVersion = 1;

    /// <summary>The longest instruction reference an erasure records.</summary>
    public const int MaximumInstructionLength = 120;

    /// <summary>
    ///     The time a deletion waits after a request stopped work of the tenant or the client. An intake that read an
    ///     active configuration before the stop has stored its job by then, and the next request stops it.
    /// </summary>
    internal static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(1);

    /// <summary>
    ///     How long a thread pass or a mention reply counts as running after it started. ProPR holds no lease on either,
    ///     and a thread pass counts as stalled after this time.
    /// </summary>
    internal static readonly TimeSpan UnleasedRunLimit = TimeSpan.FromMinutes(15);

    private const int ExportPageSize = 200;

    private const int ExportPageSizeWithProtocols = 25;

    private static readonly JobStatus[] UnfinishedReviewStatuses =
        [JobStatus.Pending, JobStatus.Processing, JobStatus.AdmissionHeld, JobStatus.BudgetHeld, JobStatus.BudgetExceeded];

    private static readonly ThreadPassJobStatus[] WaitingThreadPassStatuses =
        [ThreadPassJobStatus.Pending, ThreadPassJobStatus.BudgetHeld, ThreadPassJobStatus.BudgetExceeded];

    private static readonly MentionJobStatus[] WaitingMentionStatuses = [MentionJobStatus.Pending, MentionJobStatus.BudgetHeld];

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<TenantDeletionAnswer?> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (tenantId == SystemTenant.Id)
        {
            throw new TenantDataLifecycleException("The system tenant cannot be deleted.");
        }

        if (!await database.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var clientIds = await database.Clients.Where(client => client.TenantId == tenantId).Select(client => client.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var scope = await this.ScopeAsync(tenantId, clientIds, "The tenant is being deleted.", cancellationToken)
            .ConfigureAwait(false);
        var now = this._clock.GetUtcNow();
        var indexing = await this.StopAsync(scope, now, cancellationToken).ConfigureAwait(false);
        var quietStart = await database.Tenants.Where(tenant => tenant.Id == tenantId)
            .Select(tenant => (DateTimeOffset?)tenant.UpdatedAt).SingleAsync(cancellationToken).ConfigureAwait(false);
        if (await this.PendingAsync(quietStart, clientIds, indexing, now, cancellationToken).ConfigureAwait(false) is { } pending)
        {
            return new(null, pending);
        }

        var deleted = await this.DeleteProCursorDataAsync(scope, cancellationToken).ConfigureAwait(false);
        await this.DeleteTenantRowsAsync(tenantId, deleted, cancellationToken).ConfigureAwait(false);
        return new(new TenantDeletionResult(tenantId, deleted), null);
    }

    public async Task<ClientDeletionAnswer?> DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default)
    {
        if (!await database.Clients.AnyAsync(client => client.Id == clientId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        List<Guid> clientIds = [clientId];
        var scope = await this.ScopeAsync(null, clientIds, "The client is being deleted.", cancellationToken)
            .ConfigureAwait(false);
        var now = this._clock.GetUtcNow();
        var indexing = await this.StopAsync(scope, now, cancellationToken).ConfigureAwait(false);
        var quietStart = await database.Clients.Where(client => client.Id == clientId).Select(client => client.WorkStoppedAt)
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (await this.PendingAsync(quietStart, clientIds, indexing, now, cancellationToken).ConfigureAwait(false) is { } pending)
        {
            return new(null, pending);
        }

        var deleted = await this.DeleteProCursorDataAsync(scope, cancellationToken).ConfigureAwait(false);
        await this.DeleteClientRowsAsync(clientId, deleted, cancellationToken).ConfigureAwait(false);
        return new(new ClientDeletionResult(clientId, deleted), null);
    }

    public async Task<bool> WriteExportAsync(
        Guid tenantId, Stream output, TenantExportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);

        // One snapshot for the whole document, so its parts agree with each other.
        await using var transaction = await database.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var tenant = await database.Tenants.AsNoTracking().Where(row => row.Id == tenantId)
            .Select(row => new ExportedTenant(row.Id, row.Slug, row.DisplayName, row.IsActive, row.CreatedAt, row.ProtocolRetentionDays))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return false;
        }

        await using var writer = new Utf8JsonWriter(output);
        writer.WriteStartObject();
        writer.WriteString("format", ExportFormat);
        writer.WriteNumber("version", ExportVersion);
        writer.WriteString("exportedAt", this._clock.GetUtcNow());
        writer.WritePropertyName("tenant");
        JsonSerializer.Serialize(writer, tenant, ExportJson);
        writer.WriteStartArray("clients");
        var clients = await database.Clients.AsNoTracking().Where(client => client.TenantId == tenantId)
            .OrderBy(client => client.CreatedAt).ThenBy(client => client.Id).Select(client => client.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var client in clients)
        {
            await this.WriteClientAsync(writer, client, options, cancellationToken).ConfigureAwait(false);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SetProtocolRetentionAsync(
        Guid tenantId, int? days,
        CancellationToken cancellationToken = default)
    {
        if (days is < 1 or > 3650)
        {
            throw new TenantDataLifecycleException("The retention must be from 1 to 3650 days, or none.");
        }

        return await database.Tenants.Where(row => row.Id == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ProtocolRetentionDays, days), cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<int> PurgeExpiredProtocolEventsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var tenants = await database.Tenants.AsNoTracking().Where(tenant => tenant.ProtocolRetentionDays != null)
            .Select(tenant => new { tenant.Id, Days = tenant.ProtocolRetentionDays!.Value })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var purged = 0;
        foreach (var tenant in tenants)
        {
            var cutoff = now.AddDays(-tenant.Days);
            var clients = database.Clients.Where(client => client.TenantId == tenant.Id).Select(client => client.Id);

            // A protocol belongs to a review job, a thread pass or a mention reply, and each of them to a client.
            var protocols = database.ReviewJobProtocols
                .Where(protocol =>
                    database.ReviewJobs.Any(job => job.Id == protocol.JobId && clients.Contains(job.ClientId))
                    || database.ThreadPassJobs.Any(job => job.Id == protocol.ThreadPassJobId && clients.Contains(job.ClientId))
                    || database.MentionReplyJobs.Any(job => job.Id == protocol.MentionReplyJobId && clients.Contains(job.ClientId)))
                .Select(protocol => protocol.Id);
            purged += await database.ProtocolEvents
                .Where(protocolEvent => protocolEvent.OccurredAt < cutoff && protocols.Contains(protocolEvent.ProtocolId))
                .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        return purged;
    }

    public async Task<IdentityErasureResult?> EraseAuthorAsync(
        Guid tenantId, AuthorIdentity author, Guid? actorUserId,
        string instruction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(author);
        ArgumentException.ThrowIfNullOrWhiteSpace(author.ExternalUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        if (instruction.Trim().Length > MaximumInstructionLength)
        {
            throw new TenantDataLifecycleException($"The instruction reference can have at most {MaximumInstructionLength} characters.");
        }

        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await database.Tenants.AnyAsync(tenant => tenant.Id == tenantId, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var external = author.ExternalUserId.Trim();
        var login = author.Login?.Trim();
        var clients = database.Clients.Where(client => client.TenantId == tenantId).Select(client => client.Id);
        var reviewJobs = database.ReviewJobs.Where(job => clients.Contains(job.ClientId)
                                                          && (job.PrAuthorExternalUserId == external
                                                              || (login != null && job.PrAuthorLogin == login)));
        var mentionReplies = database.MentionReplyJobs.Where(job => clients.Contains(job.ClientId)
                                                                    && (job.CommentAuthorExternalUserId == external
                                                                        || (login != null && job.CommentAuthorLogin == login)));

        // A thread pass evaluates the threads of a pull request, and its protocol can quote their comments. It concerns
        // the author when it ran on a pull request of the author, or when it evaluated a thread with a comment of the
        // author.
        var authorComments = database.RetainedThreadComments.Where(comment =>
            comment.AuthorIdentity == external || (login != null && comment.AuthorIdentity == login));
        var threadPasses = database.ThreadPassJobs.Where(pass =>
            clients.Contains(pass.ClientId)
            && (reviewJobs.Any(job => job.ClientId == pass.ClientId && job.RepositoryId == pass.RepositoryId
                                                                    && job.PullRequestId == pass.PullRequestId)
                || database.ThreadPassHandledThreads.Any(handled =>
                    handled.ThreadPassJobId == pass.Id
                    && database.RetainedThreads.Any(thread =>
                        thread.ThreadId == handled.ThreadId
                        && authorComments.Any(comment => comment.RetainedThreadId == thread.Id)
                        && database.RetainedPullRequests.Any(pullRequest =>
                            pullRequest.Id == thread.RetainedPullRequestId && pullRequest.ClientId == handled.ClientId
                                                                           && pullRequest.RepositoryId == handled.RepositoryId
                                                                           && pullRequest.PullRequestId == handled.PullRequestId)))));

        // Protocols can quote the comments and the descriptions of the author, so the events of every execution that
        // concerns the author are deleted before the author fields that identify those executions are cleared.
        var events = await database.ProtocolEvents
            .Where(protocolEvent => database.ReviewJobProtocols.Any(protocol =>
                protocol.Id == protocolEvent.ProtocolId
                && (reviewJobs.Any(job => job.Id == protocol.JobId)
                    || mentionReplies.Any(job => job.Id == protocol.MentionReplyJobId)
                    || threadPasses.Any(pass => pass.Id == protocol.ThreadPassJobId))))
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        var jobs = await reviewJobs.ExecuteUpdateAsync(
            set => set
                .SetProperty(job => job.PrAuthorExternalUserId, (string?)null)
                .SetProperty(job => job.PrAuthorLogin, (string?)null)
                .SetProperty(job => job.PrAuthorDisplayName, (string?)null),
            cancellationToken).ConfigureAwait(false);
        var mentions = await mentionReplies.ExecuteUpdateAsync(
            set => set
                .SetProperty(job => job.CommentAuthorExternalUserId, (string?)null)
                .SetProperty(job => job.CommentAuthorLogin, (string?)null)
                .SetProperty(job => job.CommentAuthorDisplayName, (string?)null)
                .SetProperty(job => job.CommentAuthorNativeId, (string?)null),
            cancellationToken).ConfigureAwait(false);
        var comments = await database.RetainedThreadComments
            .Where(comment => (comment.AuthorIdentity == external || (login != null && comment.AuthorIdentity == login))
                              && database.RetainedThreads.Any(thread =>
                                  thread.Id == comment.RetainedThreadId
                                  && database.RetainedPullRequests.Any(pullRequest =>
                                      pullRequest.Id == thread.RetainedPullRequestId && clients.Contains(pullRequest.ClientId))))
            .ExecuteUpdateAsync(set => set.SetProperty(comment => comment.AuthorIdentity, "erased"), cancellationToken)
            .ConfigureAwait(false);

        // The audit names the erasure without the identity it erased.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(external)))[..16];
        database.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorUserId = actorUserId,
                EventType = "author.erased",
                Summary = $"Erased the author {hash} on instruction {instruction.Trim()}.",
                Detail = $"Review jobs: {jobs}. Mention replies: {mentions}. Retained comments: {comments}. Protocol events: {events}.",
                OccurredAt = this._clock.GetUtcNow(),
            });
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(jobs, mentions, comments, events);
    }

    /// <summary>Returns the scope of a deletion with the knowledge sources of its clients.</summary>
    private async Task<DeletionScope> ScopeAsync(Guid? tenantId, List<Guid> clientIds, string stopReason, CancellationToken ct)
    {
        var sourceIds = await database.ProCursorKnowledgeSources.Where(source => clientIds.Contains(source.ClientId))
            .Select(source => source.Id).ToListAsync(ct).ConfigureAwait(false);
        return new DeletionScope(tenantId, clientIds, sourceIds, stopReason);
    }

    /// <summary>Stops the work of the scope in ProPR and in ProCursor.</summary>
    /// <returns>The index jobs of the scope after their indexing was stopped, or null without ProCursor.</returns>
    private async Task<ProCursorIndexingStop?> StopAsync(DeletionScope scope, DateTimeOffset now, CancellationToken ct)
    {
        await this.StopWorkAsync(scope, now, ct).ConfigureAwait(false);

        // The knowledge sources are disabled by now. Until the ProCursor service reads that, it can still queue an
        // index job for them, and a later request cancels that job and starts the quiet period again.
        await this.NotifyProCursorAsync(scope.SourceIds, ct).ConfigureAwait(false);
        var indexing = proCursor is null
            ? null
            : await proCursor.StopIndexingAsync(scope.SourceIds, ct).ConfigureAwait(false);
        if (indexing?.Cancelled > 0)
        {
            await this.StartQuietPeriodAsync(scope, now, ct).ConfigureAwait(false);
        }

        return indexing;
    }

    /// <summary>Tells the ProCursor service that the knowledge sources changed, so it reads them again.</summary>
    /// <remarks>
    ///     Without the notice, the ProCursor service reads the change when its cached configuration expires. A failed
    ///     notice therefore delays the stop of the indexing and does not fail the deletion request.
    /// </remarks>
    private async Task NotifyProCursorAsync(List<Guid> sourceIds, CancellationToken ct)
    {
        if (proCursorNotifier is null)
        {
            return;
        }

        foreach (var sourceId in sourceIds)
        {
            try
            {
                await proCursorNotifier.SourceChangedAsync(sourceId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogWarning(ex, "The ProCursor service was not told that knowledge source {SourceId} is disabled.", sourceId);
            }
        }
    }

    /// <summary>
    ///     Deactivates the clients of the scope and their configurations, and the tenant when the whole tenant is
    ///     deleted. Closes the discussion synchronizations of the clients and ends their unfinished jobs, in one
    ///     transaction. When it changes anything, it starts the quiet period in the same transaction.
    /// </summary>
    private async Task StopWorkAsync(DeletionScope scope, DateTimeOffset now, CancellationToken ct)
    {
        var (tenantId, clientIds, sourceIds, stopReason) = scope;
        await using var transaction = await database.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var changed = 0;
        if (tenantId is { } tenant)
        {
            changed += await database.Tenants.Where(row => row.Id == tenant && row.IsActive)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.IsActive, false), ct).ConfigureAwait(false);
        }

        changed += await database.Clients.Where(client => clientIds.Contains(client.Id) && client.IsActive)
            .ExecuteUpdateAsync(set => set.SetProperty(client => client.IsActive, false), ct).ConfigureAwait(false);

        // Webhook, crawl and mention intake each read the flag of their configuration.
        changed += await database.WebhookConfigurations.Where(config => clientIds.Contains(config.ClientId) && config.IsActive)
            .ExecuteUpdateAsync(set => set.SetProperty(config => config.IsActive, false), ct).ConfigureAwait(false);
        changed += await database.CrawlConfigurations.Where(config => clientIds.Contains(config.ClientId) && config.IsActive)
            .ExecuteUpdateAsync(set => set.SetProperty(config => config.IsActive, false), ct).ConfigureAwait(false);
        changed += await database.MentionConfigurations.Where(config => clientIds.Contains(config.ClientId) && config.IsActive)
            .ExecuteUpdateAsync(set => set.SetProperty(config => config.IsActive, false), ct).ConfigureAwait(false);
        changed += await database.ProCursorKnowledgeSources.Where(source => sourceIds.Contains(source.Id) && source.IsEnabled)
            .ExecuteUpdateAsync(set => set.SetProperty(source => source.IsEnabled, false), ct).ConfigureAwait(false);
        changed += await database.ProCursorTrackedBranches
            .Where(branch => sourceIds.Contains(branch.KnowledgeSourceId) && branch.IsEnabled)
            .ExecuteUpdateAsync(set => set.SetProperty(branch => branch.IsEnabled, false), ct).ConfigureAwait(false);
        changed += await database.DiscussionSynchronizationStates.Where(state => clientIds.Contains(state.ClientId) && !state.IsClosed)
            .ExecuteUpdateAsync(set => set.SetProperty(state => state.IsClosed, true), ct).ConfigureAwait(false);

        // A running thread pass or mention reply finishes its attempt, and the deletion waits for it. The waiting ones
        // are cancelled; a mention reply has no cancelled status and fails.
        changed += await database.ThreadPassJobs
            .Where(job => clientIds.Contains(job.ClientId) && WaitingThreadPassStatuses.Contains(job.Status))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.Status, ThreadPassJobStatus.Cancelled)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.ErrorMessage, stopReason),
                ct).ConfigureAwait(false);
        changed += await database.MentionReplyJobs
            .Where(job => clientIds.Contains(job.ClientId) && WaitingMentionStatuses.Contains(job.Status))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.Status, MentionJobStatus.Failed)
                    .SetProperty(job => job.CompletedAt, now)
                    .SetProperty(job => job.ErrorMessage, stopReason),
                ct).ConfigureAwait(false);

        // Reviews end as a manual stop ends them: the status is persisted, so the pipeline aborts at its next status
        // check on any instance, and the open protocols are closed. A running review also loses its lease renewal.
        var reviews = await database.ReviewJobs
            .Where(job => clientIds.Contains(job.ClientId) && UnfinishedReviewStatuses.Contains(job.Status))
            .Select(job => job.Id).ToListAsync(ct).ConfigureAwait(false);
        if (reviews.Count > 0)
        {
            changed += await database.ReviewJobs
                .Where(job => reviews.Contains(job.Id) && UnfinishedReviewStatuses.Contains(job.Status))
                .ExecuteUpdateAsync(
                    set => set
                        .SetProperty(job => job.Status, JobStatus.Stopped)
                        .SetProperty(job => job.CompletedAt, now)
                        .SetProperty(job => job.ErrorMessage, stopReason),
                    ct).ConfigureAwait(false);
            await database.ReviewJobProtocols
                .Where(protocol => protocol.JobId != null && reviews.Contains(protocol.JobId.Value) && protocol.CompletedAt == null)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(protocol => protocol.CompletedAt, now).SetProperty(protocol => protocol.Outcome, "Abandoned"),
                    ct).ConfigureAwait(false);
        }

        if (changed > 0)
        {
            await this.StartQuietPeriodAsync(scope, now, ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        foreach (var review in reviews)
        {
            cancellations?.Cancel(review);
        }
    }

    /// <summary>
    ///     Starts the quiet period of the deletion. A tenant deletion counts it from the update time of the tenant, and a
    ///     client deletion from the time at which the work of the client was stopped.
    /// </summary>
    private async Task StartQuietPeriodAsync(DeletionScope scope, DateTimeOffset now, CancellationToken ct)
    {
        if (scope.TenantId is { } tenantId)
        {
            await database.Tenants.Where(tenant => tenant.Id == tenantId)
                .ExecuteUpdateAsync(set => set.SetProperty(tenant => tenant.UpdatedAt, now), ct).ConfigureAwait(false);
        }

        var clientIds = scope.ClientIds;
        await database.Clients.Where(client => clientIds.Contains(client.Id))
            .ExecuteUpdateAsync(set => set.SetProperty(client => client.WorkStoppedAt, now), ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the ProCursor data of the scope and returns the deleted rows, by table.</summary>
    /// <remarks>
    ///     The ProCursor data is deleted first, while the knowledge sources that identify it still exist. A failure after
    ///     that leaves the tenant or the client in place, and the next request repeats the deletion.
    /// </remarks>
    private async Task<Dictionary<string, int>> DeleteProCursorDataAsync(DeletionScope scope, CancellationToken ct) =>
        proCursor is null
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : new Dictionary<string, int>(
                await proCursor.DeleteAsync(scope.SourceIds, scope.ClientIds, ct).ConfigureAwait(false),
                StringComparer.Ordinal);

    /// <summary>
    ///     The work the deletion still waits for: the quiet period after the last stop, and every execution that may still
    ///     be running.
    /// </summary>
    /// <returns>The pending answer, or null when the deletion can go ahead.</returns>
    /// <param name="quietStart">The start of the quiet period, or null when no request stopped any work.</param>
    private async Task<DeletionPending?> PendingAsync(
        DateTimeOffset? quietStart, List<Guid> clientIds, ProCursorIndexingStop? indexing,
        DateTimeOffset now, CancellationToken ct)
    {
        var until = quietStart is { } start ? start + QuietPeriod : DateTimeOffset.MinValue;
        var running = new Dictionary<string, int>(StringComparer.Ordinal);

        void Wait(string kind, IEnumerable<DateTimeOffset> ends)
        {
            var later = ends.Where(end => end > now).ToList();
            if (later.Count == 0)
            {
                return;
            }

            running[kind] = later.Count;
            until = later.Append(until).Max();
        }

        // A stopped review keeps its lease until the lease expires, and the lease is no longer renewed.
        Wait(
            "reviews",
            await database.ReviewJobs.Where(job => clientIds.Contains(job.ClientId) && job.LeaseExpiresAt > now)
                .Select(job => job.LeaseExpiresAt!.Value).ToListAsync(ct).ConfigureAwait(false));
        var unleasedSince = now - UnleasedRunLimit;
        Wait(
            "threadPasses",
            (await database.ThreadPassJobs
                .Where(job => clientIds.Contains(job.ClientId) && job.Status == ThreadPassJobStatus.Processing
                                                               && job.ProcessingStartedAt > unleasedSince)
                .Select(job => job.ProcessingStartedAt!.Value).ToListAsync(ct).ConfigureAwait(false))
            .Select(started => started + UnleasedRunLimit));
        Wait(
            "mentionReplies",
            (await database.MentionReplyJobs
                .Where(job => clientIds.Contains(job.ClientId) && job.Status == MentionJobStatus.Processing
                                                               && job.ProcessingStartedAt > unleasedSince)
                .Select(job => job.ProcessingStartedAt!.Value).ToListAsync(ct).ConfigureAwait(false))
            .Select(started => started + UnleasedRunLimit));
        Wait(
            "discussionSyncs",
            await database.DiscussionSynchronizationStates
                .Where(state => clientIds.Contains(state.ClientId) && state.LeaseExpiresAt > now)
                .Select(state => state.LeaseExpiresAt!.Value).ToListAsync(ct).ConfigureAwait(false));
        Wait("indexJobs", indexing?.RunningUntil ?? []);
        return until > now ? new DeletionPending(until - now, running) : null;
    }

    /// <summary>Deletes the tenant, its clients and every row that carries one of their identifiers, in one transaction.</summary>
    private async Task DeleteTenantRowsAsync(Guid tenantId, Dictionary<string, int> deleted, CancellationToken ct)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = new RowDeletion(database, deleted, ct);
        await this.DeleteClientDataAsync(rows, database.Clients.Where(client => client.TenantId == tenantId).Select(client => client.Id));

        // The tables that carry the tenant without a cascading foreign key to tenants.
        await rows.RemoveAsync(database.LogicalModels.Where(row => row.TenantId == tenantId));
        await rows.RemoveAsync(database.AiModelCatalogEntries.Where(row => row.TenantId == tenantId));
        await rows.RemoveAsync(database.AiUsageReports.Where(row => row.TenantId == tenantId));
        await rows.RemoveAsync(database.ReviewRunners.Where(row => row.TenantId == tenantId));
        await rows.RemoveAsync(database.RunnerRegistrationTokens.Where(row => row.TenantId == tenantId));

        // The deletion also removes the accounts that belong to this tenant alone. It keeps administrators and accounts
        // with access to another tenant or to a client of another tenant.
        await rows.RemoveAsync(
            database.AppUsers.Where(user =>
                user.GlobalRole != AppUserRole.Admin
                && (database.TenantMemberships.Any(membership => membership.UserId == user.Id && membership.TenantId == tenantId)
                    || database.ExternalIdentities.Any(identity => identity.UserId == user.Id && identity.TenantId == tenantId))
                && !database.TenantMemberships.Any(membership => membership.UserId == user.Id && membership.TenantId != tenantId)
                && !database.UserClientRoles.Any(role => role.UserId == user.Id
                                                         && database.Clients.Any(client => client.Id == role.ClientId
                                                                                           && client.TenantId != tenantId))));
        await rows.RemoveAsync(database.Clients.Where(row => row.TenantId == tenantId));
        await rows.RemoveAsync(database.Tenants.Where(row => row.Id == tenantId));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the client and every row that carries its identifier, in one transaction.</summary>
    private async Task DeleteClientRowsAsync(Guid clientId, Dictionary<string, int> deleted, CancellationToken ct)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var rows = new RowDeletion(database, deleted, ct);
        await this.DeleteClientDataAsync(rows, database.Clients.Where(client => client.Id == clientId).Select(client => client.Id));
        await rows.RemoveAsync(database.Clients.Where(row => row.Id == clientId));
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Deletes the rows of the clients from the tables that carry a client without a cascading foreign key to
    ///     <c>clients</c>, and the rows that reference those rows without a foreign key. The rows of the clients
    ///     themselves stay, and the rows that cascade from them are deleted with them.
    /// </summary>
    private async Task DeleteClientDataAsync(RowDeletion rows, IQueryable<Guid> clients)
    {
        var reviewJobs = database.ReviewJobs.Where(job => clients.Contains(job.ClientId)).Select(job => job.Id);
        var syncStates = database.DiscussionSynchronizationStates.Where(state => clients.Contains(state.ClientId))
            .Select(state => state.Id);
        var reports = database.ReviewerPerformanceReportClients.Where(report => clients.Contains(report.ClientId))
            .Select(report => report.ReportId);

        // Rows that reference rows of the tables below without a foreign key are deleted first.
        await rows.RemoveAsync(database.RunnerIngestReceipts.Where(receipt => reviewJobs.Contains(receipt.JobId)));
        await rows.RemoveAsync(database.DiscussionObservationFences.Where(fence => syncStates.Contains(fence.OwnerStateId)));

        // A cached report can summarize the clients.
        await rows.RemoveAsync(database.ReviewerPerformanceReports.Where(report => reports.Contains(report.Id)));

        // The tables that carry a client without a cascading foreign key to clients, in deletion order: a table that a
        // later table references with a restricting key comes after it.
        await rows.RemoveAsync(database.LogicalModelOverrides.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.AiModelCatalogEntries.Where(row => row.ClientId != null && clients.Contains(row.ClientId.Value)));
        await rows.RemoveAsync(database.AiUsageReports.Where(row => row.ClientId != null && clients.Contains(row.ClientId.Value)));
        await rows.RemoveAsync(database.BudgetEvents.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.BudgetSpendResets.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.ClientPullRequestOverviewCache.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.CompletedReviewUsageSnapshots.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.DiscussionSynchronizationStates.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.MemoryActivityLogEntries.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.PostedCommentOrigins.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.RetainedPullRequests.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.ReviewerPerformanceDailyCounts.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.ReviewJobs.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.MentionReplyJobs.Where(row => clients.Contains(row.ClientId)));
        await rows.RemoveAsync(database.UserClientRoles.Where(row => clients.Contains(row.ClientId)));
    }

    private async Task WriteClientAsync(Utf8JsonWriter writer, Guid clientId, TenantExportOptions options, CancellationToken ct)
    {
        // Connections are exported without their secret material.
        var client = await database.Clients.AsNoTracking().Where(row => row.Id == clientId)
            .Select(row => new ExportedClient(
                row.Id, row.DisplayName, row.IsActive, row.CreatedAt, row.CustomSystemMessage, row.OutputLanguage,
                database.ClientScmConnections.Where(connection => connection.ClientId == row.Id)
                    .OrderBy(connection => connection.CreatedAt)
                    .Select(connection => new ExportedConnection(
                        connection.Id, connection.Provider, connection.HostBaseUrl, connection.DisplayName,
                        connection.IsActive, connection.CreatedAt))
                    .ToList(),
                database.ClientScmScopes.Where(scope => scope.ClientId == row.Id).OrderBy(scope => scope.CreatedAt)
                    .Select(scope => new ExportedScope(scope.Id, scope.ConnectionId, scope.ScopeType, scope.ScopePath, scope.DisplayName, scope.IsEnabled))
                    .ToList(),
                database.ThreadMemoryRecords.Where(memory => memory.ClientId == row.Id).OrderBy(memory => memory.CreatedAt)
                    .Select(memory => new ExportedThreadMemory(
                        memory.Id, memory.ThreadId, memory.RepositoryId, memory.PullRequestId, memory.FilePath,
                        memory.ChangeExcerpt, memory.CommentHistoryDigest, memory.ResolutionSummary, memory.CreatedAt,
                        memory.UpdatedAt))
                    .ToList()))
            .SingleAsync(ct).ConfigureAwait(false);

        // The client is written without its closing brace, and its review jobs follow page by page, so a large tenant
        // does not have to fit into memory.
        writer.WriteStartObject();
        foreach (var property in JsonSerializer.SerializeToElement(client, ExportJson).EnumerateObject())
        {
            property.WriteTo(writer);
        }

        writer.WriteStartArray("reviewJobs");
        var pageSize = options.IncludeProtocols ? ExportPageSizeWithProtocols : ExportPageSize;
        for (var offset = 0;; offset += pageSize)
        {
            var page = await database.ReviewJobs.AsNoTracking().Where(job => job.ClientId == clientId)
                .OrderBy(job => job.SubmittedAt).ThenBy(job => job.Id).Skip(offset).Take(pageSize)
                .Select(job => new ExportedJob(
                    job.Id, job.Status, job.Provider, job.HostBaseUrl, job.RepositoryId, job.RepositoryProjectPath,
                    job.PullRequestId, job.IterationId, job.PrTitle, job.PrSourceBranch, job.PrTargetBranch,
                    new ExportedAuthor(job.PrAuthorExternalUserId, job.PrAuthorLogin, job.PrAuthorDisplayName),
                    job.SubmittedAt, job.CompletedAt, job.ResultSummary, job.Result, job.TotalEstimatedCostUsd,
                    job.FileReviewResults.OrderBy(file => file.FilePath)
                        .Select(file => new ExportedFileResult(
                            file.FilePath, file.IsComplete, file.IsFailed, file.IsExcluded, file.PerFileSummary, file.Comments))
                        .ToList(),
                    null))
                .ToListAsync(ct).ConfigureAwait(false);
            if (options.IncludeProtocols && page.Count > 0)
            {
                var protocols = await this.ProtocolsAsync(page.Select(job => job.Id).ToList(), ct).ConfigureAwait(false);
                page = [.. page.Select(job => job with { Protocols = protocols.GetValueOrDefault(job.Id) ?? [] })];
            }

            foreach (var job in page)
            {
                JsonSerializer.Serialize(writer, job, ExportJson);
            }

            await writer.FlushAsync(ct).ConfigureAwait(false);
            if (page.Count < pageSize)
            {
                break;
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private async Task<Dictionary<Guid, List<ExportedProtocol>>> ProtocolsAsync(List<Guid> jobIds, CancellationToken ct)
    {
        var protocols = await database.ReviewJobProtocols.AsNoTracking()
            .Where(protocol => protocol.JobId != null && jobIds.Contains(protocol.JobId.Value))
            .OrderBy(protocol => protocol.StartedAt)
            .Select(protocol => new
            {
                JobId = protocol.JobId!.Value,
                Protocol = new ExportedProtocol(
                    protocol.Id, protocol.AttemptNumber, protocol.Label, protocol.Outcome, protocol.StartedAt,
                    protocol.CompletedAt,
                    protocol.Events.OrderBy(protocolEvent => protocolEvent.OccurredAt)
                        .Select(protocolEvent => new ExportedEvent(
                            protocolEvent.Kind, protocolEvent.Name, protocolEvent.OccurredAt, protocolEvent.InputTokens,
                            protocolEvent.OutputTokens, protocolEvent.InputTextSample, protocolEvent.OutputSummary,
                            protocolEvent.Error))
                        .ToList()),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        return protocols.GroupBy(item => item.JobId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Protocol).ToList());
    }

    private sealed record ExportedTenant(Guid Id, string Slug, string DisplayName, bool IsActive, DateTimeOffset CreatedAt, int? ProtocolRetentionDays);

    private sealed record ExportedClient(
        Guid Id,
        string DisplayName,
        bool IsActive,
        DateTimeOffset CreatedAt,
        string? CustomSystemMessage,
        string OutputLanguage,
        List<ExportedConnection> ScmConnections,
        List<ExportedScope> ScmScopes,
        List<ExportedThreadMemory> ThreadMemory);

    private sealed record ExportedConnection(Guid Id, ScmProvider Provider, string HostBaseUrl, string DisplayName, bool IsActive, DateTimeOffset CreatedAt);

    private sealed record ExportedScope(Guid Id, Guid ConnectionId, string ScopeType, string ScopePath, string DisplayName, bool IsEnabled);

    private sealed record ExportedThreadMemory(
        Guid Id,
        string ThreadId,
        string RepositoryId,
        int PullRequestId,
        string? FilePath,
        string? ChangeExcerpt,
        string CommentHistoryDigest,
        string ResolutionSummary,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ExportedAuthor(string? ExternalUserId, string? Login, string? DisplayName);

    private sealed record ExportedFileResult(
        string FilePath,
        bool IsComplete,
        bool IsFailed,
        bool IsExcluded,
        string? Summary,
        IReadOnlyList<ReviewComment>? Comments);

    private sealed record ExportedEvent(
        ProtocolEventKind Kind,
        string Name,
        DateTimeOffset OccurredAt,
        long? InputTokens,
        long? OutputTokens,
        string? InputTextSample,
        string? OutputSummary,
        string? Error);

    private sealed record ExportedProtocol(
        Guid Id,
        int Attempt,
        string? Label,
        string? Outcome,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        List<ExportedEvent> Events);

    private sealed record ExportedJob(
        Guid Id,
        JobStatus Status,
        ScmProvider Provider,
        string? HostBaseUrl,
        string RepositoryId,
        string? RepositoryPath,
        int PullRequestId,
        int IterationId,
        string? Title,
        string? SourceBranch,
        string? TargetBranch,
        ExportedAuthor Author,
        DateTimeOffset SubmittedAt,
        DateTimeOffset? CompletedAt,
        string? Summary,
        ReviewResult? Result,
        decimal? EstimatedCostUsd,
        List<ExportedFileResult> FileResults,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        List<ExportedProtocol>? Protocols);

    /// <summary>The tenant, the clients and the knowledge sources whose work a deletion stops.</summary>
    /// <param name="TenantId">The tenant, when the deletion removes the whole tenant.</param>
    /// <param name="ClientIds">The clients that the deletion removes.</param>
    /// <param name="SourceIds">The knowledge sources of the clients.</param>
    /// <param name="StopReason">The error message of the jobs that the deletion stops or cancels.</param>
    private sealed record DeletionScope(Guid? TenantId, List<Guid> ClientIds, List<Guid> SourceIds, string StopReason);

    /// <summary>Deletes rows and adds the number of deleted rows to their table.</summary>
    private sealed class RowDeletion(MeisterProPRDbContext database, Dictionary<string, int> deleted, CancellationToken ct)
    {
        public async Task RemoveAsync<T>(IQueryable<T> rows)
            where T : class
        {
            var table = database.Model.FindEntityType(typeof(T))!.GetTableName()!;
            deleted[table] = deleted.GetValueOrDefault(table) + await rows.ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
    }
}
