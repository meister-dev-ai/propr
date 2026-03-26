namespace MeisterProPR.Domain.Entities;

/// <summary>A user-generated Personal Access Token, stored as a one-way hash.</summary>
public sealed class UserPat
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>User who generated this PAT.</summary>
    public Guid UserId { get; set; }

    /// <summary>BCrypt hash of the plaintext token. The plaintext is returned only once at creation.</summary>
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>Human-readable label for the PAT.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Optional expiry. Null means the PAT does not expire.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When the PAT was created.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the PAT was last used. Updated on each authenticated request.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>Whether the PAT has been manually revoked.</summary>
    public bool IsRevoked { get; set; }
}
