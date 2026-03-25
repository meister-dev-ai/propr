using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.AzureDevOps.Stub;

/// <summary>
///     No-op implementation of <see cref="IReviewContextToolsFactory" /> used when ADO_STUB_PR is enabled.
///     Returns a <see cref="NullReviewContextTools" /> instance that produces empty results for all tool calls.
/// </summary>
public sealed class StubReviewContextToolsFactory : IReviewContextToolsFactory
{
    /// <inheritdoc />
    public IReviewContextTools Create(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId)
    {
        return new NullReviewContextTools();
    }
}

/// <summary>No-op <see cref="IReviewContextTools" /> that returns empty results for all calls.</summary>
internal sealed class NullReviewContextTools : IReviewContextTools
{
    /// <inheritdoc />
    public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <inheritdoc />
    public Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        return Task.FromResult("");
    }
}
