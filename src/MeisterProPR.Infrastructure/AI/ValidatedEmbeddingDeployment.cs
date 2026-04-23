// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     One validated embedding deployment selection ready for runtime use.
/// </summary>
/// <param name="Connection">Owning AI connection DTO.</param>
/// <param name="Model">Selected configured model.</param>
public sealed record ValidatedEmbeddingDeployment(
    AiConnectionDto Connection,
    AiConfiguredModelDto Model)
{
    /// <summary>Gets the selected deployment/model name.</summary>
    public string DeploymentName => this.Model.RemoteModelId;

    /// <summary>Gets the validated capability metadata for the selected deployment.</summary>
    public AiConnectionModelCapabilityDto Capability => new(
        this.Model.RemoteModelId,
        this.Model.TokenizerName ?? string.Empty,
        this.Model.MaxInputTokens ?? 0,
        this.Model.EmbeddingDimensions ?? 0,
        this.Model.InputCostPer1MUsd,
        this.Model.OutputCostPer1MUsd);
}
