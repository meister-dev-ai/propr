using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>Data transfer object for an AI connection (API key is never included).</summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="ClientId">Owning client ID.</param>
/// <param name="DisplayName">Human-readable display name.</param>
/// <param name="EndpointUrl">Azure OpenAI or AI Foundry endpoint URL.</param>
/// <param name="Models">Available model deployment names.</param>
/// <param name="IsActive">Whether this is the active connection for the client.</param>
/// <param name="ActiveModel">Selected model when active; null otherwise.</param>
/// <param name="CreatedAt">When created.</param>
/// <param name="ModelCategory">Optional model category for tier-based routing. Null means default connection.</param>
public sealed record AiConnectionDto(
    Guid Id,
    Guid ClientId,
    string DisplayName,
    string EndpointUrl,
    IReadOnlyList<string> Models,
    bool IsActive,
    string? ActiveModel,
    DateTimeOffset CreatedAt,
    AiConnectionModelCategory? ModelCategory = null);
