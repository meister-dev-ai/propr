// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers.ProCursor;

namespace MeisterProPR.Api.Tests.Features.ProCursor;

public sealed class ProCursorModuleIntegrationTests(ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
    : IClassFixture<ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync() => factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreateSource_ThenListSources_ReturnsPersistedFeatureOwnedSource()
    {
        var http = factory.CreateClient();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, $"/admin/clients/{factory.ClientId}/procursor/sources");
        createRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientAdministratorToken());
        createRequest.Content = JsonContent.Create(new
        {
            displayName = "Knowledge Repo",
            sourceKind = "repository",
            organizationUrl = "https://dev.azure.com/test-org",
            projectId = "project-a",
            repositoryId = "repo-a",
            defaultBranch = "main",
            symbolMode = "auto",
            trackedBranches = new[]
            {
                new { branchName = "main", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
            },
        });

        var createResponse = await http.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listRequest = new HttpRequestMessage(HttpMethod.Get, $"/admin/clients/{factory.ClientId}/procursor/sources");
        listRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientAdministratorToken());

        var listResponse = await http.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var items = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()).RootElement.EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal("Knowledge Repo", items[0].GetProperty("displayName").GetString());
    }
}
