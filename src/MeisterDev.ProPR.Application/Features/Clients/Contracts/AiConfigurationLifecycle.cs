// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Clients.Contracts;

/// <summary>A configured model and its owning connection selected for a workspace slot.</summary>
public sealed record AiWorkspaceModelSelection(Guid ConnectionId, Guid ConfiguredModelId);

/// <summary>The complete workspace selection applied in one operation.</summary>
public sealed record AiWorkspacePurposeSelection(
    AiWorkspaceModelSelection Default,
    AiWorkspaceModelSelection High,
    AiWorkspaceModelSelection Embedding);

/// <summary>The bounded configuration operation outcome without credential material.</summary>
public sealed record AiConfigurationResult(
    bool Applied,
    string? Error = null,
    bool Conflict = false,
    IReadOnlyList<AiPurpose>? ConflictingPurposes = null);

/// <summary>The verification and promotion outcome for an unsaved profile candidate.</summary>
public sealed record AiVerifiedUpdateResult(
    bool Applied,
    AiConnectionDto? Connection = null,
    bool Conflict = false,
    bool NotFound = false);
