// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoReviewWorkspaceRemoteResolver(
    IClientScmConnectionRepository connectionRepository,
    VssConnectionFactory connectionFactory) : IProviderReviewWorkspaceRemoteResolver
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<ReviewWorkspaceRemoteRef> ResolveAsync(ReviewRepositoryWorkspaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var host = new ProviderHostRef(ScmProvider.AzureDevOps, request.ProviderScopePath);
        var connection = await connectionRepository.GetOperationalConnectionAsync(request.ClientId, host, ct)
                         ?? throw new InvalidOperationException("No active Azure DevOps connection is configured for the supplied host.");
        var projectName = request.Repository.ProjectPath;
        var repositoryName = request.Repository.RepositoryName;
        var remoteUrl = $"{request.ProviderScopePath.TrimEnd('/')}/{Uri.EscapeDataString(projectName)}/_git/{Uri.EscapeDataString(repositoryName)}";

        var credentials = AdoProviderAdapterHelpers.ToAdoCredentials(connection);
        var authorizationHeader = await connectionFactory.GetHttpAuthorizationHeaderAsync(
            request.ProviderScopePath,
            credentials,
            ct);

        return new ReviewWorkspaceRemoteRef(
            ScmProvider.AzureDevOps,
            remoteUrl,
            BuildFetchRefSpecs(request.SourceBranch, request.TargetBranch),
            $"ado:{host.HostBaseUrl}:{projectName}:{repositoryName}",
            $"ado:{connection.Id:D}:{connection.AuthenticationKind}",
            authorizationHeader is not null,
            projectName,
            authorizationHeader);
    }

    private static IReadOnlyList<string> BuildFetchRefSpecs(string sourceBranch, string targetBranch)
    {
        var refSpecs = new List<string>();
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

        var normalized = AdoProviderAdapterHelpers.StripRefsHeads(branch) ?? branch.Trim();
        refSpecs.Add($"+refs/heads/{normalized}:refs/remotes/origin/{normalized}");
    }
}
