// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for append-only provider-connection operational audit entries.</summary>
public sealed class ProviderConnectionAuditEntryRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConnectionId { get; set; }
    public ScmProvider Provider { get; set; }
    public string HostBaseUrl { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FailureCategory { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public ClientRecord? Client { get; set; }
}
