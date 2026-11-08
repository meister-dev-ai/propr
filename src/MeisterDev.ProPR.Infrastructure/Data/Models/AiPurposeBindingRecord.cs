// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     EF Core persistence model for one AI purpose binding.
/// </summary>
public sealed class AiPurposeBindingRecord
{
    public Guid Id { get; set; }

    public Guid ConnectionProfileId { get; set; }

    public Guid ConfiguredModelId { get; set; }

    public string Purpose { get; set; } = string.Empty;

    public string ProtocolMode { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>
    ///     The reasoning level the purpose runs at on the bound model, or null when the binding leaves it to the default
    ///     level of the configured model. The workspace selection sets it, and a save of the connection keeps it while the
    ///     binding keeps its model.
    /// </summary>
    public string? ReasoningLevel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public AiConnectionProfileRecord? ConnectionProfile { get; set; }

    public AiConfiguredModelRecord? ConfiguredModel { get; set; }
}
