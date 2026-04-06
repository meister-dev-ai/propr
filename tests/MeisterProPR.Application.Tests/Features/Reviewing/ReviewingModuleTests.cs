// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetReviewJobProtocol;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing;

public sealed class ReviewingModuleTests
{
    [Fact]
    public async Task GetReviewJobProtocolHandler_WhenJobMissing_ReturnsNull()
    {
        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        diagnosticsReader.GetJobProtocolAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetReviewJobProtocolResult?>(null));

        var handler = new GetReviewJobProtocolHandler(diagnosticsReader);

        var result = await handler.HandleAsync(new GetReviewJobProtocolQuery(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetReviewJobProtocolHandler_WhenJobExists_ReturnsProtocols()
    {
        var jobId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var expected = new GetReviewJobProtocolResult(
            clientId,
            [new ReviewJobProtocolDto(
                Guid.NewGuid(),
                jobId,
                1,
                "posting",
                null,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow,
                "Completed",
                10,
                5,
                1,
                0,
                null,
                null,
                null,
                [])]);

        var diagnosticsReader = Substitute.For<IReviewDiagnosticsReader>();
        diagnosticsReader.GetJobProtocolAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<GetReviewJobProtocolResult?>(expected));

        var handler = new GetReviewJobProtocolHandler(diagnosticsReader);

        var result = await handler.HandleAsync(new GetReviewJobProtocolQuery(jobId), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(clientId, result!.ClientId);
        Assert.Single(result.Protocols);
        Assert.Equal("posting", result.Protocols[0].Label);
    }
}
