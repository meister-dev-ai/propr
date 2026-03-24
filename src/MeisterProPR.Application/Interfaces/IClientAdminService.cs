using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>Admin CRUD operations for managing clients. Only available in DB mode.</summary>
public interface IClientAdminService
{
    /// <summary>Returns all clients ordered by creation date descending.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ClientDto>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Returns a single client by ID, or <c>null</c> if not found.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto?> GetByIdAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Creates a new active client and returns its data.
    ///     Returns <c>null</c> when a client with <paramref name="key" /> already exists.
    /// </summary>
    /// <param name="key">The unique client API key.</param>
    /// <param name="displayName">Human-readable name for the client.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto?> CreateAsync(string key, string displayName, CancellationToken ct = default);

    /// <summary>
    ///     Applies partial updates to a client.
    ///     Returns the updated client, or <c>null</c> if not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="isActive">When non-null, sets the active flag.</param>
    /// <param name="displayName">When non-null, replaces the display name.</param>
    /// <param name="commentResolutionBehavior">When non-null, sets the comment resolution behavior.</param>
    /// <param name="customSystemMessage">
    ///     When non-null, updates the custom AI system message.
    ///     Pass an empty string to clear an existing value (sets the stored value to <see langword="null" />).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<ClientDto?> PatchAsync(
        Guid clientId,
        bool? isActive,
        string? displayName,
        CommentResolutionBehavior? commentResolutionBehavior = null,
        string? customSystemMessage = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes a client and all its crawl configurations.
    ///     Returns <c>false</c> if the client was not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> DeleteAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns <c>true</c> when a client with <paramref name="clientId" /> exists.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ExistsAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Sets the ADO reviewer identity GUID for a client.
    ///     Returns <c>false</c> if the client was not found.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="reviewerId">ADO identity GUID of the AI service account.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> SetReviewerIdentityAsync(Guid clientId, Guid reviewerId, CancellationToken ct = default);
}
