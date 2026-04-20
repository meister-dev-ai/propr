// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Tests.Features.Reviewing.Intake;

public sealed class ProviderReviewJobIntakeIntegrationTests(ReviewJobIntakeIntegrationTests.IntakeApiFactory factory)
    : IClassFixture<ReviewJobIntakeIntegrationTests.IntakeApiFactory>
{
    [Fact]
    public async Task SubmitReview_GitHubRequest_ReturnsAcceptedAndPersistsProviderNeutralContext()
    {
        await factory.ClearJobsAsync();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/clients/{factory.ClientId}/reviewing/jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.ClientAdministratorUserId));
        request.Content = JsonContent.Create(
            new
            {
                provider = "github",
                hostBaseUrl = "https://github.com",
                repository = new
                {
                    externalRepositoryId = "repo-gh-1",
                    ownerOrNamespace = "acme",
                    projectPath = "acme/propr",
                },
                codeReview = new
                {
                    platform = "pullRequest",
                    externalReviewId = "42",
                    number = 42,
                },
                reviewRevision = new
                {
                    headSha = "head-sha",
                    baseSha = "base-sha",
                    startSha = "start-sha",
                    providerRevisionId = "11",
                    patchIdentity = "patch-1",
                },
                requestedReviewerIdentity = new
                {
                    externalUserId = "user-1",
                    login = "meister-dev-bot",
                    displayName = "Meister Dev Bot",
                    isBot = true,
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("github", body.GetProperty("provider").GetString());
        var jobId = body.GetProperty("jobId").GetGuid();

        var persisted = await factory.GetJobAsync(jobId);
        Assert.NotNull(persisted);
        Assert.Equal(ScmProvider.GitHub, persisted!.Provider);
        Assert.Equal("https://github.com", persisted.HostBaseUrl);
        Assert.Equal("acme", persisted.RepositoryOwnerOrNamespace);
        Assert.Equal("acme/propr", persisted.RepositoryProjectPath);
        Assert.Equal("42", persisted.ExternalCodeReviewId);
        Assert.Equal("head-sha", persisted.RevisionHeadSha);
        Assert.Equal("base-sha", persisted.RevisionBaseSha);
        Assert.Equal("start-sha", persisted.RevisionStartSha);
        Assert.Equal("11", persisted.ProviderRevisionId);
        Assert.Equal("patch-1", persisted.ReviewPatchIdentity);
    }
}
