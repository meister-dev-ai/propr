namespace MeisterProPR.Domain.Enums;

/// <summary>Global role assigned directly to an application user.</summary>
public enum AppUserRole
{
    /// <summary>Read-only access scoped to assigned clients via <c>UserClientRole</c>.</summary>
    User = 0,

    /// <summary>Full administrative access across all clients and system settings.</summary>
    Admin = 1,
}
