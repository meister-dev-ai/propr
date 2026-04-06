// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers;

namespace MeisterProPR.Api.Tests.Features.Crawling;

public sealed class CrawlingConfigurationModuleIntegrationTests(AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory factory)
    : IClassFixture<AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory>
{
    [Fact]
    public async Task CreateGuidedCrawlConfiguration_ReturnsGuidedMetadata()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(new
        {
            clientId = factory.TestClientId,
            organizationScopeId = factory.GuidedOrganizationScopeId,
            projectId = "GuidedProject",
            crawlIntervalSeconds = 60,
            repoFilters = new[]
            {
                new
                {
                    displayName = "Repository One",
                    canonicalSourceRef = new
                    {
                        provider = "azureDevOps",
                        value = "repo-1",
                    },
                    targetBranchPatterns = new[] { "main" },
                },
            },
            proCursorSourceScopeMode = "selectedSources",
            proCursorSourceIds = new[] { factory.GuidedProCursorSourceId },
        });

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.GuidedOrganizationScopeId, body.GetProperty("organizationScopeId").GetGuid());
        Assert.Equal(factory.GuidedProCursorSourceId, body.GetProperty("proCursorSourceIds")[0].GetGuid());
    }
}

public sealed class CrawlingDiscoveryModuleIntegrationTests(AdoDiscoveryControllerTests.AdoDiscoveryApiFactory factory)
    : IClassFixture<AdoDiscoveryControllerTests.AdoDiscoveryApiFactory>
{
    [Fact]
    public async Task GetCrawlFilters_ClientUserForAssignedClient_ReturnsScopedDiscoveryOptions()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/crawl-filters?organizationScopeId={factory.OrganizationScopeId}&projectId=project-1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(body.EnumerateArray());
        Assert.Equal("Repository One", body[0].GetProperty("displayName").GetString());
    }
}

public sealed class CrawlingIdentityResolutionModuleIntegrationTests(IdentitiesControllerTests.IdentitiesApiFactory factory)
    : IClassFixture<IdentitiesControllerTests.IdentitiesApiFactory>
{
    [Fact]
    public async Task ResolveIdentity_ClientAdministrator_ReturnsResolvedIdentity()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/identities/resolve?orgUrl=https://dev.azure.com/org&displayName=Reviewer");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateClientAdministratorToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("Reviewer", payload, StringComparison.Ordinal);
    }
}
