// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using MeisterDev.ProPR.CodeInsights.History;
using MeisterDev.ProPR.CodeInsights.Http;

namespace MeisterDev.ProPR.CodeInsights.Tests.Controllers;

/// <summary>
///     The import endpoint. It writes, and it can spend tokens, so who may call it and what it refuses matter as
///     much as what it returns.
/// </summary>
public sealed class ReviewerPerformanceImportTests
{
    private static readonly DateOnly From = new(2026, 5, 1);
    private static readonly DateOnly To = new(2026, 5, 31);

    [Fact]
    public async Task AClientUserWhoAdministersNoTenantCannotImport()
    {
        var harness = new CodeInsightAudienceHarness();

        var result = await harness.ReviewerPerformance.Import(new CodeInsightImportRequestBody(CodeInsightAudienceHarness.MineA, From, To));

        // The same bar as reading this surface: judging and repairing the measurement is an operator's job.
        Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await harness.Importer.DidNotReceive()
            .ImportAsync(Arg.Any<CodeInsightImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportingSomebodyElsesClientIsDeniedRatherThanQuietlyIgnored()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        var result = await harness.ReviewerPerformance.Import(new CodeInsightImportRequestBody(CodeInsightAudienceHarness.SomebodyElses, From, To));

        Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, ((ObjectResult)result).StatusCode);
        await harness.Importer.DidNotReceive()
            .ImportAsync(Arg.Any<CodeInsightImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnInvertedWindowIsRefusedBeforeAnythingIsRead()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);

        var result = await harness.ReviewerPerformance.Import(new CodeInsightImportRequestBody(CodeInsightAudienceHarness.MineA, To, From));

        Assert.IsType<BadRequestObjectResult>(result);
        await harness.Importer.DidNotReceive()
            .ImportAsync(Arg.Any<CodeInsightImportRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OutcomesAreOnlyRequestedWhenTheCallerAsksForThem()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.Importer
            .ImportAsync(Arg.Any<CodeInsightImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightImportResult(4, 3, 1, 12, 2, 2, 0, 0, false));

        await harness.ReviewerPerformance.Import(new CodeInsightImportRequestBody(CodeInsightAudienceHarness.MineA, From, To));

        // The default is the free run: the one part of an import that calls a model stays off unless asked for.
        await harness.Importer.Received(1).ImportAsync(
            Arg.Is<CodeInsightImportRequest>(request => !request.IncludeOutcomes),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhatTheRunCouldNotDoIsReportedRatherThanRoundedAway()
    {
        var harness = new CodeInsightAudienceHarness(tenantAdmin: true);
        harness.Importer
            .ImportAsync(Arg.Any<CodeInsightImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CodeInsightImportResult(40, 31, 9, 184, 26, 17, 58, 23, false, true));

        var result = await harness.ReviewerPerformance.Import(
            new CodeInsightImportRequestBody(CodeInsightAudienceHarness.MineA, From, To, IncludeOutcomes: true));

        var payload = Assert.IsType<CodeInsightImportResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(9, payload.JobsAlreadyCollected);
        // Findings whose comments were never linked to a thread can never gain an outcome, and the surface says so.
        Assert.Equal(26, payload.FindingsWithoutThread);
        Assert.True(payload.ReachedLimit);
        Assert.Equal(58, payload.OutcomeThreadsReplayed);
        Assert.Equal(23, payload.HumanThreadsReplayed);
    }
}
