// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for an AI connection. The API key is excluded from JSON serialization.</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="EndpointUrl">Azure OpenAI or AI Foundry endpoint URL.</param>
/// <param name="Models">Available model deployment names.</param>
/// <param name="IsActive">Whether this is the active connection for the client.</param>
/// <param name="ActiveModel">Selected model when active; null otherwise.</param>
/// <param name="CreatedAt">When created.</param>
/// <param name="ModelCategory">Optional model category for tier-based routing. Null means default connection.</param>
/// <param name="ModelCapabilities">Optional per-deployment embedding capability metadata.</param>
/// <param name="ApiKey">Optional API key for internal use only; never serialized to JSON.</param>
public sealed record AiConnectionDto(
    Guid Id,
    Guid ClientId,
    string DisplayName,
    string EndpointUrl,
    IReadOnlyList<string> Models,
    bool IsActive,
    string? ActiveModel,
    DateTimeOffset CreatedAt,
    AiConnectionModelCategory? ModelCategory = null,
    IReadOnlyList<AiConnectionModelCapabilityDto>? ModelCapabilities = null,
    [property: JsonIgnore] string? ApiKey = null);
