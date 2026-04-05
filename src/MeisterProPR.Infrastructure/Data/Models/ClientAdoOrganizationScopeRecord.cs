// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for a client-scoped Azure DevOps organization scope.</summary>
public sealed class ClientAdoOrganizationScopeRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public string OrganizationUrl { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AdoOrganizationVerificationStatus VerificationStatus { get; set; } = AdoOrganizationVerificationStatus.Unknown;
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastVerificationError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ClientRecord? Client { get; set; }
    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
