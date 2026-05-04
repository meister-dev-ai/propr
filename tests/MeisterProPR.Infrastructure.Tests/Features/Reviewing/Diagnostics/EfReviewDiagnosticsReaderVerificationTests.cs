// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Diagnostics;

public sealed class EfReviewDiagnosticsReaderVerificationTests
{
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

        var result = await sut.GetJobProtocolAsync(job.Id, CancellationToken.None);

        var returnedProtocol = Assert.Single(result!.Protocols);
        var degradedEvent = Assert.Single(returnedProtocol.Events, e => e.Name == ReviewProtocolEventNames.VerificationDegraded);
        Assert.Contains("evidence_collection", degradedEvent.InputTextSample ?? string.Empty);
        Assert.Contains("ProCursor unavailable.", degradedEvent.Error ?? string.Empty);
    }
}
