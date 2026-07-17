// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterProPR.Infrastructure.Tests.AI;

public class ReviewReasoningRequestTests
{
    [Fact]
    public void ApplyReasoningSummaryOptIn_Disabled_LeavesRawRepresentationUnset()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoningSummaryOptIn(captureReasoning: false);

        Assert.Same(options, result);
        Assert.Null(result.RawRepresentationFactory);
    }

    [Fact]
    public void ApplyReasoningSummaryOptIn_Enabled_RequestsAutoReasoningSummary()
    {
        var options = new ChatOptions();

        var result = options.ApplyReasoningSummaryOptIn(captureReasoning: true);

        Assert.NotNull(result.RawRepresentationFactory);

        var raw = result.RawRepresentationFactory!(null!);
#pragma warning disable OPENAI001
        var createOptions = Assert.IsType<CreateResponseOptions>(raw);
        Assert.NotNull(createOptions.ReasoningOptions);
        Assert.Equal(ResponseReasoningSummaryVerbosity.Auto, createOptions.ReasoningOptions!.ReasoningSummaryVerbosity);
        // Effort stays governed by the selected model/deployment, not forced here.
        Assert.Null(createOptions.ReasoningOptions.ReasoningEffortLevel);
#pragma warning restore OPENAI001
    }

    [Fact]
    public void ApplyReasoningSummaryOptIn_Enabled_ReturnsFreshRawInstancePerInvocation()
    {
        var options = new ChatOptions().ApplyReasoningSummaryOptIn(captureReasoning: true);

        var first = options.RawRepresentationFactory!(null!);
        var second = options.RawRepresentationFactory!(null!);

        Assert.NotSame(first, second);
    }
}
