// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for the tenant boundary.</summary>
public sealed class TenantRecord
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public bool LocalLoginEnabled { get; set; } = true;

    /// <summary>
    ///     Provider families this tenant's clients may use, as provider identity keys. Empty means unrestricted —
    ///     the one reading under which a tenant that has never set a policy keeps working. An entry no loaded
    ///     family claims is kept, and permits nothing.
    /// </summary>
    public string[] AllowedAiProviderKinds { get; set; } = [];

    /// <summary>
    ///     Endpoint hosts this tenant's clients may send AI traffic to. Empty means unrestricted. An entry
    ///     matches a host exactly, or any subdomain of it when written with a leading dot.
    /// </summary>
    public string[] AllowedAiEndpointHosts { get; set; } = [];

    /// <summary>
    ///     Whether this tenant's review jobs capture model reasoning into the protocol.
    ///     <see cref="ReasoningCapturePolicy.InstallationDefault" /> leaves the decision to the installation
    ///     switch, which is how a tenant that has never stated a policy reads.
    /// </summary>
    public ReasoningCapturePolicy ReasoningCapturePolicy { get; set; } = ReasoningCapturePolicy.InstallationDefault;

    /// <summary>
    ///     Optional soft USD cap on the month-to-date review spend of every client in this tenant. When the
    ///     tenant's month-to-date spend reaches it, new review jobs are held. Null means no limit.
    /// </summary>
    public decimal? MonthlyBudgetSoftCapUsd { get; set; }

    /// <summary>
    ///     Optional hard USD cap on the month-to-date review spend of every client in this tenant. When the
    ///     tenant's month-to-date spend reaches it, further model calls are cut. Null means no limit.
    /// </summary>
    public decimal? MonthlyBudgetHardCapUsd { get; set; }

    /// <summary>
    ///     Optional per-file byte limit for this tenant's reviews, overriding the installation value. Null
    ///     leaves the installation value in force.
    /// </summary>
    public int? AiMaxFileSizeBytes { get; set; }

    /// <summary>
    ///     Optional structural-parse byte limit for this tenant's reviews, overriding the installation value.
    ///     Null leaves the installation value in force.
    /// </summary>
    public int? AiMaxStructuralParseBytes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<ClientRecord> Clients { get; set; } = [];
    public ICollection<TenantMembershipRecord> Memberships { get; set; } = [];
    public ICollection<TenantSsoProviderRecord> SsoProviders { get; set; } = [];
    public ICollection<ExternalIdentityRecord> ExternalIdentities { get; set; } = [];
    public ICollection<TenantAuditEntryRecord> AuditEntries { get; set; } = [];
}
