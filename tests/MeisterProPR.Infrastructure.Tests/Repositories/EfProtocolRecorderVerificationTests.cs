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

namespace MeisterProPR.Infrastructure.Tests.Repositories;

[Collection("PostgresIntegration")]
public sealed class EfProtocolRecorderVerificationTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _db = null!;
    private Guid _protocolId;
    private EfProtocolRecorder _recorder = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        var factory = new TestDbContextFactory(options);
        this._recorder = new EfProtocolRecorder(factory, NullLogger<EfProtocolRecorder>.Instance);

        this._db = new MeisterProPRDbContext(options);

        var job = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/test",
            "test-project",
            "test-repo",
            1,
            1);

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

    [Fact]
    public async Task RecordVerificationEventAsync_PersistsVerificationPayload()
    {
        await this._recorder.RecordVerificationEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.VerificationLocalDecision,
            "{\"findingId\":\"finding-001\"}",
            "{\"recommendedDisposition\":\"Drop\"}",
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.VerificationLocalDecision)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Equal(ProtocolEventKind.Operational, stored.Kind);
        Assert.Contains("finding-001", stored.InputTextSample ?? string.Empty);
        Assert.Contains("Drop", stored.OutputSummary ?? string.Empty);
    }

    [Fact]
    public async Task RecordVerificationEventAsync_PersistsEvidenceAttemptAndProCursorStatusPayload()
    {
        await this._recorder.RecordVerificationEventAsync(
            this._protocolId,
            ReviewProtocolEventNames.VerificationEvidenceCollected,
            "{\"findingId\":\"finding-001\",\"claimId\":\"claim-001\"}",
            "{\"evidenceAttempts\":[{\"sourceFamily\":\"ProCursorKnowledge\",\"status\":\"Empty\"}],\"hasProCursorAttempt\":true,\"proCursorResultStatus\":\"Empty\"}",
            null);

        var stored = await this._db.ProtocolEvents
            .Where(e => e.ProtocolId == this._protocolId && e.Name == ReviewProtocolEventNames.VerificationEvidenceCollected)
            .FirstOrDefaultAsync();

        Assert.NotNull(stored);
        Assert.Contains("claim-001", stored.InputTextSample ?? string.Empty);
        Assert.Contains("evidenceAttempts", stored.OutputSummary ?? string.Empty);
        Assert.Contains("proCursorResultStatus", stored.OutputSummary ?? string.Empty);
    }
}
