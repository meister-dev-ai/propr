// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a repository-scope filter on a webhook configuration.</summary>
public sealed class WebhookRepoFilterRecord
{
    public Guid Id { get; set; }
    public Guid WebhookConfigurationId { get; set; }
    public string RepositoryName { get; set; } = string.Empty;
    public string? SourceProvider { get; set; }
    public string? CanonicalSourceRef { get; set; }
    public string? DisplayName { get; set; }
    public string[] TargetBranchPatterns { get; set; } = [];
    public WebhookConfigurationRecord? WebhookConfiguration { get; set; }
}
