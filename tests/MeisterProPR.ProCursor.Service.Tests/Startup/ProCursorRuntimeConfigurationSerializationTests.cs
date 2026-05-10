// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Domain.Enums;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.ProCursor.Service.Tests.Startup;

public sealed class ProCursorRuntimeConfigurationSerializationTests
{
    [Fact]
    public async Task ListEnabledAsync_ParsesCamelCaseEnumPayloads()
    {
        var payload = """
                      [{
                        "projectionVersion": "v1",
                        "fetchedAt": "2026-05-09T13:09:32.5015207+00:00",
                        "source": {
                          "id": "60ec4070-6b81-4738-9fff-c34ce8fe503d",
                          "clientId": "7e2456e5-f799-4aea-b749-9bf543308780",
                          "displayName": "ProPR Repo",
                          "sourceKind": "repository",
                          "providerScopePath": "https://dev.azure.com/meister-dev",
                          "providerProjectKey": "5cda05b9-bbfa-4c44-88e9-16aa900515d2",
                          "repositoryId": "c39fd3f3-e84b-4d01-84df-57964de91bc8",
                          "defaultBranch": "main",
                          "rootPath": null,
                          "isEnabled": true,
                          "symbolMode": "auto",
                          "latestSnapshot": null,
                          "trackedBranches": [{
                            "id": "f4b8af6e-5c27-440c-8a0c-06bcee49760e",
                            "branchName": "main",
                            "refreshTriggerMode": "branchUpdate",
                            "miniIndexEnabled": true,
                            "lastSeenCommitSha": "d78b95702e2d27595ab4f08d64d49af0272f7031",
                            "lastIndexedCommitSha": "d78b95702e2d27595ab4f08d64d49af0272f7031",
                            "isEnabled": true,
                            "freshnessStatus": "unknown"
                          }],
                          "organizationScopeId": null,
                          "canonicalSourceRef": {
                            "provider": "azureDevOps",
                            "value": "c39fd3f3-e84b-4d01-84df-57964de91bc8"
                          },
                          "sourceDisplayName": "meister-propr"
                        }
                      }]
                      """;

        using var httpClient = new HttpClient(new StubHandler(payload));
        var logger = NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<ProPrRuntimeConfigurationBroker>>();
        var broker = new ProPrRuntimeConfigurationBroker(
            httpClient,
            logger,
            Microsoft.Extensions.Options.Options.Create(new ProCursorHostOptions { ProPrBaseUrl = "http://localhost" }));

        var projections = await broker.ListEnabledAsync();

        var projection = Assert.Single(projections);
        Assert.Equal(ProCursorSourceKind.Repository, projection.Source.SourceKind);
        var branch = Assert.Single(projection.Source.TrackedBranches);
        Assert.Equal(ProCursorRefreshTriggerMode.BranchUpdate, branch.RefreshTriggerMode);
    }

    [Fact]
    public async Task EmbeddingBroker_UsesConfiguredAbsoluteBrokerUri_WhenHttpClientBaseAddressIsMissing()
    {
        Uri? requestUri = null;
        using var httpClient = new HttpClient(new StubHandler(
            """
            {
              "aiConnectionId": "3136e333-0110-4ac8-bff0-b01100d24d13",
              "deploymentName": "text-embedding-3-large",
              "tokenizerName": "cl100k_base",
              "maxInputTokens": 8191,
              "embeddingDimensions": 3072,
              "inputCostPer1MUsd": 0.13,
              "outputCostPer1MUsd": 0.0
            }
            """,
            request => requestUri = request.RequestUri));
        var logger = NSubstitute.Substitute.For<Microsoft.Extensions.Logging.ILogger<ProPrEmbeddingBroker>>();
        var broker = new ProPrEmbeddingBroker(
            httpClient,
            logger,
            Microsoft.Extensions.Options.Options.Create(new ProCursorHostOptions { ProPrBaseUrl = "http://propr.internal:8080" }));

        var deployment = await broker.GetDeploymentAsync(Guid.NewGuid(), 3072);

        Assert.Equal("http://propr.internal:8080/internal/propr/procursor/broker/embeddings/deployment", requestUri?.AbsoluteUri);
        Assert.Equal("text-embedding-3-large", deployment.DeploymentName);
    }

    private sealed class StubHandler(string payload, Action<HttpRequestMessage>? onRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
