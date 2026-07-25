// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Raised when a model is defined that the catalog snapshot already describes. Accepting it would be
///     misleading: the snapshot supplies capabilities for any model it knows, so the definition's own capability
///     values would be silently ignored. The operator wants a pricing override instead.
/// </summary>
/// <param name="providerId">Catalog provider identifier.</param>
/// <param name="remoteModelId">Model identifier as the provider knows it.</param>
public sealed class ModelCatalogDefinitionConflictException(string providerId, string remoteModelId)
    : InvalidOperationException(
        $"The catalog already describes '{remoteModelId}' for provider '{providerId}'. Record a pricing override for it instead of defining it again.")
{
    /// <summary>Catalog provider identifier.</summary>
    public string ProviderId { get; } = providerId;

    /// <summary>Model identifier as the provider knows it.</summary>
    public string RemoteModelId { get; } = remoteModelId;
}
