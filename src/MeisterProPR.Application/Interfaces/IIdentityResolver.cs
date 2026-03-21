namespace MeisterProPR.Application.Interfaces;

/// <summary>Resolves Azure DevOps VSS identity IDs by display name.</summary>
public interface IIdentityResolver
{
    /// <summary>
    ///     Searches for identities in the given ADO organisation whose display name matches
    ///     <paramref name="displayName" />.  Returns all matches so the caller can disambiguate.
    /// </summary>
    Task<IReadOnlyList<ResolvedIdentity>> ResolveAsync(
        string organizationUrl,
        string displayName,
        Guid clientId,
        CancellationToken ct = default);
}

/// <summary>A resolved ADO identity.</summary>
/// <param name="Id">VSS identity GUID — use this as <c>reviewerId</c> in crawl configurations.</param>
/// <param name="DisplayName">Human-readable display name.</param>
public sealed record ResolvedIdentity(Guid Id, string DisplayName);
