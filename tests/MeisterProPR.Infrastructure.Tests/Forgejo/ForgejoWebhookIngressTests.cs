// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Forgejo;

public sealed class ForgejoWebhookIngressTests
{
    [Fact]
    public async Task VerifyAsync_ValidSignature_ReturnsTrue()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host, "connection-secret");
        var sut = new ForgejoWebhookIngressService(
            connectionRepository,
            new ForgejoWebhookSignatureVerifier(),
            new ForgejoWebhookPayloadParser(new ForgejoWebhookEventClassifier()),
            Substitute.For<IClientRegistry>());
        var payload = "{" +
                      "\"action\":\"opened\"," +
                      "\"repository\":{\"id\":101,\"full_name\":\"acme/propr\",\"owner\":{\"login\":\"acme\"}}," +
                      "\"pull_request\":{\"id\":4201,\"number\":42,\"state\":\"open\",\"merged\":false,\"head\":{\"ref\":\"feature/providers\",\"sha\":\"head-sha\"},\"base\":{\"ref\":\"main\",\"sha\":\"base-sha\"}}," +
                      "\"sender\":{\"id\":7,\"login\":\"octocat\"}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitea-Signature"] = ComputeSignature("webhook-secret", payload),
            ["X-Gitea-Event"] = "pull_request",
            ["X-Gitea-Delivery"] = "delivery-1",
        };

        var result = await sut.VerifyAsync(clientId, host, headers, payload, "webhook-secret");

        Assert.True(result);
    }

    [Fact]
    public async Task ParseAsync_ReviewRequestedForConfiguredReviewer_ReturnsNormalizedEnvelope()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host, "webhook-secret");
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var sut = new ForgejoWebhookIngressService(
            connectionRepository,
            new ForgejoWebhookSignatureVerifier(),
            new ForgejoWebhookPayloadParser(new ForgejoWebhookEventClassifier()),
            clientRegistry);
        var payload = "{" +
                      "\"action\":\"review_requested\"," +
                      "\"repository\":{\"id\":101,\"full_name\":\"acme/propr\",\"owner\":{\"login\":\"acme\"}}," +
                      "\"pull_request\":{\"id\":4201,\"number\":42,\"state\":\"open\",\"merged\":false,\"head\":{\"ref\":\"feature/providers\",\"sha\":\"head-sha\"},\"base\":{\"ref\":\"main\",\"sha\":\"base-sha\"}}," +
                      "\"requested_reviewer\":{\"id\":99,\"login\":\"meister-review-bot\"}," +
                      "\"sender\":{\"id\":7,\"login\":\"octocat\"}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitea-Signature"] = ComputeSignature("webhook-secret", payload),
            ["X-Gitea-Event"] = "pull_request",
            ["X-Gitea-Delivery"] = "delivery-1",
        };

        var envelope = await sut.ParseAsync(clientId, host, headers, payload);

        Assert.Equal("delivery-1", envelope.DeliveryId);
        Assert.Equal("reviewer_assignment", envelope.DeliveryKind);
        Assert.Equal("pull_request", envelope.EventName);
        Assert.Equal(ScmProvider.Forgejo, envelope.Host.Provider);
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

    [Fact]
    public async Task ParseAsync_ClosedMergedPullRequest_ReturnsMergedDeliveryKind()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host, "webhook-secret");
        var sut = new ForgejoWebhookIngressService(
            connectionRepository,
            new ForgejoWebhookSignatureVerifier(),
            new ForgejoWebhookPayloadParser(new ForgejoWebhookEventClassifier()),
            Substitute.For<IClientRegistry>());
        var payload = "{" +
                      "\"action\":\"closed\"," +
                      "\"repository\":{\"id\":101,\"full_name\":\"acme/propr\",\"owner\":{\"login\":\"acme\"}}," +
                      "\"pull_request\":{\"id\":4201,\"number\":42,\"state\":\"closed\",\"merged\":true,\"head\":{\"ref\":\"feature/providers\",\"sha\":\"head-sha\"},\"base\":{\"ref\":\"main\",\"sha\":\"base-sha\"}}," +
                      "\"sender\":{\"id\":7,\"login\":\"octocat\"}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitea-Signature"] = ComputeSignature("webhook-secret", payload),
            ["X-Gitea-Event"] = "pull_request",
            ["X-Gitea-Delivery"] = "delivery-2",
        };

        var envelope = await sut.ParseAsync(clientId, host, headers, payload);

        Assert.Equal("pull_request.merged", envelope.DeliveryKind);
    }

    private static string ComputeSignature(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
