namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF Core persistence model for a registered API client.</summary>
public sealed class ClientRecord
{
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string? AdoTenantId { get; set; }
    public string? AdoClientId { get; set; }
    public string? AdoClientSecret { get; set; }
    public Guid? ReviewerId { get; set; }
}