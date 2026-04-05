// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     One validated embedding deployment selection ready for runtime use.
/// </summary>
/// <param name="Connection">Owning AI connection DTO.</param>
/// <param name="DeploymentName">Selected deployment/model name.</param>
/// <param name="Capability">Validated capability metadata for the selected deployment.</param>
public sealed record ValidatedEmbeddingDeployment(
    AiConnectionDto Connection,
    string DeploymentName,
    AiConnectionModelCapabilityDto Capability);
