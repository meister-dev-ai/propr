// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for one provider-neutral AI connection profile.
/// </summary>
public sealed class AiConnectionProfileRecord
{
    public Guid Id { get; set; }

    public Guid ClientId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string ProviderKind { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string AuthMode { get; set; } = string.Empty;

    public string? ProtectedSecret { get; set; }

    public Dictionary<string, string> DefaultHeaders { get; set; } = [];

    public Dictionary<string, string> DefaultQueryParams { get; set; } = [];

    public string DiscoveryMode { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AiConfiguredModelRecord> ConfiguredModels { get; set; } = [];

    public ICollection<AiPurposeBindingRecord> PurposeBindings { get; set; } = [];

    public AiVerificationSnapshotRecord? VerificationSnapshot { get; set; }

    public ClientRecord? Client { get; set; }
}
