// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
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

    // T037 — /reviewing/jobs/{id}/protocol returns an array (not a single object)
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        var proto = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .First();

        Assert.Equal(JsonValueKind.Null, proto.GetProperty("fileResultId").ValueKind);
    }

    [Fact]
    public async Task GetJobProtocol_IncludesDedupPostingEvents()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 5, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "posting",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(new ProtocolEvent
        {
            Id = Guid.NewGuid(),
            ProtocolId = protocol.Id,
            Kind = ProtocolEventKind.MemoryOperation,
            Name = "dedup_summary",
            OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            InputTextSample = "{\"candidateCount\":2,\"suppressedCount\":1}",
        });
        protocol.Events.Add(new ProtocolEvent
        {
            Id = Guid.NewGuid(),
            ProtocolId = protocol.Id,
            Kind = ProtocolEventKind.MemoryOperation,
            Name = "dedup_degraded_mode",
            OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            InputTextSample = "{\"degradedComponents\":[\"thread_memory_embedding\"]}",
        });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single()
            .GetProperty("events")
            .EnumerateArray()
            .ToList();

        Assert.Contains(events, ev => ev.GetProperty("name").GetString() == "dedup_summary");
        Assert.Contains(events, ev => ev.GetProperty("name").GetString() == "dedup_degraded_mode");
    }

    public sealed class ProtocolApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-protocol-jwt-secret-32chars!";
        private readonly string _dbName = $"TestDb_Protocol_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public string GenerateAdminToken()
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity([
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("global_role", "Admin"),
                ]),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                // InMemory EF Core DB + IJobRepository for test seeding and controller
                services.AddDbContextFactory<MeisterProPRDbContext>(opts =>
                    opts.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IJobRepository, JobRepository>();

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());
            });
        }
    }
}
