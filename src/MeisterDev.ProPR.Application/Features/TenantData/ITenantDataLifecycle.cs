// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.TenantData;

/// <summary>The rows a tenant deletion removed, per table.</summary>
/// <param name="DeletedRows">The rows each statement removed, by table. Rows that foreign keys cascaded are not listed.</param>
public sealed record TenantDeletionResult(Guid TenantId, IReadOnlyDictionary<string, int> DeletedRows);

/// <summary>The work of a tenant or a client that a deletion stopped and still waits for.</summary>
/// <param name="RetryAfter">The time after which the caller requests the deletion again.</param>
/// <param name="Running">The executions that may still be running, by kind.</param>
public sealed record DeletionPending(TimeSpan RetryAfter, IReadOnlyDictionary<string, int> Running);

/// <summary>The answer to a tenant deletion request: the deletion, or the work it waits for.</summary>
/// <param name="Deleted">The deleted rows, when the tenant was deleted.</param>
/// <param name="Pending">The work the deletion waits for, when the tenant was not deleted yet.</param>
public sealed record TenantDeletionAnswer(TenantDeletionResult? Deleted, DeletionPending? Pending);

/// <summary>The rows a client deletion removed, per table.</summary>
/// <param name="DeletedRows">The rows each statement removed, by table. Rows that foreign keys cascaded are not listed.</param>
public sealed record ClientDeletionResult(Guid ClientId, IReadOnlyDictionary<string, int> DeletedRows);

/// <summary>The answer to a client deletion request: the deletion, or the work it waits for.</summary>
/// <param name="Deleted">The deleted rows, when the client was deleted.</param>
/// <param name="Pending">The work the deletion waits for, when the client was not deleted yet.</param>
public sealed record ClientDeletionAnswer(ClientDeletionResult? Deleted, DeletionPending? Pending);

/// <summary>What a tenant export contains besides configuration and review records.</summary>
/// <param name="IncludeProtocols">Whether the protocols of the review jobs and their events are included.</param>
public sealed record TenantExportOptions(bool IncludeProtocols);

/// <summary>A pull request author, as the provider identifies the account.</summary>
/// <param name="ExternalUserId">The provider's stable identifier of the account.</param>
/// <param name="Login">The login of the account, when known; comments that name only the login are matched by it.</param>
public sealed record AuthorIdentity(string ExternalUserId, string? Login);

/// <summary>The records an identity erasure changed.</summary>
/// <param name="ReviewJobs">Review jobs whose pull request author was erased.</param>
/// <param name="MentionReplies">Mention replies whose comment author was erased.</param>
/// <param name="RetainedComments">Retained thread comments whose author was erased.</param>
/// <param name="ProtocolEvents">Protocol events of the affected executions, which were deleted because their prompts can name the author.</param>
public sealed record IdentityErasureResult(int ReviewJobs, int MentionReplies, int RetainedComments, int ProtocolEvents);

/// <summary>The tenant cannot be changed this way.</summary>
public sealed class TenantDataLifecycleException(string message) : Exception(message);

/// <summary>Deletes, exports and retains the data of a tenant.</summary>
public interface ITenantDataLifecycle
{
    /// <summary>
    ///     Stops the work of the tenant and deletes the tenant once its work has ended. Each request deactivates the
    ///     tenant, its clients and their webhook, crawl, mention and ProCursor configurations, closes their discussion
    ///     synchronizations and cancels or stops their unfinished jobs. While an execution may still be running, or
    ///     within a quiet period after the request changed anything, the answer names the time to wait. Otherwise the
    ///     request deletes the tenant, its clients and every row that carries one of their identifiers. The system
    ///     tenant cannot be deleted. Accounts that belong only to this tenant are deleted with it.
    /// </summary>
    /// <returns>The answer, or null when the tenant does not exist.</returns>
    /// <exception cref="TenantDataLifecycleException">The tenant is the system tenant.</exception>
    Task<TenantDeletionAnswer?> DeleteTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stops the work of one client and deletes the client once its work has ended. Each request deactivates the
    ///     client and its webhook, crawl, mention and ProCursor configurations, closes its discussion synchronizations
    ///     and cancels or stops its unfinished jobs. While an execution may still be running, or within a quiet period
    ///     after the request changed anything, the answer names the time to wait. Otherwise the request deletes the
    ///     client and every row that carries its identifier. The tenant of the client and its other clients stay.
    /// </summary>
    /// <returns>The answer, or null when the client does not exist.</returns>
    Task<ClientDeletionAnswer?> DeleteClientAsync(Guid clientId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Writes the export of the tenant to <paramref name="output" /> as one JSON document in the format
    ///     <c>propr.tenant-export</c>, version 1. Secrets, credentials and embeddings are left out.
    /// </summary>
    /// <returns>False when the tenant does not exist.</returns>
    Task<bool> WriteExportAsync(
        Guid tenantId, Stream output, TenantExportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Sets how many days protocol events of the tenant are kept; null keeps them.</summary>
    /// <returns>False when the tenant does not exist.</returns>
    Task<bool> SetProtocolRetentionAsync(Guid tenantId, int? days, CancellationToken cancellationToken = default);

    /// <summary>Deletes the protocol events that are older than the retention of their tenant.</summary>
    /// <returns>The deleted events.</returns>
    Task<int> PurgeExpiredProtocolEventsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Erases one developer's identity from the review records of the tenant: the author fields of review jobs,
    ///     mention replies and retained thread comments, and the protocol events of the review jobs, mention replies and
    ///     thread passes that concern the developer. The tenant audit records the erasure with a hash of the identifier
    ///     and the instruction reference.
    /// </summary>
    /// <param name="actorUserId">The administrator who erased the identity, when known.</param>
    /// <param name="instruction">The reference of the controller's instruction, for example a ticket number.</param>
    /// <returns>The changed records, or null when the tenant does not exist.</returns>
    Task<IdentityErasureResult?> EraseAuthorAsync(
        Guid tenantId, AuthorIdentity author, Guid? actorUserId,
        string instruction, CancellationToken cancellationToken = default);
}
