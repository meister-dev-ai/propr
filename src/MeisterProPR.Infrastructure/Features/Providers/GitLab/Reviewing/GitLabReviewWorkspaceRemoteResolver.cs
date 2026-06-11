// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

internal sealed class GitLabReviewWorkspaceRemoteResolver(GitLabConnectionVerifier connectionVerifier) : IProviderReviewWorkspaceRemoteResolver
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await connectionVerifier.VerifyAsync(request.ClientId, request.Repository.Host, ct);
        var repositoryPath = BuildRepositoryPath(request.Repository);
        var remoteUrl = $"{request.Repository.Host.HostBaseUrl.TrimEnd('/')}/{repositoryPath}.git";

        return new ReviewWorkspaceRemoteRef(
            ScmProvider.GitLab,
            remoteUrl,
            BuildFetchRefSpecs(request.PullRequestNumber, request.SourceBranch, request.TargetBranch),
            $"gitlab:{request.Repository.Host.HostBaseUrl}:{repositoryPath}",
            $"gitlab:{context.Connection.Id:D}:{context.Connection.AuthenticationKind}",
            true,
            request.Repository.OwnerOrNamespace,
            $"PRIVATE-TOKEN: {context.Connection.Secret}");
    }

    private static IReadOnlyList<string> BuildFetchRefSpecs(int pullRequestNumber, string sourceBranch, string targetBranch)
    {
        var refSpecs = new List<string>
        {
            $"+refs/merge-requests/{pullRequestNumber}/head:refs/remotes/review/mr/{pullRequestNumber}/head",
        };

        AddBranchRef(refSpecs, sourceBranch);
        AddBranchRef(refSpecs, targetBranch);
        return refSpecs.AsReadOnly();
    }

    private static void AddBranchRef(ICollection<string> refSpecs, string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
        {
            return;
        }

        var normalized = branch.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? branch["refs/heads/".Length..]
            : branch.Trim();
        refSpecs.Add($"+refs/heads/{normalized}:refs/remotes/origin/{normalized}");
    }

    private static string BuildRepositoryPath(RepositoryRef repository)
    {
        if (!string.IsNullOrWhiteSpace(repository.ProjectPath) && repository.ProjectPath.Contains('/'))
        {
            return repository.ProjectPath.Trim();
        }

        return $"{repository.OwnerOrNamespace}/{repository.ProjectPath}";
    }
}
