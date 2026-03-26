namespace MeisterProPR.Domain.Entities;

/// <summary>A server-persisted refresh token used to obtain new JWT access tokens.</summary>
public sealed class RefreshToken
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>User this refresh token belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 hash of the plaintext token value.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>When this refresh token expires.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When this refresh token was issued.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When this refresh token was revoked, if applicable.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>True if the token has been explicitly revoked or has expired.</summary>
    public bool IsActive => this.RevokedAt is null && this.ExpiresAt > DateTimeOffset.UtcNow;
}
