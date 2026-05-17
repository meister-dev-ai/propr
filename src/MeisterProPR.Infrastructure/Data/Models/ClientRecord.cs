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
    public Guid TenantId { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    ///     Determines how the reviewer behaves when automatically resolving its own comment threads.
    ///     Defaults to <see cref="Domain.Enums.CommentResolutionBehavior.Silent" />.
    /// </summary>
    public CommentResolutionBehavior CommentResolutionBehavior { get; set; } = CommentResolutionBehavior.Silent;

    /// <summary>Default review strategy used when intake does not provide an override.</summary>
    public ReviewStrategy DefaultReviewStrategy { get; set; } = ReviewStrategy.FileByFile;

    /// <summary>Optional custom AI system message for this client.</summary>
    public string? CustomSystemMessage { get; set; }

    /// <summary>
    ///     Controls whether newly generated review comments are published back to the SCM provider.
    ///     Defaults to <see langword="true" /> so existing clients continue using visible review publication.
    /// </summary>
    public bool ScmCommentPostingEnabled { get; set; } = true;

    /// <summary>
    ///     Controls whether ProRV executes during review generation for this client.
    ///     Defaults to <see langword="true" /> so existing clients keep current verification behavior.
    /// </summary>
    public bool EnableProRV { get; set; } = true;

    public TenantRecord? Tenant { get; set; }

    public ICollection<ClientScmConnectionRecord> ScmConnections { get; set; } = [];

    public ICollection<ProviderConnectionAuditEntryRecord> ProviderConnectionAuditEntries { get; set; } = [];

    public ICollection<ClientReviewerIdentityRecord> ReviewerIdentities { get; set; } = [];

    public ICollection<CrawlConfigurationRecord> CrawlConfigurations { get; set; } = [];
}
