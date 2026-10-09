// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

public sealed class SymbolProxyBoundaryTests
{
    [Fact]
    public async Task SymbolQueriesUseTheAuthorizedProxyWithoutLocalWorkspaceTools()
    {
        var proxy = Substitute.For<IRunnerToolProxy>();
        var local = Substitute.For<IReviewContextTools>();
        var call = new RunnerCallContext(Guid.NewGuid(), 3, "runner");
        var expected = new ProCursorSymbolInsightDto("current", null, true, true, null, []);
        proxy.GetSymbolInsightAsync(call, "Symbol", "qualifiedName", 13, Arg.Any<CancellationToken>())
            .Returns(RunnerToolResult<ProCursorSymbolInsightDto>.Served(expected));

        var result = await new ProxyReviewContextTools(call, proxy, local)
            .GetProCursorSymbolInfoAsync("Symbol", "qualifiedName", 13, CancellationToken.None);

        Assert.Same(expected, result);
        await proxy.Received(1).GetSymbolInsightAsync(call, "Symbol", "qualifiedName", 13, CancellationToken.None);
        await local.DidNotReceiveWithAnyArgs().GetProCursorSymbolInfoAsync(default!, default, default, default);
    }
}
