using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Data.Models;

/// <summary>EF persistence model for <see cref="Domain.Entities.UserClientRole"/>.</summary>
public sealed class UserClientRoleRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
    public ClientRole Role { get; set; }
    public DateTimeOffset AssignedAt { get; set; }

    public AppUserRecord? User { get; set; }
}
