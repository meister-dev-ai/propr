// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.TenantAudit;

namespace MeisterDev.ProPR.Application.Features.PlatformAdminElevation;

/// <summary>The state of an elevation at the time it is read.</summary>
public enum PlatformAdminElevationState
{
    /// <summary>The elevation grants its scope until it expires.</summary>
    Active,

    /// <summary>A platform administrator ended the elevation before it expired.</summary>
    Ended,

    /// <summary>The elevation reached its expiry.</summary>
    Expired,
}

/// <summary>
///     A time-bound grant with which a platform administrator reads the content of one tenant, or of one client of a
///     tenant, read-only.
/// </summary>
/// <param name="Id">The elevation.</param>
/// <param name="AdministratorUserId">The platform administrator, or null when the account was deleted.</param>
/// <param name="AdministratorName">The user name of the platform administrator when the elevation started.</param>
/// <param name="TenantId">The tenant the elevation covers.</param>
/// <param name="TenantDisplayName">The display name of that tenant.</param>
/// <param name="ClientId">The one client the elevation covers, or null when it covers every client of the tenant.</param>
/// <param name="ClientDisplayName">The display name of that client.</param>
/// <param name="Reason">Why the platform administrator started the elevation, for example a ticket number.</param>
/// <param name="StartedAt">When the elevation started.</param>
/// <param name="ExpiresAt">When the elevation expires.</param>
/// <param name="EndedAt">When the elevation ended early or its expiry was recorded, or null.</param>
/// <param name="EndedByName">The user name of the platform administrator who ended the elevation early, or null.</param>
/// <param name="State">The state of the elevation when it was read.</param>
/// <param name="ContentReads">How many content reads ProPR recorded under the elevation.</param>
public sealed record PlatformAdminElevationDto(
    Guid Id,
    Guid? AdministratorUserId,
    string AdministratorName,
    Guid TenantId,
    string TenantDisplayName,
    Guid? ClientId,
    string? ClientDisplayName,
    string Reason,
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? EndedAt,
    string? EndedByName,
    PlatformAdminElevationState State,
    int ContentReads);

/// <summary>The outcome of a request to start an elevation.</summary>
public enum StartElevationOutcome
{
    /// <summary>The elevation started.</summary>
    Started,

    /// <summary>The tenant does not exist.</summary>
    TenantNotFound,

    /// <summary>The client does not exist or does not belong to the tenant.</summary>
    ClientNotFound,

    /// <summary>
    ///     The tenant is the System tenant, whose content platform administrators read without an elevation because it
    ///     belongs to the installation.
    /// </summary>
    SystemTenant,

    /// <summary>The duration is below one minute or above the maximum of the installation.</summary>
    InvalidDuration,

    /// <summary>The reason is empty or longer than <see cref="PlatformAdminElevationLimits.MaxReasonLength" /> characters.</summary>
    InvalidReason,

    /// <summary>The caller is not an active platform administrator.</summary>
    NotPlatformAdministrator,
}

/// <summary>The answer to a request to start an elevation.</summary>
/// <param name="Outcome">Whether the elevation started, and why not.</param>
/// <param name="Elevation">The elevation, when it started.</param>
public sealed record StartElevationResult(StartElevationOutcome Outcome, PlatformAdminElevationDto? Elevation);

/// <summary>The outcome of a request to end an elevation early.</summary>
public enum EndElevationOutcome
{
    /// <summary>The elevation ended.</summary>
    Ended,

    /// <summary>The elevation does not exist.</summary>
    NotFound,

    /// <summary>The elevation had already ended or expired.</summary>
    AlreadyOver,
}

/// <summary>The answer to a request to end an elevation early.</summary>
/// <param name="Outcome">Whether the elevation ended, and why not.</param>
/// <param name="Elevation">The elevation as it is after the request, when it exists.</param>
public sealed record EndElevationResult(EndElevationOutcome Outcome, PlatformAdminElevationDto? Elevation);

/// <summary>
///     Why a platform administrator may read the content of a client, or that they may not. A role the administrator
///     holds on the client through a tenant membership or a client assignment grants no content: outside the System
///     tenant only an elevation does.
/// </summary>
public enum ContentAccessKind
{
    /// <summary>
    ///     The client belongs to the System tenant, which the installation itself administers. ProPR treats a client
    ///     without a tenant the same way, as everywhere else; a migrated database holds no such client.
    /// </summary>
    Installation,

    /// <summary>An active elevation of the administrator covers the client.</summary>
    Elevation,

    /// <summary>Nothing grants the administrator the content of the client.</summary>
    Refused,
}

/// <summary>What a platform administrator may read of one client.</summary>
/// <param name="ClientId">The client.</param>
/// <param name="ClientDisplayName">The display name of the client.</param>
/// <param name="TenantId">The tenant of the client; <see cref="Guid.Empty" /> for a client without a tenant.</param>
/// <param name="Kind">Why the administrator may read the content, or that they may not.</param>
/// <param name="Elevation">The elevation that grants the content, for <see cref="ContentAccessKind.Elevation" />.</param>
public sealed record ContentAccessGrant(
    Guid ClientId,
    string ClientDisplayName,
    Guid TenantId,
    ContentAccessKind Kind,
    PlatformAdminElevationDto? Elevation = null);

