// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a registered API client.</summary>
public sealed class ClientRecord
{
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public Guid? ReviewerId { get; set; }

    /// <summary>
    ///     Determines how the reviewer behaves when automatically resolving its own comment threads.
    ///     Defaults to <see cref="Domain.Enums.CommentResolutionBehavior.Silent" />.
    /// </summary>
    public CommentResolutionBehavior CommentResolutionBehavior { get; set; } = CommentResolutionBehavior.Silent;

    /// <summary>Optional custom AI system message for this client.</summary>
    public string? CustomSystemMessage { get; set; }

    public ICollection<ClientScmConnectionRecord> ScmConnections { get; set; } = [];

    public ICollection<ProviderConnectionAuditEntryRecord> ProviderConnectionAuditEntries { get; set; } = [];

    public ICollection<ClientReviewerIdentityRecord> ReviewerIdentities { get; set; } = [];

    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
