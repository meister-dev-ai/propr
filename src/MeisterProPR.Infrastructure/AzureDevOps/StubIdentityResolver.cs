using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>No-op identity resolver used when <c>ADO_STUB_PR=true</c>.</summary>
public sealed class StubIdentityResolver : IIdentityResolver
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ResolvedIdentity>> ResolveAsync(
        string organizationUrl,
        string displayName,
        Guid clientId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ResolvedIdentity>>([]);
    }
}
