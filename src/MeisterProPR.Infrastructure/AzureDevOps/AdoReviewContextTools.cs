// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Azure DevOps-backed implementation of <see cref="IReviewContextTools" />.
///     Each instance is scoped to a single pull request review.
///     File content is cached in memory for the lifetime of the instance to avoid
///     redundant network calls within a single review pass.
/// </summary>
public partial class AdoReviewContextTools : IReviewContextTools
{
    private readonly Guid? _clientId;
    private readonly VssConnectionFactory _connectionFactory;
    private readonly IClientAdoCredentialRepository _credentialRepository;
    private readonly Dictionary<string, string> _fileCache = new(StringComparer.Ordinal);
    private readonly int _iterationId;
    private readonly IReadOnlyList<Guid>? _knowledgeSourceIds;
    private readonly ILogger<AdoReviewContextTools> _logger;
    private readonly AiReviewOptions _options;
    private readonly string _organizationUrl;
    private readonly string _projectId;
    private readonly IProCursorGateway _proCursorGateway;
    private readonly int _pullRequestId;
    private readonly string _repositoryId;
    private readonly string _sourceBranch;

    /// <summary>
    ///     Initializes a new <see cref="AdoReviewContextTools" /> scoped to the given pull request.
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
    /// <param name="knowledgeSourceIds">Optional persisted ProCursor source scope captured for the queued review job.</param>
    /// <param name="sourceBranch">PR source branch enforced for all file-fetch operations.</param>
    /// <param name="logger">Logger for diagnostic messages.</param>
    public AdoReviewContextTools(
        VssConnectionFactory connectionFactory,
        IClientAdoCredentialRepository credentialRepository,
        IProCursorGateway proCursorGateway,
        IOptions<AiReviewOptions> options,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string sourceBranch,
        int pullRequestId,
        int iterationId,
        Guid? clientId,
        IReadOnlyList<Guid>? knowledgeSourceIds = null,
        ILogger<AdoReviewContextTools>? logger = null)
    {
        this._connectionFactory = connectionFactory;
        this._credentialRepository = credentialRepository;
        this._proCursorGateway = proCursorGateway;
        this._options = options.Value;
        this._organizationUrl = organizationUrl;
        this._projectId = projectId;
        this._repositoryId = repositoryId;
        this._sourceBranch = sourceBranch;
        this._pullRequestId = pullRequestId;
        this._iterationId = iterationId;
        this._clientId = clientId;
        this._knowledgeSourceIds = knowledgeSourceIds?.Count > 0 ? knowledgeSourceIds.ToList().AsReadOnly() : null;
        this._logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AdoReviewContextTools>.Instance;
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

        var summaries = (
            from change in changes.ChangeEntries ?? []
            where change.Item?.IsFolder != true
            let path = change.Item?.Path ?? string.Empty
            where !string.IsNullOrEmpty(path)
            select new ChangedFileSummary(path, MapChangeType(change.ChangeType))).ToList();

        return summaries.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetFileTreeAsync(string branch, CancellationToken ct)
    {
        var gitClient = await this.GetGitClientAsync(ct);
        var normalizedBranch = NormalizeBranchName(this._sourceBranch);

        GitBranchStats? branchRef;
        try
        {
            branchRef = await gitClient.GetBranchAsync(
                this._projectId,
                this._repositoryId,
                normalizedBranch,
                cancellationToken: ct);
        }
        catch (VssServiceResponseException)
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
            projectId: this._projectId,
            repositoryId: this._repositoryId,
            sha1: commitSha,
            recursive: true,
            fileName: null,
            userState: null,
            cancellationToken: ct);

        return (tree?.TreeEntries ?? [])
            .Where(e => e.GitObjectType == GitObjectType.Blob)
            .Select(e => e.RelativePath ?? string.Empty)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<string> GetFileContentAsync(string path, string branch, int startLine, int endLine, CancellationToken ct)
    {
        if (BinaryFileDetector.IsBinary(path))
        {
            return $"[Binary file — content not available: {path}]";
        }

        var normalizedBranch = NormalizeBranchName(this._sourceBranch);
        var cacheKey = $"{normalizedBranch}:{path}";

        if (!this._fileCache.TryGetValue(cacheKey, out var content))
        {
            string? rawContent;
            try
            {
                rawContent = await this.FetchRawFileContentAsync(path, normalizedBranch, ct);
            }
            catch
            {
                LogFileNotFound(this._logger, path, normalizedBranch);
                return string.Empty;
            }

            if (rawContent is null)
            {
                LogFileNotFound(this._logger, path, normalizedBranch);
                return string.Empty;
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
            return string.Empty;
        }

        var lines = content.Split('\n');
        var clampedStart = Math.Max(1, startLine);
        var clampedEnd = Math.Min(lines.Length, endLine);

        return clampedStart > clampedEnd ? string.Empty : string.Join("\n", lines[(clampedStart - 1)..clampedEnd]);
    }

    /// <inheritdoc />
    public Task<ProCursorKnowledgeAnswerDto> AskProCursorKnowledgeAsync(string question, CancellationToken ct)
    {
        if (this._clientId is null)
        {
            return Task.FromResult(new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "The current review context does not include a client identifier for ProCursor."));
        }

        return this._proCursorGateway.AskKnowledgeAsync(
            new ProCursorKnowledgeQueryRequest(
                this._clientId.Value,
                question,
                KnowledgeSourceIds: this._knowledgeSourceIds,
                RepositoryContext: new ProCursorRepositoryContextDto(
                    this._organizationUrl,
                    this._projectId,
                    this._repositoryId,
                    NormalizeBranchName(this._sourceBranch))),
            ct);
    }

    /// <inheritdoc />
    public Task<ProCursorSymbolInsightDto> GetProCursorSymbolInfoAsync(
        string symbol,
        string? queryMode,
        int? maxRelations,
        CancellationToken ct)
    {
        if (this._clientId is null)
        {
            return Task.FromResult(new ProCursorSymbolInsightDto(
                "unavailable",
                null,
                false,
                false,
                null,
                [],
                null));
        }

        return this._proCursorGateway.GetSymbolInsightAsync(
            new ProCursorSymbolQueryRequest(
                this._clientId.Value,
                symbol,
                string.IsNullOrWhiteSpace(queryMode) ? "name" : queryMode.Trim(),
                StateMode: "reviewTarget",
                ReviewContext: new ProCursorReviewContextDto(
                    this._repositoryId,
                    NormalizeBranchName(this._sourceBranch),
                    this._pullRequestId,
                    this._iterationId),
                MaxRelations: maxRelations),
            ct);
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
    internal static ChangeType MapChangeType(VersionControlChangeType adoChangeType)
    {
        return adoChangeType switch
        {
            VersionControlChangeType.Add => ChangeType.Add,
            VersionControlChangeType.Edit => ChangeType.Edit,
            VersionControlChangeType.Delete => ChangeType.Delete,
            _ => ChangeType.Edit,
        };
    }

    /// <summary>Strips <c>refs/heads/</c> prefix from a branch name so it matches what ADO's branch-scoped APIs expect.</summary>
    private static string NormalizeBranchName(string branch) =>
        branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch;

    private async Task<GitHttpClient> GetGitClientAsync(CancellationToken ct)
    {
        var credentials = this._clientId.HasValue
            ? await this._credentialRepository.GetByClientIdAsync(this._clientId.Value, ct)
            : null;
        var connection = await this._connectionFactory.GetConnectionAsync(this._organizationUrl, credentials, ct);
        return connection.GetClient<GitHttpClient>();
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "File not found in repository (branch: {Branch}): {Path}")]
    private static partial void LogFileNotFound(ILogger logger, string path, string branch);
}
