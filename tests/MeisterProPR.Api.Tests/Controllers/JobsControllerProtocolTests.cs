// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());

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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.MemoryOperation,
                Name = "dedup_summary",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                InputTextSample = "{\"candidateCount\":2,\"suppressedCount\":1}",
            });
        protocol.Events.Add(
            new ProtocolEvent
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
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
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

    [Fact]
    public async Task GetJobProtocol_BoundsLargeFreeTextEventBodies()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 6, 1);
        var inputText = new string('A', 60_000) + "-input-tail";
        var outputText = new string('B', 60_000) + "-output-tail";

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Long.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = "ai_call_iter_1",
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = inputText,
                OutputSummary = outputText,
            });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ev = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single()
            .GetProperty("events")
            .EnumerateArray()
            .Single();

        // The per-pass endpoint now bounds large FREE-TEXT event bodies so a heavy protocol
        // no longer ships hundreds of MB (full-text trace search is server-side, not the
        // browser's job, so the wire copy no longer needs the whole body). Structured JSON
        // bodies are left whole — covered by EfReviewDiagnosticsReaderTests.
        var input = ev.GetProperty("inputTextSample").GetString();
        var output = ev.GetProperty("outputSummary").GetString();
        Assert.NotNull(input);
        Assert.NotNull(output);
        Assert.True(input!.Length < inputText.Length);
        Assert.True(output!.Length < outputText.Length);
        Assert.Contains("truncated", input);
        Assert.Contains("truncated", output);
        Assert.DoesNotContain("-input-tail", input);
        Assert.DoesNotContain("-output-tail", output);
    }

    [Fact]
    public async Task GetJobProtocol_WhenIncludeEventsIsFalse_OmitsEventBodiesButKeepsEventRows()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 61, 1);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Overview.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = "ai_call_iter_1",
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = "full input",
                SystemPrompt = "full system",
                OutputSummary = "full output",
            });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol?includeEvents=false");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var ev = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single()
            .GetProperty("events")
            .EnumerateArray()
            .Single();

        Assert.Equal("ai_call_iter_1", ev.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("inputTextSample").ValueKind);
        Assert.Equal(JsonValueKind.Null, ev.GetProperty("systemPrompt").ValueKind);
        Assert.Equal(
            "Event payload omitted from the overview to keep large protocol traces responsive. Select this pass to load the full captured body.",
            ev.GetProperty("outputSummary").GetString());
    }

    [Fact]
    public async Task GetJobProtocol_IncludesCacheEvidenceAndFinalizationDiagnostics()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 63, 1);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Cache.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
            TotalInputTokens = 2048,
            TotalOutputTokens = 300,
            TotalCachedInputTokens = 1024,
            CacheObservability = CacheObservabilityStatus.Observable,
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = "ai_call_iter_1",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-10),
                InputTokens = 2048,
                OutputTokens = 300,
                CachedInputTokens = 1024,
                CacheStatus = CacheCallStatus.Hit,
                PrefixEligibility = PrefixEligibilityStatus.Eligible,
                FinalizationAttemptKind = "ForcedFinal",
                FinalizationReason = "iteration_limit_reached",
                FinalizationOutcome = "ProducedFinalText",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.ToolCall,
                Name = "get_file_content",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                ToolEvidenceAction = "Bounded",
                ToolEvidenceSourceToolName = "get_file_content",
                ToolEvidenceOriginalPayloadTokens = 2000,
                ToolEvidenceBoundedPayloadTokens = 256,
                ToolEvidenceRefreshable = true,
            });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();
        Assert.Equal(1024, protocolJson.GetProperty("totalCachedInputTokens").GetInt64());
        Assert.Equal("observable", protocolJson.GetProperty("cacheObservability").GetString());

        var events = protocolJson.GetProperty("events").EnumerateArray().ToList();
        var aiCall = events.Single(e => e.GetProperty("name").GetString() == "ai_call_iter_1");
        Assert.Equal(1024, aiCall.GetProperty("cachedInputTokens").GetInt64());
        Assert.Equal("hit", aiCall.GetProperty("cacheStatus").GetString());
        Assert.Equal("eligible", aiCall.GetProperty("prefixEligibility").GetString());
        Assert.Equal("ForcedFinal", aiCall.GetProperty("finalizationAttemptKind").GetString());

        var toolEvidence = events.Single(e => e.GetProperty("name").GetString() == "get_file_content")
            .GetProperty("toolEvidence");
        Assert.Equal("Bounded", toolEvidence.GetProperty("action").GetString());
        Assert.Equal(2000, toolEvidence.GetProperty("originalPayloadTokens").GetInt32());
        Assert.True(toolEvidence.GetProperty("refreshable").GetBoolean());
    }

    [Fact]
    public async Task GetJobProtocol_IncludesToolTimingAndPhaseDiagnostics()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 64, 1);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Timing.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.ToolCall,
                Name = "search_source_repo",
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                StartedAt = DateTimeOffset.UtcNow.AddSeconds(-7),
                CompletedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                DurationMs = 2100,
                WaitDurationMs = 400,
                ActiveDurationMs = 1700,
                TimingAvailability = ProtocolEventTimingAvailabilities.Captured,
                ToolOutcome = ProtocolEventToolOutcomes.Succeeded,
                PhaseTimings =
                [
                    new ProtocolEventPhaseTiming(
                        ProtocolEventToolPhaseNames.ScmFileTreeFetch,
                        "SCM file tree fetch",
                        1,
                        null,
                        DateTimeOffset.UtcNow.AddSeconds(-7),
                        DateTimeOffset.UtcNow.AddSeconds(-6),
                        900,
                        ProtocolEventTimingAvailabilities.Captured,
                        ProtocolEventToolOutcomes.Succeeded,
                        "candidate_paths=42"),
                    new ProtocolEventPhaseTiming(
                        ProtocolEventToolPhaseNames.RepositorySearch,
                        "Repository search",
                        2,
                        null,
                        DateTimeOffset.UtcNow.AddSeconds(-6),
                        DateTimeOffset.UtcNow.AddSeconds(-5),
                        1200,
                        ProtocolEventTimingAvailabilities.Captured,
                        ProtocolEventToolOutcomes.Succeeded,
                        "matches=2"),
                ],
            });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var toolEvent = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single()
            .GetProperty("events")
            .EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "search_source_repo");

        Assert.Equal(2100, toolEvent.GetProperty("durationMs").GetInt64());
        Assert.Equal(400, toolEvent.GetProperty("waitDurationMs").GetInt64());
        Assert.Equal(1700, toolEvent.GetProperty("activeDurationMs").GetInt64());
        Assert.Equal("captured", toolEvent.GetProperty("timingAvailability").GetString());
        Assert.Equal("succeeded", toolEvent.GetProperty("toolOutcome").GetString());

        var phases = toolEvent.GetProperty("phaseTimings").EnumerateArray().ToList();
        Assert.Equal(2, phases.Count);
        Assert.Equal("scm_file_tree_fetch", phases[0].GetProperty("name").GetString());
        Assert.Equal("SCM file tree fetch", phases[0].GetProperty("displayName").GetString());
        Assert.Equal(900, phases[0].GetProperty("durationMs").GetInt64());
        Assert.Equal("matches=2", phases[1].GetProperty("summary").GetString());
    }

    [Fact]
    public async Task GetJobProtocol_DerivesEventCategoryForLegacyProtocolRows()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 67, 1);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Trace.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.ToolCall,
                Name = ReviewProtocolEventNames.VerificationEvidenceCollected,
                OccurredAt = DateTimeOffset.UtcNow,
                EventCategory = null,
            });

        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();

        var verificationEvent = protocolJson.GetProperty("events").EnumerateArray().Single();
        Assert.Equal("verification", verificationEvent.GetProperty("eventCategory").GetString());
    }

    [Fact]
    public async Task GetJobProtocolPass_ReturnsFullEventBodiesForSelectedProtocol()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 62, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Detail.cs",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.AiCall,
                Name = "ai_call_iter_1",
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = "full input",
                SystemPrompt = "full system",
                OutputSummary = "full output",
            });
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol/{protocol.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var ev = body.GetProperty("events").EnumerateArray().Single();
        Assert.Equal(protocol.Id.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("full input", ev.GetProperty("inputTextSample").GetString());
        Assert.Equal("full system", ev.GetProperty("systemPrompt").GetString());
        Assert.Equal("full output", ev.GetProperty("outputSummary").GetString());
    }

    [Fact]
    public async Task GetJobProtocol_PreservesTimingAttributionAcrossVisiblePasses()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 66, 1);

        var firstFileResultId = Guid.NewGuid();
        var secondFileResultId = Guid.NewGuid();

        var firstProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = firstFileResultId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            Outcome = "Completed",
        };
        firstProtocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = firstProtocol.Id,
                Kind = ProtocolEventKind.ToolCall,
                Name = "search_source_repo",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                DurationMs = 3200,
                TimingAvailability = ProtocolEventTimingAvailabilities.Captured,
                ToolOutcome = ProtocolEventToolOutcomes.Succeeded,
            });

        var secondProtocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 2,
            Label = "src/Bar.cs",
            FileResultId = secondFileResultId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Outcome = "Completed",
        };
        secondProtocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = secondProtocol.Id,
                Kind = ProtocolEventKind.ToolCall,
                Name = "search_code",
                OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                DurationMs = 5100,
                TimingAvailability = ProtocolEventTimingAvailabilities.Captured,
                ToolOutcome = ProtocolEventToolOutcomes.Succeeded,
            });

        job.Protocols.Add(firstProtocol);
        job.Protocols.Add(secondProtocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol?includeEvents=false");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocols = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .EnumerateArray()
            .ToList();

        Assert.Equal(2, protocols.Count);
        Assert.Contains(
            protocols, protocol =>
                protocol.GetProperty("label").GetString() == "src/Foo.cs"
                && protocol.GetProperty("fileResultId").GetString() == firstFileResultId.ToString()
                && protocol.GetProperty("events").EnumerateArray().Single().GetProperty("durationMs").GetInt64() == 3200);
        Assert.Contains(
            protocols, protocol =>
                protocol.GetProperty("label").GetString() == "src/Bar.cs"
                && protocol.GetProperty("fileResultId").GetString() == secondFileResultId.ToString()
                && protocol.GetProperty("events").EnumerateArray().Single().GetProperty("durationMs").GetInt64() == 5100);
    }

    [Fact]
    public async Task GetJobProtocol_FileProtocol_IncludesFinalSummaryAndComments()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 7, 1);

        var fileResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        fileResult.MarkCompleted(
            "Final file summary",
            [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Final comment from file pass")]);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };

        job.FileReviewResults.Add(fileResult);
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();

        Assert.Equal("Final file summary", protocolJson.GetProperty("finalSummary").GetString());
        var finalComment = protocolJson.GetProperty("finalComments").EnumerateArray().Single();
        Assert.Equal("Final comment from file pass", finalComment.GetProperty("message").GetString());
        Assert.Equal("src/Foo.cs", finalComment.GetProperty("filePath").GetString());
    }

    [Fact]
    public async Task GetJobProtocol_FileLinkedPass_IncludesFileOutcomeMetadata()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 8, 1);

        var fileResult = new ReviewFileResult(job.Id, "src/Agentic.cs");
        fileResult.MarkCompleted("Final file summary", []);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Agentic.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileDegraded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                InputTextSample = "{\"stage\":\"investigation\"}",
                OutputSummary = "{\"reason\":\"fallback\"}",
            });

        job.FileReviewResults.Add(fileResult);
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();

        Assert.Equal("https://dev.azure.com/org", protocolJson.GetProperty("providerScopePath").GetString());
        Assert.Equal("proj", protocolJson.GetProperty("providerProjectKey").GetString());
        Assert.Equal("repo", protocolJson.GetProperty("repositoryId").GetString());
        Assert.Equal(8, protocolJson.GetProperty("pullRequestId").GetInt32());

        var fileOutcome = protocolJson.GetProperty("fileOutcome");
        Assert.Equal("src/Agentic.cs", fileOutcome.GetProperty("filePath").GetString());
        Assert.True(fileOutcome.GetProperty("isComplete").GetBoolean());
        Assert.True(fileOutcome.GetProperty("isDegraded").GetBoolean());
    }

    [Theory]
    [InlineData("blocked_scope_violation")]
    [InlineData("blocked_budget_exhausted")]
    [InlineData("failed")]
    public async Task GetJobProtocol_AgenticStageBRuntimeTrace_PreservesAuthoritativeStatusPayload(string runtimeStatus)
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 9, 1);

        var fileResult = new ReviewFileResult(job.Id, "src/Agentic.cs");
        fileResult.MarkCompleted("Final file summary", []);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Agentic.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileDegraded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-5),
                InputTextSample = "{\"stage\":\"investigation\",\"taskId\":\"task-001\"}",
                OutputSummary =
                    $"{{\"Status\":\"degraded\",\"ToolUsage\":[{{\"ToolName\":\"get_file_content\",\"Status\":\"{runtimeStatus}\",\"Target\":\"src/Agentic.cs\"}}],\"Degraded\":true,\"candidateCount\":0}}",
            });

        job.FileReviewResults.Add(fileResult);
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();

        var degradedEvent = protocolJson.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == ReviewProtocolEventNames.AgenticFileDegraded);
        Assert.Contains(runtimeStatus, degradedEvent.GetProperty("outputSummary").GetString());
        Assert.True(protocolJson.GetProperty("fileOutcome").GetProperty("isDegraded").GetBoolean());
    }

    [Fact]
    public async Task GetJobProtocol_AgenticFileProtocol_IncludesFollowUpVisibilityMetadata()
    {
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 10, 1);

        var fileResult = new ReviewFileResult(job.Id, "src/FollowUp.cs");
        fileResult.MarkCompleted("Final file summary", []);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/FollowUp.cs",
            FileResultId = fileResult.Id,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
        };
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFilePlanCreated,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-3),
                InputTextSample = "{\"stage\":\"planning\"}",
                OutputSummary =
                    "{\"anchorFilePath\":\"src/FollowUp.cs\",\"investigationTasks\":[{\"taskId\":\"task-100\",\"triggerFamily\":\"dispatch_or_registration\"}]}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileInvestigationResult,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample =
                    "{\"stage\":\"investigation\",\"taskId\":\"task-100\",\"anchorFile\":\"src/FollowUp.cs\"}",
                OutputSummary = "{\"status\":\"completed\",\"degraded\":false,\"diagnosticsOnly\":false}",
            });
        protocol.Events.Add(
            new ProtocolEvent
            {
                Id = Guid.NewGuid(),
                ProtocolId = protocol.Id,
                Kind = ProtocolEventKind.Operational,
                Name = ReviewProtocolEventNames.AgenticFileFollowUpDependencyRecorded,
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                InputTextSample =
                    "{\"anchorFile\":\"src/FollowUp.cs\",\"taskId\":\"task-100\",\"triggerFamily\":\"dispatch_or_registration\"}",
                OutputSummary = "{\"dependencyRecorded\":true}",
            });

        job.FileReviewResults.Add(fileResult);
        job.Protocols.Add(protocol);
        await jobRepo.AddAsync(job);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviewing/jobs/{job.Id}/protocol");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateAdminToken());
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var protocolJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .Single();

        var followUp = protocolJson.GetProperty("followUp");
        Assert.True(followUp.GetProperty("used").GetBoolean());
        Assert.Equal("dispatch_or_registration", followUp.GetProperty("triggerFamily").GetString());
        Assert.True(followUp.GetProperty("completedSuccessfully").GetBoolean());
        Assert.True(followUp.GetProperty("dependencyRecorded").GetBoolean());
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
                Subject = new ClaimsIdentity(
                [
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

                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

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
