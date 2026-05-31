// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class EfReviewDiagnosticsReaderVerificationTests
{
    [Fact]
    public async Task ProtocolEventModel_IncludesEventCategoryColumnAndIndexes()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"EfReviewDiagnosticsReaderVerification-{Guid.NewGuid():N}")
            .Options;

        await using var db = new MeisterProPRDbContext(options);
        var entityType = db.Model.FindEntityType(typeof(ProtocolEvent));

        Assert.NotNull(entityType);
        Assert.NotNull(entityType!.FindProperty(nameof(ProtocolEvent.EventCategory)));

        var indexColumns = entityType.GetIndexes()
            .Select(index => string.Join('|', index.Properties.Select(property => property.Name)))
            .ToArray();

        Assert.Contains(nameof(ProtocolEvent.EventCategory), indexColumns);
        Assert.Contains(nameof(ProtocolEvent.OccurredAt), indexColumns);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenAgenticFilePassDegrades_MarksFileOutcomeAsDegraded()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 9, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.JobOverride,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var fileResult = new ReviewFileResult(job.Id, "src/Bar.cs");
        fileResult.MarkCompleted("Summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Bar.cs",
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
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = "{\"stage\":\"investigation\",\"taskId\":\"task-001\"}",
                OutputSummary = "{\"reason\":\"Tool budget exhausted.\"}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.FileOutcome);
        Assert.True(returnedProtocol.FileOutcome!.IsDegraded);
        Assert.Equal(ReviewStrategy.AgenticFileByFile, returnedProtocol.ResolvedReviewStrategy);
        Assert.Equal(ReviewStrategySelectionSource.JobOverride, returnedProtocol.StrategySelectionSource);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenJobContainsVerificationDegradedEvents_ReturnsNamesAndPayloadsUnchanged()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "synthesis",
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
                Name = ReviewProtocolEventNames.VerificationDegraded,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample = "{\"degradedComponent\":\"evidence_collection\",\"stage\":\"PrLevel\"}",
                Error = "ProCursor unavailable.",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        var degradedEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.VerificationDegraded);
        Assert.Contains("evidence_collection", degradedEvent.InputTextSample ?? string.Empty);
        Assert.Contains("ProCursor unavailable.", degradedEvent.Error ?? string.Empty);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenJobContainsSessionFallbackEvent_ReturnsFallbackPayloadUnchanged()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 3, 1);
        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Foo.cs",
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
                Name = ReviewProtocolEventNames.ReviewAgentSessionFallback,
                OccurredAt = DateTimeOffset.UtcNow,
                InputTextSample =
                    "{\"fromMode\":\"ProviderManagedSession\",\"toMode\":\"LocalManagedSession\",\"reason\":\"provider_session_continue_failed\",\"turnNumber\":2,\"preservedState\":\"preserved durable system prompts and latest turn transcript\"}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        var fallbackEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.ReviewAgentSessionFallback);
        Assert.Contains("provider_session_continue_failed", fallbackEvent.InputTextSample ?? string.Empty);
        Assert.Contains("LocalManagedSession", fallbackEvent.InputTextSample ?? string.Empty);
        Assert.Null(fallbackEvent.OutputSummary);
    }

    [Fact]
    public async Task GetJobProtocolAsync_WhenFollowUpDependencyExistsWithoutCompletedResult_StillProjectsDependencyAndIncompleteCompletion()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 13, 1);
        var fileResult = new ReviewFileResult(job.Id, "src/Bar.cs");
        fileResult.MarkCompleted("Summary", []);
        job.FileReviewResults.Add(fileResult);

        var protocol = new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            AttemptNumber = 1,
            Label = "src/Bar.cs",
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
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(-2),
                InputTextSample = "{\"stage\":\"planning\"}",
                OutputSummary =
                    "{\"anchorFilePath\":\"src/Bar.cs\",\"investigationTasks\":[{\"taskId\":\"task-009\",\"triggerFamily\":\"explicit_follow_up_signal\"}]}",
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
                    "{\"anchorFile\":\"src/Bar.cs\",\"taskId\":\"task-009\",\"triggerFamily\":\"explicit_follow_up_signal\"}",
                OutputSummary = "{\"dependencyRecorded\":true}",
            });

        job.Protocols.Add(protocol);

        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithProtocolsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var sut = new EfReviewDiagnosticsReader(repository);

        var result = await sut.GetJobProtocolAsync(job.Id, ct: CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        Assert.NotNull(returnedProtocol.FollowUp);
        Assert.True(returnedProtocol.FollowUp!.Used);
        Assert.Equal("explicit_follow_up_signal", returnedProtocol.FollowUp.TriggerFamily);
        Assert.False(returnedProtocol.FollowUp.CompletedSuccessfully);
        Assert.True(returnedProtocol.FollowUp.DependencyRecorded);
    }
}
