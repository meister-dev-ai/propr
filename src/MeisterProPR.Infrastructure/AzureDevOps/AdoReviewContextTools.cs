using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Options;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Azure DevOps-backed implementation of <see cref="IReviewContextTools" />.
///     Each instance is scoped to a single pull request review.
///     File content is cached in memory for the lifetime of the instance to avoid
///     redundant network calls within a single review pass.
/// </summary>
public class AdoReviewContextTools : IReviewContextTools
{
    private readonly Guid? _clientId;
    private readonly VssConnectionFactory _connectionFactory;
    private readonly IClientAdoCredentialRepository _credentialRepository;
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly int _iterationId;
    private readonly AiReviewOptions _options;
    private readonly string _organizationUrl;
    private readonly string _projectId;
    private readonly int _pullRequestId;
    private readonly string _repositoryId;

    /// <summary>
    ///     Initialises a new <see cref="AdoReviewContextTools" /> scoped to the given pull request.
    /// </summary>
    /// <param name="connectionFactory">Factory used to resolve ADO connections.</param>
    /// <param name="credentialRepository">Repository for per-client ADO credentials.</param>
    /// <param name="options">AI review configuration options.</param>
    /// <param name="organizationUrl">Azure DevOps organization URL.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="repositoryId">Repository identifier.</param>
    /// <param name="pullRequestId">Pull request numeric identifier.</param>
    /// <param name="iterationId">Pull request iteration identifier.</param>
    /// <param name="clientId">Optional client identifier for credential lookup.</param>
    public AdoReviewContextTools(
        VssConnectionFactory connectionFactory,
        IClientAdoCredentialRepository credentialRepository,
        IOptions<AiReviewOptions> options,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId)
    {
        this._connectionFactory = connectionFactory;
        this._credentialRepository = credentialRepository;
        this._options = options.Value;
        this._organizationUrl = organizationUrl;
        this._projectId = projectId;
        this._repositoryId = repositoryId;
        this._pullRequestId = pullRequestId;
        this._iterationId = iterationId;
        this._clientId = clientId;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(ct);
        var changes = await gitClient.GetPullRequestIterationChangesAsync(
            this._projectId,
            this._repositoryId,
            this._pullRequestId,
            this._iterationId,
            cancellationToken: ct);

        var summaries = new List<ChangedFileSummary>();
        foreach (var change in changes.ChangeEntries ?? [])
        {
            if (change.Item?.IsFolder == true)
            {
                continue;
            }

            var path = change.Item?.Path ?? "";
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            summaries.Add(new ChangedFileSummary(path, MapChangeType(change.ChangeType)));
        }

        return summaries.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(ct);

        GitBranchStats? branchRef;
        try
        {
            branchRef = await gitClient.GetBranchAsync(
                this._projectId,
                this._repositoryId,
                branch,
                cancellationToken: ct);
        }
        catch (Microsoft.VisualStudio.Services.WebApi.VssServiceResponseException)
        {
            // Branch does not exist in this repository.
            return [];
        }

        var commitSha = branchRef?.Commit?.CommitId;
        if (string.IsNullOrEmpty(commitSha))
        {
            return [];
        }

        var tree = await gitClient.GetTreeAsync(
            this._projectId,
            this._repositoryId,
            commitSha,
            null, // projectId (overload disambiguation)
            true, // recursive
            null, // userState
            ct);

        return (tree?.TreeEntries ?? [])
            .Where(e => e.GitObjectType == GitObjectType.Blob)
            .Select(e => e.RelativePath ?? "")
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        var cacheKey = $"{branch}:{path}";

        if (!this._fileCache.TryGetValue(cacheKey, out var content))
        {
            string? rawContent;
            try
            {
                rawContent = await this.FetchRawFileContentAsync(path, branch, ct);
            }
            catch
            {
                return "";
            }

            if (rawContent is null)
            {
                return "";
            }

            var byteSize = Encoding.UTF8.GetByteCount(rawContent);
            if (byteSize > this._options.MaxFileSizeBytes)
            {
                return $"[File too large: {byteSize} bytes exceeds limit of {this._options.MaxFileSizeBytes} bytes]";
            }

            content = rawContent;
            this._fileCache[cacheKey] = content;
        }

        if (string.IsNullOrEmpty(content))
        {
            return "";
        }

        var lines = content.Split('\n');
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, endLine);

        if (clampedStart > clampedEnd)
        {
            return "";
        }

        return string.Join("\n", lines[(clampedStart - 1)..clampedEnd]);
    }

    /// <summary>
    ///     Fetches the raw string content of a file from ADO. Returns <see langword="null" /> when the file is not found.
    ///     Overridable in tests to inject controlled content without an ADO connection.
    /// </summary>
    /// <param name="path">Repository-relative file path.</param>
    /// <param name="branch">Branch name.</param>
    /// <param name="ct">Cancellation token.</param>
    protected internal virtual async Task<string?> FetchRawFileContentAsync(string path, string branch, CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(ct);
        var item = await gitClient.GetItemAsync(
            this._projectId,
            this._repositoryId,
            path,
            null, // scopePath
            null, // recursionLevel
            null, // includeContentMetadata
            null, // latestProcessedChange
            null, // download
            new GitVersionDescriptor
            {
                VersionType = GitVersionType.Branch,
                Version = branch,
            },
            true, // includeContent
            null, // resolveLfs
            null, // sanitize
            null, // userState
            ct);

        return item?.Content;
    }

    /// <summary>Maps a <see cref="VersionControlChangeType" /> to the domain <see cref="ChangeType" />.</summary>
    internal static ChangeType MapChangeType(VersionControlChangeType adoChangeType) => adoChangeType switch
    {
        VersionControlChangeType.Add => ChangeType.Add,
        VersionControlChangeType.Edit => ChangeType.Edit,
        VersionControlChangeType.Delete => ChangeType.Delete,
        _ => ChangeType.Edit,
    };

    private async Task<GitHttpClient> GetGitClientAsync(CancellationToken ct)
    {
        var credentials = this._clientId.HasValue
            ? await this._credentialRepository.GetByClientIdAsync(this._clientId.Value, ct)
            : null;
        var connection = await this._connectionFactory.GetConnectionAsync(this._organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }
}
