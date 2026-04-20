// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Parsing;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Runtime;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabWebhookIngressTests
{
    [Fact]
    public async Task VerifyAsync_ValidToken_ReturnsTrue()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host, "connection-secret");
        var sut = new GitLabWebhookIngressService(
            connectionRepository,
            new GitLabWebhookTokenVerifier(),
            new GitLabWebhookPayloadParser(new GitLabWebhookEventClassifier()),
            Substitute.For<IClientRegistry>());
        var payload = "{" +
                      "\"object_kind\":\"merge_request\"," +
                      "\"event_type\":\"merge_request\"," +
                      "\"project\":{\"id\":101,\"path_with_namespace\":\"acme/platform/propr\",\"namespace\":\"Acme Platform\"}," +
                      "\"object_attributes\":{\"id\":4201,\"iid\":42,\"action\":\"open\",\"source_branch\":\"feature/providers\",\"target_branch\":\"main\",\"last_commit\":{\"id\":\"head-sha\"}}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitlab-Token"] = "webhook-secret",
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "delivery-1",
        };

        var result = await sut.VerifyAsync(clientId, host, headers, payload, "webhook-secret");

        Assert.True(result);
    }

    [Fact]
    public async Task ParseAsync_ReRequestedReviewer_ReturnsNormalizedEnvelope()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host, "webhook-secret");
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true));
        var sut = new GitLabWebhookIngressService(
            connectionRepository,
            new GitLabWebhookTokenVerifier(),
            new GitLabWebhookPayloadParser(new GitLabWebhookEventClassifier()),
            clientRegistry);
        var payload = "{" +
                      "\"object_kind\":\"merge_request\"," +
                      "\"event_type\":\"merge_request\"," +
                      "\"user\":{\"id\":7,\"name\":\"Octocat\",\"username\":\"octocat\"}," +
                      "\"project\":{\"id\":101,\"path_with_namespace\":\"acme/platform/propr\",\"namespace\":\"Acme Platform\"}," +
                      "\"object_attributes\":{\"id\":4201,\"iid\":42,\"action\":\"update\",\"oldrev\":\"base-sha\",\"source_branch\":\"feature/providers\",\"target_branch\":\"main\",\"last_commit\":{\"id\":\"head-sha\"}}," +
                      "\"changes\":{\"reviewers\":[[{\"id\":99,\"username\":\"meister-review-bot\",\"re_requested\":false}],[{\"id\":99,\"username\":\"meister-review-bot\",\"re_requested\":true}]]}," +
                      "\"reviewers\":[{\"id\":99,\"username\":\"meister-review-bot\",\"state\":\"unreviewed\",\"re_requested\":true}]" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitlab-Token"] = "webhook-secret",
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "delivery-1",
        };

        var envelope = await sut.ParseAsync(clientId, host, headers, payload);

        Assert.Equal("delivery-1", envelope.DeliveryId);
        Assert.Equal("reviewer_assignment", envelope.DeliveryKind);
        Assert.Equal("Merge Request Hook", envelope.EventName);
        Assert.Equal(ScmProvider.GitLab, envelope.Host.Provider);
        Assert.Equal("101", envelope.Repository!.ExternalRepositoryId);
        Assert.Equal("acme/platform/propr", envelope.Repository.ProjectPath);
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
    public async Task ParseAsync_MergeAction_ReturnsMergedDeliveryKind()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host, "webhook-secret");
        var sut = new GitLabWebhookIngressService(
            connectionRepository,
            new GitLabWebhookTokenVerifier(),
            new GitLabWebhookPayloadParser(new GitLabWebhookEventClassifier()),
            Substitute.For<IClientRegistry>());
        var payload = "{" +
                      "\"object_kind\":\"merge_request\"," +
                      "\"event_type\":\"merge_request\"," +
                      "\"user\":{\"id\":7,\"name\":\"Octocat\",\"username\":\"octocat\"}," +
                      "\"project\":{\"id\":101,\"path_with_namespace\":\"acme/platform/propr\",\"namespace\":\"Acme Platform\"}," +
                      "\"object_attributes\":{\"id\":4201,\"iid\":42,\"action\":\"merge\",\"source_branch\":\"feature/providers\",\"target_branch\":\"main\",\"last_commit\":{\"id\":\"head-sha\"}}" +
                      "}";
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Gitlab-Token"] = "webhook-secret",
            ["X-Gitlab-Event"] = "Merge Request Hook",
            ["X-Gitlab-Event-UUID"] = "delivery-2",
        };

        var envelope = await sut.ParseAsync(clientId, host, headers, payload);

        Assert.Equal("pull_request.merged", envelope.DeliveryKind);
    }
}
