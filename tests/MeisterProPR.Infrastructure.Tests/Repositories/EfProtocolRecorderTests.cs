// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="EfProtocolRecorder.RecordMemoryEventAsync" /> (T014).
/// </summary>
[Collection("PostgresIntegration")]
public sealed class EfProtocolRecorderTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly string[] ValidEventNames =
    [
        "memory_embedding_stored",
        "memory_embedding_removed",
        "memory_retrieval_executed",
        "memory_reconsideration_completed",
        "memory_operation_failed",
    ];

    private static readonly string[] ValidDedupEventNames =
    [
        "dedup_summary",
        "dedup_degraded_mode",
    ];

    private MeisterProPRDbContext _db = null!;
    private Guid _jobId;
    private Guid _protocolId;
    private EfProtocolRecorder _recorder = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var factory = new TestDbContextFactory(fixture.ConnectionString);
        this._recorder = new EfProtocolRecorder(factory, NullLogger<EfProtocolRecorder>.Instance);

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._db = new MeisterProPRDbContext(options);

        // Seed a job and protocol record to attach events to.
        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/test",
            "test-project",
            "test-repo",
            1,
            1);
        this._jobId = job.Id;
        this._db.ReviewJobs.Add(job);
        await this._db.SaveChangesAsync();

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
        };
        this._db.ReviewJobProtocols.Add(protocol);
        await this._db.SaveChangesAsync();
        this._protocolId = protocol.Id;
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Theory]
    [MemberData(nameof(GetValidEventNames))]
    public async Task RecordMemoryEventAsync_WithValidEventName_PersistsEventWithMemoryOperationKind(string eventName)
    {
        await this._recorder.RecordMemoryEventAsync(this._protocolId, eventName, "{\"key\":\"value\"}", null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == eventName)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.MemoryOperation, stored.Kind);
        Assert.Equal(eventName, stored.Name);
        Assert.Contains("key", stored.InputTextSample ?? "");
    }

    [Fact]
    public async Task RecordMemoryEventAsync_WithError_PersistsErrorField()
    {
        const string eventName = "memory_operation_failed";
        const string error = "connection refused";

        await this._recorder.RecordMemoryEventAsync(this._protocolId, eventName, null, error);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == eventName)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(error, stored.Error);
    }

    [Fact]
    public async Task RecordMemoryEventAsync_WithNonExistentProtocolId_DoesNotThrow()
    {
        // Must never throw — even for non-existent protocol IDs.
        var exception = await Record.ExceptionAsync(() =>
            this._recorder.RecordMemoryEventAsync(Guid.NewGuid(), "memory_embedding_stored", null, null));

        Assert.Null(exception);
    }

    [Fact]
    public async Task RecordMemoryEventAsync_WithNullDetails_PersistsNullInputSample()
    {
        await this._recorder.RecordMemoryEventAsync(this._protocolId, "memory_embedding_stored", null, null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == "memory_embedding_stored")
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Null(stored.InputTextSample);
    }

    [Theory]
    [MemberData(nameof(GetValidDedupEventNames))]
    public async Task RecordDedupEventAsync_WithValidEventName_PersistsMemoryOperationEvent(string eventName)
    {
        await this._recorder.RecordDedupEventAsync(this._protocolId, eventName, "{\"candidateCount\":2}", null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == eventName)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Contains("candidateCount", stored.InputTextSample ?? string.Empty);
    }

    [Fact]
    public async Task RecordDedupEventAsync_PersistsSuppressionCountsAndDegradedMetadataOncePerPass()
    {
        await this._recorder.RecordDedupEventAsync(
            this._protocolId,
            "dedup_summary",
            "{\"candidateCount\":3,\"postedCount\":1,\"suppressedCount\":2}",
            null);
        await this._recorder.RecordDedupEventAsync(
            this._protocolId,
            "dedup_degraded_mode",
            "{\"degradedComponents\":[\"thread_memory_embedding\"],\"fallbackChecks\":[\"deterministic_text_similarity\"],\"affectedCandidateCount\":2}",
            null);

        var storedEvents = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId &&
                        (e.Name == "dedup_summary" || e.Name == "dedup_degraded_mode"))
            .OrderBy(e => e.Name)
            .ToListAsync();

        Assert.Equal(2, storedEvents.Count);
        Assert.Contains(
            "\"suppressedCount\":2",
            storedEvents.First(e => e.Name == "dedup_summary").InputTextSample ?? string.Empty);
        Assert.Contains(
            "thread_memory_embedding",
            storedEvents.First(e => e.Name == "dedup_degraded_mode").InputTextSample ?? string.Empty);
    }

    [Fact]
    public async Task RecordCommentRelevanceEventAsync_PersistsComparableOutputAndDegradedMarkers()
    {
        const string details = """
                               {"implementationId":"hybrid-v1","degradedComponents":["comment_relevance_evaluator"],"fallbackChecks":["pre_filter_comments_retained"],"degradedCause":"Evaluator timeout."}
                               """;
        const string output = """
                              {"implementationId":"hybrid-v1","implementationVersion":"1.0.0","filePath":"src/Foo.cs","originalCommentCount":1,"keptCount":1,"discardedCount":0,"reasonBuckets":{},"decisionSources":{"fallback_mode":1},"degradedComponents":["comment_relevance_evaluator"],"fallbackChecks":["pre_filter_comments_retained"],"degradedCause":"Evaluator timeout.","aiTokenUsage":{"inputTokens":320,"outputTokens":71},"discarded":[]}
                              """;

        await this._recorder.RecordCommentRelevanceEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.CommentRelevanceEvaluatorDegraded,
            details,
            output,
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.CommentRelevanceEvaluatorDegraded)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Contains("comment_relevance_evaluator", stored.InputTextSample ?? string.Empty);
        Assert.Contains("pre_filter_comments_retained", stored.InputTextSample ?? string.Empty);
        Assert.Contains("\"inputTokens\":320", stored.OutputSummary ?? string.Empty);
        Assert.Contains("\"discarded\":[]", stored.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task RecordReviewFindingGateEventAsync_PersistsDecisionPayloads()
    {
        const string details = """
                               {"candidateCount":2,"publishCount":1,"summaryOnlyCount":1,"dropCount":0}
                               """;
        const string output = """
                              {"findingId":"finding-001","disposition":"SummaryOnly","category":"cross_cutting","ruleSource":"cross_cutting_evidence_rules","reasonCodes":["missing_multi_file_evidence"],"blockedInvariantIds":[],"summaryText":"Needs stronger evidence."}
                              """;

        await this._recorder.RecordReviewFindingGateEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.ReviewFindingGateDecision,
            details,
            output,
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.ReviewFindingGateDecision)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Contains("summaryOnlyCount", stored.InputTextSample ?? string.Empty);
        Assert.Contains("finding-001", stored.OutputSummary ?? string.Empty);
        Assert.Contains("SummaryOnly", stored.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task RecordReviewStrategyEventAsync_SessionTurn_PersistsSessionContextPayloads()
    {
        const string details = """
                               {"turnNumber":2,"sessionMode":"ProviderManagedSession","contextStrategy":"DeltaContext","newInputSummary":"tool result delta only","replayedPayloadSummary":"system+tool transcript","providerSessionId":"conv-1","providerResponseId":"resp-2"}
                               """;
        const string output = """
                              {"outputSample":"Working set refined.","continuationHandle":{"handleType":"ProviderSession","handleId":"conv-1","providerSessionId":"conv-1","providerResponseId":"resp-2"}}
                              """;

        await this._recorder.RecordReviewStrategyEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionTurn,
            details,
            output,
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.ReviewAgentSessionTurn)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Equal(details, stored.InputTextSample);
        Assert.Equal(output, stored.OutputSummary);
        Assert.Contains("ProviderManagedSession", stored.InputTextSample ?? string.Empty);
        Assert.Contains("conv-1", stored.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task RecordReviewStrategyEventAsync_SessionBinding_PersistsBindingPayloads()
    {
        const string details = """
                               {"sessionOwnerId":"session-1","conversationOwnerId":"session-1","bindingMethod":"created_remote_thread","bindingOutcome":"succeeded","promptMode":"InitialBind","remoteConversationId":"conv-1","sessionMode":"ProviderManagedSession"}
                               """;
        const string output = """
                              {"providerResponseId":"resp-1"}
                              """;

        await this._recorder.RecordReviewStrategyEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionBinding,
            details,
            output,
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.ReviewAgentSessionBinding)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Equal(details, stored.InputTextSample);
        Assert.Equal(output, stored.OutputSummary);
        Assert.Contains("created_remote_thread", stored.InputTextSample ?? string.Empty);
        Assert.Contains("InitialBind", stored.InputTextSample ?? string.Empty);
        Assert.Contains("resp-1", stored.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task RecordReviewStrategyEventAsync_SessionFallback_PersistsFallbackPayloads()
    {
        const string details = """
                               {"fromMode":"ProviderManagedSession","toMode":"LocalManagedSession","reason":"provider_session_continue_failed","turnNumber":2,"preservedState":"preserved durable system prompts and latest turn transcript"}
                               """;

        await this._recorder.RecordReviewStrategyEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.ReviewAgentSessionFallback,
            details,
            null,
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.ReviewAgentSessionFallback)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Equal(details, stored.InputTextSample);
        Assert.Null(stored.OutputSummary);
        Assert.Contains("provider_session_continue_failed", stored.InputTextSample ?? string.Empty);
        Assert.Contains("LocalManagedSession", stored.InputTextSample ?? string.Empty);
    }

    [Fact]
    public async Task RecordAiCallAsync_PersistsFullTextWithoutTruncation_AndStripsNullBytes()
    {
        var inputText = new string('I', 60_000) + "\0TAIL";
        var outputText = new string('O', 60_000) + "\0DONE";

        await this._recorder.RecordAiCallAsync(
            this._protocolId,
            1,
            123,
            456,
            inputText,
            null,
            outputText);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == "ai_call_iter_1")
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.NotNull(stored.InputTextSample);
        Assert.NotNull(stored.OutputSummary);
        Assert.Equal(new string('I', 60_000) + "TAIL", stored.InputTextSample);
        Assert.Equal(new string('O', 60_000) + "DONE", stored.OutputSummary);
        Assert.DoesNotContain('\0', stored.InputTextSample);
        Assert.DoesNotContain('\0', stored.OutputSummary);
    }

    [Fact]
    public async Task RecordAiCallAsync_PersistsCacheAndFinalizationDiagnostics()
    {
        await this._recorder.RecordAiCallAsync(
            this._protocolId,
            2,
            2048,
            128,
            "input",
            "system",
            "output",
            cachedInputTokens: 1024,
            cacheStatus: CacheCallStatus.Hit,
            prefixEligibility: PrefixEligibilityStatus.Eligible,
            finalizationAttemptKind: "ForcedFinal",
            finalizationReason: "iteration_limit_reached",
            finalizationOutcome: "ProducedFinalText");

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == "ai_call_iter_2")
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(1024, stored.CachedInputTokens);
        Assert.Equal(CacheCallStatus.Hit, stored.CacheStatus);
        Assert.Null(stored.CacheMissCategory);
        Assert.Equal(PrefixEligibilityStatus.Eligible, stored.PrefixEligibility);
        Assert.Equal("ForcedFinal", stored.FinalizationAttemptKind);
        Assert.Equal("iteration_limit_reached", stored.FinalizationReason);
        Assert.Equal("ProducedFinalText", stored.FinalizationOutcome);
    }

    [Fact]
    public async Task RecordToolCallAsync_WithDeepIteration_PersistsFullResultWithoutExcerptTruncation()
    {
        var result = new string('R', 2_500) + " final-tail";

        await this._recorder.RecordToolCallAsync(
            this._protocolId,
            "read_file",
            "{\"path\":\"src/Foo.cs\"}",
            result,
            4);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == "read_file")
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(result, stored.OutputSummary);
        Assert.DoesNotContain("[TRUNCATED]", stored.OutputSummary);
        Assert.EndsWith("final-tail", stored.OutputSummary);
    }

    [Fact]
    public async Task RecordToolCallAsync_PersistsToolEvidenceDiagnostics()
    {
        await this._recorder.RecordToolCallAsync(
            this._protocolId,
            "get_file_content",
            "{\"path\":\"src/Foo.cs\"}",
            "bounded result",
            1,
            toolEvidenceAction: "Bounded",
            toolEvidenceOriginalPayloadTokens: 2000,
            toolEvidenceBoundedPayloadTokens: 256,
            toolEvidenceRefreshable: true);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == "get_file_content")
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal("Bounded", stored.ToolEvidenceAction);
        Assert.Equal("get_file_content", stored.ToolEvidenceSourceToolName);
        Assert.Equal(2000, stored.ToolEvidenceOriginalPayloadTokens);
        Assert.Equal(256, stored.ToolEvidenceBoundedPayloadTokens);
        Assert.True(stored.ToolEvidenceRefreshable);
    }

    [Fact]
    public async Task AddTokensAsync_WithMatchingProtocol_IncrementsProtocolAndJobTotals()
    {
        // Arrange: mark the protocol as completed with known token counts.
        await this._recorder.SetCompletedAsync(this._protocolId, "Completed", 1000, 500, 2, 1, null);

        // Act: add extra tokens from an out-of-loop AI call (e.g. memory reconsideration).
        await this._recorder.AddTokensAsync(this._protocolId, 150, 75);

        var stored = await this._db.ReviewJobProtocols
            .AsNoTracking()
            .Where(p => p.Id == this._protocolId)
            .FirstOrDefaultAsync();

        var job = await this._db.ReviewJobs
            .AsNoTracking()
            .Where(j => j.Id == this._jobId)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.NotNull(job);
        Assert.Equal(1150, stored.TotalInputTokens);
        Assert.Equal(575, stored.TotalOutputTokens);
        Assert.Equal(1150, job.TotalInputTokensAggregated);
        Assert.Equal(575, job.TotalOutputTokensAggregated);
    }

    [Fact]
    public async Task SetCompletedAsync_PersistsCachedInputTotalAndObservability()
    {
        await this._recorder.SetCompletedAsync(
            this._protocolId,
            "Completed",
            2000,
            500,
            2,
            1,
            null,
            totalCachedInputTokens: 1200,
            cacheObservability: CacheObservabilityStatus.Observable);

        var stored = await this._db.ReviewJobProtocols
            .AsNoTracking()
            .Where(p => p.Id == this._protocolId)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(1200, stored.TotalCachedInputTokens);
        Assert.Equal(CacheObservabilityStatus.Observable, stored.CacheObservability);
    }

    [Fact]
    public async Task AddTokensAsync_WithNonExistentProtocolId_DoesNotThrow()
    {
        var exception = await Record.ExceptionAsync(() =>
            this._recorder.AddTokensAsync(Guid.NewGuid(), 100, 50));

        Assert.Null(exception);
    }

    public static IEnumerable<object[]> GetValidEventNames()
    {
        return ValidEventNames.Select(name => new object[] { name });
    }

    public static IEnumerable<object[]> GetValidDedupEventNames()
    {
        return ValidDedupEventNames.Select(name => new object[] { name });
    }
}

/// <summary>
///     Minimal <see cref="IDbContextFactory{TContext}" /> used by these tests.
/// </summary>
file sealed class TestDbContextFactory(string connectionString)
    : IDbContextFactory<MeisterProPRDbContext>
{
    public MeisterProPRDbContext CreateDbContext()
    {
        var opts = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;
        return new MeisterProPRDbContext(opts);
    }

    public Task<MeisterProPRDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(this.CreateDbContext());
    }
}
