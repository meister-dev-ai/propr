// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubWebhookIngressTests
{
    [Fact]
    public async Task VerifyAsync_ValidSignature_ReturnsTrue()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host, "connection-secret");
        var sut = new GitHubWebhookIngressService(
            connectionRepository,
            new GitHubWebhookSignatureVerifier(),
            new GitHubWebhookPayloadParser(new GitHubWebhookEventClassifier()),
            Substitute.For<IClientRegistry>());
        var payload = "{" +
                      "\"action\":\"opened\"," +
                      "\"repository\":{\"id\":101,\"full_name\":\"acme/propr\",\"owner\":{\"login\":\"acme\"}}," +
                      "\"pull_request\":{\"id\":42,\"number\":42,\"state\":\"open\",\"head\":{\"ref\":\"feature/providers\",\"sha\":\"head-sha\"},\"base\":{\"ref\":\"main\",\"sha\":\"base-sha\"}}," +
                      "\"sender\":{\"id\":7,\"login\":\"octocat\",\"type\":\"User\"}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = ComputeSignature("webhook-secret", payload),
            ["X-GitHub-Event"] = "pull_request",
            ["X-GitHub-Delivery"] = "delivery-1",
        };

        var result = await sut.VerifyAsync(clientId, host, headers, payload, "webhook-secret");

        Assert.True(result);
    }

    [Fact]
    public async Task ParseAsync_ReviewRequestedForConfiguredReviewer_ReturnsNormalizedEnvelope()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = CreateConnectionRepository(clientId, host, "webhook-secret");
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var sut = new GitHubWebhookIngressService(
            connectionRepository,
            new GitHubWebhookSignatureVerifier(),
            new GitHubWebhookPayloadParser(new GitHubWebhookEventClassifier()),
            clientRegistry);
        var payload = "{" +
                      "\"action\":\"review_requested\"," +
                      "\"repository\":{\"id\":101,\"full_name\":\"acme/propr\",\"owner\":{\"login\":\"acme\"}}," +
                      "\"pull_request\":{\"id\":4201,\"number\":42,\"state\":\"open\",\"head\":{\"ref\":\"feature/providers\",\"sha\":\"head-sha\"},\"base\":{\"ref\":\"main\",\"sha\":\"base-sha\"}}," +
                      "\"requested_reviewer\":{\"id\":99,\"login\":\"meister-review-bot\",\"type\":\"Bot\"}," +
                      "\"sender\":{\"id\":7,\"login\":\"octocat\",\"type\":\"User\"}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Hub-Signature-256"] = ComputeSignature("webhook-secret", payload),
            ["X-GitHub-Event"] = "pull_request",
            ["X-GitHub-Delivery"] = "delivery-1",
        };

        var envelope = await sut.ParseAsync(clientId, host, headers, payload);

        Assert.Equal("delivery-1", envelope.DeliveryId);
        Assert.Equal("reviewer_assignment", envelope.DeliveryKind);
        Assert.Equal("pull_request", envelope.EventName);
        Assert.Equal(ScmProvider.GitHub, envelope.Host.Provider);
        Assert.Equal("101", envelope.Repository!.ExternalRepositoryId);
        Assert.Equal("acme/propr", envelope.Repository.ProjectPath);
        Assert.Equal("4201", envelope.Review!.ExternalReviewId);
        Assert.Equal(42, envelope.Review.Number);
        Assert.Equal("head-sha", envelope.Revision!.HeadSha);
        Assert.Equal("base-sha", envelope.Revision.BaseSha);
        Assert.Equal("feature/providers", envelope.SourceBranch);
        Assert.Equal("main", envelope.TargetBranch);
        Assert.NotNull(envelope.Actor);
        Assert.Equal("octocat", envelope.Actor!.Login);
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(
        Guid clientId,
        ProviderHostRef host,
        string secret)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    secret,
                    true));
        return repository;
    }

    private static string ComputeSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}
