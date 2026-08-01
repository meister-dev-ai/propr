// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public class ReviewReasoningRequestTests
{
    [Fact]
    public void ApplyReasoning_CaptureOffAndEffortNone_LeavesRawRepresentationUnset()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: false, ReviewReasoningEffort.None);

        // Default-none path: byte-identical to sending no reasoning options at all.
        Assert.Same(options, result);
        Assert.Null(result.RawRepresentationFactory);
    }

    // OpenAI rejects the whole request with HTTP 400 when a reasoning model is sent a temperature, so a
    // configured effort has to clear it. Every review call carries a temperature, so this failed all of them.
    [Theory]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void ApplyReasoning_WithAnEffort_DropsTheSamplingTemperature(ReviewReasoningEffort effort)
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: false, effort);

        Assert.Null(result.Temperature);
    }

    // An ordinary sampling model is not asked to reason, so its configured temperature has to survive.
    [Fact]
    public void ApplyReasoning_WithNoEffort_KeepsTheSamplingTemperature()
    {
        var options = new ChatOptions { Temperature = 0.2f };

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        Assert.Equal(0.2f, result.Temperature);
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndEffortNone_RequestsSummaryButNoEffort()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        Assert.NotNull(result.RawRepresentationFactory);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        // Effort stays unset when None, so behavior matches the pre-feature capture-on request exactly.
        Assert.Null(createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOffAndEffortSet_SetsEffortUnconditionallyWithoutSummary()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: false, ReviewReasoningEffort.High);

        Assert.NotNull(result.RawRepresentationFactory);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        // Effort is applied independently of the reasoning-summary capture flag.
        Assert.Equal(ResponseReasoningEffortLevel.High, createOptions.ReasoningOptions!.ReasoningEffortLevel);
        // No summary requested when capture is off.
        Assert.Null(createOptions.ReasoningOptions.ReasoningSummaryVerbosity);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_CaptureOnAndEffortSet_SetsBothSummaryAndEffort()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.Medium);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        Assert.Equal(ResponseReasoningEffortLevel.Medium, createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Theory]
    [InlineData(ReviewReasoningEffort.Low)]
    [InlineData(ReviewReasoningEffort.Medium)]
    [InlineData(ReviewReasoningEffort.High)]
    public void ApplyReasoning_MapsEachEffortLevelToTheProviderLevel(ReviewReasoningEffort effort)
    {
        var result = new ChatOptions().ApplyReasoning(captureReasoning: false, effort);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        var expected = effort switch
        {
            ReviewReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReviewReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReviewReasoningEffort.High => ResponseReasoningEffortLevel.High,
            _ => (ResponseReasoningEffortLevel?)null,
        };
        Assert.Equal(expected, createOptions.ReasoningOptions!.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoning_Enabled_ReturnsFreshRawInstancePerInvocation()
    {
        var options = new ChatOptions().ApplyReasoning(captureReasoning: true, ReviewReasoningEffort.None);

        var first = options.RawRepresentationFactory!(null!);
        var second = options.RawRepresentationFactory!(null!);

        Assert.NotSame(first, second);
    }
}
