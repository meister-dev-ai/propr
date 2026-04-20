// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for one selected provider scope under a client SCM connection.</summary>
public sealed class ClientScmScopeRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConnectionId { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public string ExternalScopeId { get; set; } = string.Empty;
    public string ScopePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string VerificationStatus { get; set; } = "unknown";
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset? LastVerifiedAt { get; set; }
    public string? LastVerificationError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ClientRecord? Client { get; set; }
    public ClientScmConnectionRecord? Connection { get; set; }
    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
