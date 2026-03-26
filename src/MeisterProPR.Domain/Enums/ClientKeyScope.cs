namespace MeisterProPR.Domain.Enums;

/// <summary>Bitfield that controls which API operations a client key is permitted to perform.</summary>
[Flags]
public enum ClientKeyScope
{
    /// <summary>No access.</summary>
    None = 0,

    /// <summary>Allows posting review comments (the webhook path).</summary>
    PostReview = 1,

    /// <summary>Allows querying review jobs.</summary>
    ViewJobs = 2,

    /// <summary>Allows viewing protocol traces.</summary>
    ViewProtocol = 4,

    /// <summary>Full access (all scopes combined).</summary>
    All = PostReview | ViewJobs | ViewProtocol,
}
