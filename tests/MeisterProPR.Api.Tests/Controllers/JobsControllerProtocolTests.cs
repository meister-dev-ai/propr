using System.Net;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>
///     Integration tests verifying the updated protocol API response shape
///     matches the contract defined in contracts/protocol-api.md.
/// </summary>
public sealed class JobsControllerProtocolTests(JobsControllerProtocolTests.ProtocolApiFactory factory)
    : IClassFixture<JobsControllerProtocolTests.ProtocolApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

    // T037 — /jobs/{id}/protocol returns an array (not a single object)
    [Fact]
    public async Task GetJobProtocol_ReturnsArrayNotObject()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
            TotalInputTokens = 1000L,
            TotalOutputTokens = 500L,
            IterationCount = 1,
        };
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/jobs/{job.Id}/protocol");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Response must be a JSON array
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
    }

    // T037 — Each protocol object has label and fileResultId fields
    [Fact]
    public async Task GetJobProtocol_ProtocolObject_HasLabelAndFileResultId()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var fileResultId = Guid.NewGuid();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 2, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Bar.cs",
            FileResultId = fileResultId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/jobs/{job.Id}/protocol");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var proto = body.RootElement.EnumerateArray().First();

        Assert.Equal("src/Bar.cs", proto.GetProperty("label").GetString());
        Assert.Equal(fileResultId.ToString(), proto.GetProperty("fileResultId").GetString());
    }

    // T037 — Multiple protocols (per-file + synthesis) are all returned
    [Fact]
    public async Task GetJobProtocol_MultipleProtocols_AllReturned()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 3, 1);

        foreach (var (label, attempt) in new[] { ("src/Foo.cs", 1), ("src/Bar.cs", 2), ("synthesis", 3) })
        {
            job.Protocols.Add(
                new ReviewJobProtocol
                {
                    Id = Guid.NewGuid(),
                    JobId = job.Id,
                    AttemptNumber = attempt,
                    Label = label,
                    FileResultId = label != "synthesis" ? Guid.NewGuid() : null,
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-attempt),
                    CompletedAt = DateTimeOffset.UtcNow,
                    Outcome = "Completed",
                });
        }

        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/jobs/{job.Id}/protocol");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var protocols = body.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, protocols.Count);

        var labels = protocols.Select(p => p.GetProperty("label").GetString()).ToList();
        Assert.Contains("src/Foo.cs", labels);
        Assert.Contains("src/Bar.cs", labels);
        Assert.Contains("synthesis", labels);
    }

    // T037 — Synthesis protocol has null fileResultId
    [Fact]
    public async Task GetJobProtocol_SynthesisProtocol_HasNullFileResultId()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 4, 1);
        job.Protocols.Add(
            new ReviewJobProtocol
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                AttemptNumber = 1,
                Label = "synthesis",
                FileResultId = null,
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                CompletedAt = DateTimeOffset.UtcNow,
                Outcome = "Completed",
            });
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/jobs/{job.Id}/protocol");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        var proto = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .First();

        Assert.Equal(JsonValueKind.Null, proto.GetProperty("fileResultId").ValueKind);
    }

    public sealed class ProtocolApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
            });
        }
    }
}
