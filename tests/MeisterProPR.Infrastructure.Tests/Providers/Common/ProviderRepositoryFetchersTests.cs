// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Providers.Common;

public sealed class ProviderRepositoryFetchersTests
{
    [Fact]
    public async Task InstructionFetcher_WithGitLabConnection_DispatchesToGitLabProvider()
    {
        var clientId = Guid.NewGuid();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitLab,
                    "https://gitlab.example.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    true,
                    "verified",
                    now,
                    null,
                    null,
                    now,
                    now),
            ]);

        var adoFetcher = Substitute.For<IProviderRepositoryInstructionFetcher>();
        adoFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var gitLabFetcher = Substitute.For<IProviderRepositoryInstructionFetcher>();
        gitLabFetcher.Provider.Returns(ScmProvider.GitLab);
        gitLabFetcher.FetchAsync(
                "https://gitlab.example.com",
                "acme",
                "repo-1",
                "main",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(
            [
                RepositoryInstruction.Parse(
                    "instructions-test.md",
                    "\"\"\"\ndescription: Test\nwhen-to-use: Always\n\"\"\"\nBody")!,
            ]);

        var sut = new ProviderRepositoryInstructionFetcher([adoFetcher, gitLabFetcher], connectionRepository);

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo-1",
            "main",
            clientId,
            CancellationToken.None);

        Assert.Single(result);
        await gitLabFetcher.Received(1)
            .FetchAsync("https://gitlab.example.com", "acme", "repo-1", "main", clientId, Arg.Any<CancellationToken>());
        await adoFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExclusionFetcher_WithGitLabConnection_DispatchesToGitLabProvider()
    {
        var clientId = Guid.NewGuid();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitLab,
                    "https://gitlab.example.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    true,
                    "verified",
                    now,
                    null,
                    null,
                    now,
                    now),
            ]);

        var adoFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        adoFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var gitLabFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        gitLabFetcher.Provider.Returns(ScmProvider.GitLab);
        gitLabFetcher.FetchAsync(
                "https://gitlab.example.com",
                "acme",
                "repo-1",
                "main",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.FromPatterns(["openapi.json"]));

        var sut = new ProviderRepositoryExclusionFetcher([adoFetcher, gitLabFetcher], connectionRepository);

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo-1",
            "main",
            clientId,
            CancellationToken.None);

        Assert.True(result.Matches("openapi.json"));
        await gitLabFetcher.Received(1)
            .FetchAsync("https://gitlab.example.com", "acme", "repo-1", "main", clientId, Arg.Any<CancellationToken>());
        await adoFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExclusionFetcher_WithForgejoConnection_DispatchesToForgejoProvider()
    {
        var clientId = Guid.NewGuid();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.Forgejo,
                    "https://codeberg.example.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "Forgejo",
                    true,
                    "verified",
                    now,
                    null,
                    null,
                    now,
                    now),
            ]);

        var adoFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        adoFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var forgejoFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        forgejoFetcher.Provider.Returns(ScmProvider.Forgejo);
        forgejoFetcher.FetchAsync(
                "https://codeberg.example.com",
                "local_admin",
                "local_admin/propr",
                "main",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.FromPatterns(["openapi.json"]));

        var sut = new ProviderRepositoryExclusionFetcher([adoFetcher, forgejoFetcher], connectionRepository);

        var result = await sut.FetchAsync(
            "https://codeberg.example.com",
            "local_admin",
            "local_admin/propr",
            "main",
            clientId,
            CancellationToken.None);

        Assert.True(result.Matches("openapi.json"));
        await forgejoFetcher.Received(1)
            .FetchAsync(
                "https://codeberg.example.com",
                "local_admin",
                "local_admin/propr",
                "main",
                clientId,
                Arg.Any<CancellationToken>());
        await adoFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExclusionFetcher_WithGitHubConnection_DispatchesToGitHubProvider()
    {
        var clientId = Guid.NewGuid();
        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        var now = DateTimeOffset.UtcNow;
        connectionRepository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(
            [
                new ClientScmConnectionDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    "https://github.com",
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    true,
                    "verified",
                    now,
                    null,
                    null,
                    now,
                    now),
            ]);

        var adoFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        adoFetcher.Provider.Returns(ScmProvider.AzureDevOps);

        var gitHubFetcher = Substitute.For<IProviderRepositoryExclusionFetcher>();
        gitHubFetcher.Provider.Returns(ScmProvider.GitHub);
        gitHubFetcher.FetchAsync(
                "https://github.com",
                "acme",
                "123456",
                "main",
                clientId,
                Arg.Any<CancellationToken>())
            .Returns(ReviewExclusionRules.FromPatterns(["openapi.json"]));

        var sut = new ProviderRepositoryExclusionFetcher([adoFetcher, gitHubFetcher], connectionRepository);

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "123456",
            "main",
            clientId,
            CancellationToken.None);

        Assert.True(result.Matches("openapi.json"));
        await gitHubFetcher.Received(1)
            .FetchAsync("https://github.com", "acme", "123456", "main", clientId, Arg.Any<CancellationToken>());
        await adoFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }
}
