namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries client data across the Application/Infrastructure boundary. The secret key and ADO client secret are
///     never included.
/// </summary>
public sealed record ClientDto(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    bool HasAdoCredentials,
    string? AdoTenantId,
    string? AdoClientId,
    Guid? ReviewerId);
