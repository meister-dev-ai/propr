// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests.Features.Reviewing;

public sealed class ReviewingModuleIntegrationTests(JobsControllerProtocolTests.ProtocolApiFactory factory)
    : IClassFixture<JobsControllerProtocolTests.ProtocolApiFactory>
{
    [Fact]
    public async Task GetJobProtocol_WhenReviewingDiagnosticsExist_ReturnsProtocolArray()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 91, 1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                Label = "posting",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = "Completed",
            });
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal("posting", body.RootElement.EnumerateArray().Single().GetProperty("label").GetString());
    }

    [Fact]
    public async Task GetJobProtocol_WhenNoReviewingDiagnosticsExist_ReturnsNotFound()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{Guid.NewGuid()}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
