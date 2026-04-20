// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for a configured provider reviewer identity.</summary>
public sealed class ClientReviewerIdentityRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid ConnectionId { get; set; }
    public ScmProvider Provider { get; set; }
    public string ExternalUserId { get; set; } = string.Empty;
    public string Login { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ClientRecord? Client { get; set; }
    public ClientScmConnectionRecord? Connection { get; set; }
}
