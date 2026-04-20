// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a client-owned webhook configuration.</summary>
public sealed class WebhookConfigurationRecord
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public ClientRecord Client { get; set; } = null!;
    public WebhookProviderType ProviderType { get; set; } = WebhookProviderType.AzureDevOps;
    public string PublicPathKey { get; set; } = string.Empty;
    public Guid? OrganizationScopeId { get; set; }
    public ClientScmScopeRecord? OrganizationScope { get; set; }
    public string OrganizationUrl { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string SecretCiphertext { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string[] EnabledEvents { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<WebhookRepoFilterRecord> RepoFilters { get; set; } = [];
    public ICollection<WebhookDeliveryLogEntryRecord> DeliveryLogs { get; set; } = [];
}
