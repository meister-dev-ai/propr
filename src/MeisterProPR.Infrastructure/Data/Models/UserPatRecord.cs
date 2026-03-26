namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for <see cref="Domain.Entities.UserPat"/>.</summary>
public sealed class UserPatRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }

    public AppUserRecord? User { get; set; }
}
