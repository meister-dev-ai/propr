// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     An iteration is an Azure DevOps concept with one implementation, which every provider-neutral caller
///     reaches. What it must not do is carry the platform's own identity to somebody else's host.
/// </summary>
public sealed class AdoPullRequestIterationResolverTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    [Theory]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example.com")]
    [InlineData(ScmProvider.GitHub, "https://github.com")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example")]
    public async Task GetLatestIterationIdAsync_ForAnotherProvidersHost_RefusesWithoutReachingIt(
        ScmProvider provider,
        string hostBaseUrl)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetByClientIdAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns([Connection(provider, hostBaseUrl)]);

        // No VssConnectionFactory is supplied: reaching the host at all would fault on the null, so the test
        // fails loudly if the guard ever stops holding.
        var sut = new AdoPullRequestIterationResolver(
            null!,
            connections,
            NullLogger<AdoPullRequestIterationResolver>.Instance);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetLatestIterationIdAsync(ClientId, hostBaseUrl, "acme", "101", 42));

        Assert.Contains("Azure DevOps concept", failure.Message, StringComparison.Ordinal);

        // The credential lookup is what precedes opening a connection, so never asking for one is the proof
        // that no token was acquired for this host.
        await connections.DidNotReceiveWithAnyArgs()
            .GetOperationalConnectionAsync(default, default!, default);
    }

    private static ClientScmConnectionDto Connection(ScmProvider provider, string hostBaseUrl)
    {
        return new ClientScmConnectionDto(
            Guid.NewGuid(),
            ClientId,
            provider,
            hostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            provider.ToString(),
            true,
            "verified",
            DateTimeOffset.UtcNow,
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }
}
