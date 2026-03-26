namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for <see cref="Domain.Entities.RefreshToken"/>.</summary>
public sealed class RefreshTokenRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    public AppUserRecord? User { get; set; }
}
