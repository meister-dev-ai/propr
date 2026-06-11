// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Providers.AzureDevOps.Reviewing;

public sealed class AdoReviewWorkspaceRemoteResolverTests
{
    [Fact]
    public async Task ResolveAsync_WindowsUserAccountConnection_DisablesLocalFetch()
    {
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://ado.example.com");
        var clientId = Guid.NewGuid();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.AzureDevOps,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.WindowsUserAccount,
                    null,
                    null,
                    "ADO Server",
                    "secret",
                    true,
                    UserName: "DOMAIN\\user"));

        var sut = new AdoReviewWorkspaceRemoteResolver(connectionRepository, new VssConnectionFactory(Substitute.For<TokenCredential>()));
        var result = await sut.ResolveAsync(CreateRequest(host, clientId), CancellationToken.None);

        Assert.False(result.SupportsLocalFetch);
    }

    private static ReviewRepositoryWorkspaceRequest CreateRequest(ProviderHostRef host, Guid clientId)
    {
        var repository = new RepositoryRef(host, "repo-id", "project", "project");
        return new ReviewRepositoryWorkspaceRequest(
            Guid.NewGuid(),
            clientId,
            ScmProvider.AzureDevOps,
            host.HostBaseUrl,
            repository,
            42,
            new ReviewRevision("head", "base", null, null, null),
            "refs/heads/feature/test",
            "refs/heads/main");
    }
}