/// <summary>A content read of a platform administrator under one elevation, for the audit history of the tenant.</summary>
/// <param name="Elevation">The elevation the read happened under.</param>
/// <param name="Clients">The clients whose content the read returned.</param>
/// <param name="Resource">What the read returned, as a noun phrase, for example <c>the protocol of a review job</c>.</param>
/// <param name="Request">The HTTP method and path of the request.</param>
/// <param name="ReviewJobId">The review job or thread pass the read concerned, when it concerned one.</param>
public sealed record ContentRead(
    PlatformAdminElevationDto Elevation,
    IReadOnlyList<ContentAccessGrant> Clients,
    string Resource,
    string Request,
    Guid? ReviewJobId);

/// <summary>Limits of the values a platform administrator enters for an elevation.</summary>
public static class PlatformAdminElevationLimits
{
    /// <summary>The longest reason, in characters.</summary>
    public const int MaxReasonLength = 500;
}

/// <summary>The event types ProPR writes to the audit history of a tenant for elevations.</summary>
public static class PlatformAdminElevationAuditEvents
{
    /// <summary>A platform administrator started an elevation.</summary>
    public const string Started = "elevation.started";

    /// <summary>A platform administrator ended an elevation before its expiry.</summary>
    public const string Ended = "elevation.ended";

    /// <summary>An elevation reached its expiry.</summary>
    public const string Expired = "elevation.expired";

    /// <summary>A platform administrator read content under an elevation.</summary>
    public const string ContentRead = "elevation.content_read";
}

/// <summary>
///     Starts, ends and lists the elevations of platform administrators, decides which content a platform
///     administrator may read, and records the reads in the audit history of the tenant.
/// </summary>
public interface IPlatformAdminElevationService
{
    /// <summary>The longest duration, in minutes, that the installation permits for one elevation.</summary>
    int MaxDurationMinutes { get; }

    /// <summary>Starts an elevation and records the start in the audit history of the tenant.</summary>
    /// <param name="administratorUserId">The platform administrator, who starts the elevation for themselves.</param>
    /// <param name="tenantId">The tenant the elevation covers.</param>
    /// <param name="clientId">The one client of the tenant the elevation covers, or null for every client of the tenant.</param>
    /// <param name="reason">Why the administrator needs the content, for example a ticket number.</param>
    /// <param name="durationMinutes">How long the elevation lasts, up to <see cref="MaxDurationMinutes" />.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StartElevationResult> StartAsync(
        Guid administratorUserId,
        Guid tenantId,
        Guid? clientId,
        string reason,
        int durationMinutes,
        CancellationToken ct = default);

    /// <summary>Ends an active elevation and records the end in the audit history of the tenant.</summary>
    /// <param name="elevationId">The elevation.</param>
    /// <param name="actorUserId">The platform administrator who ends it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<EndElevationResult> EndAsync(Guid elevationId, Guid actorUserId, CancellationToken ct = default);

    /// <summary>Returns the active elevations of one platform administrator, the one that expires first first.</summary>
    Task<IReadOnlyList<PlatformAdminElevationDto>> GetActiveAsync(Guid administratorUserId, CancellationToken ct = default);

    /// <summary>Returns one page of the elevations of all platform administrators, the newest first.</summary>
    /// <param name="tenantId">Limits the page to the elevations of one tenant, when set.</param>
    /// <param name="page">The page, starting at 1.</param>
    /// <param name="pageSize">The elevations per page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PlatformAdminElevationDto>> ListAsync(Guid? tenantId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Returns one elevation, or null when it does not exist.</summary>
    Task<PlatformAdminElevationDto?> GetAsync(Guid elevationId, CancellationToken ct = default);

    /// <summary>Returns the audit entries recorded under one elevation, the oldest first.</summary>
    Task<IReadOnlyList<TenantAuditEntryDto>> GetAuditEntriesAsync(Guid elevationId, CancellationToken ct = default);

    /// <summary>Returns the client that owns a review job or a thread pass, or null when neither exists.</summary>
    Task<Guid?> GetReviewClientIdAsync(Guid reviewJobOrThreadPassId, CancellationToken ct = default);

    /// <summary>
    ///     Decides, for each of the given clients that exists, whether a platform administrator may read its content.
    /// </summary>
    /// <param name="administratorUserId">The platform administrator.</param>
    /// <param name="clientIds">The clients to decide for. A client that does not exist has no entry in the answer.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ContentAccessGrant>> ResolveAsync(
        Guid administratorUserId,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns every client whose content a platform administrator may read: the clients of the System tenant and
    ///     the clients an active elevation of the administrator covers.
    /// </summary>
    Task<IReadOnlyList<ContentAccessGrant>> ResolveReadableAsync(Guid administratorUserId, CancellationToken ct = default);

    /// <summary>Records content reads in the audit histories of the tenants of their elevations.</summary>
    Task RecordReadsAsync(Guid administratorUserId, IReadOnlyList<ContentRead> reads, CancellationToken ct = default);

    /// <summary>
    ///     Records the expiry of every elevation that reached its expiry without an end, once per elevation, and returns
    ///     how many it recorded.
    /// </summary>
    Task<int> RecordExpiriesAsync(CancellationToken ct = default);
}
