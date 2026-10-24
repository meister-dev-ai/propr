// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiReviewOptionsVerificationReasoningTests
{
    [Theory]
    [InlineData("medium", ReviewReasoningEffort.Medium)]
    [InlineData("High", ReviewReasoningEffort.High)]
    [InlineData("none", ReviewReasoningEffort.None)]
    [InlineData("9", ReviewReasoningEffort.None)]
    [InlineData("deep", ReviewReasoningEffort.None)]
    [InlineData(null, ReviewReasoningEffort.None)]
    public void Bind_ReadsTheVerificationReasoningEffortAndKeepsTheDefaultForAnUnknownValue(string? value, ReviewReasoningEffort expected)
    {
        var options = new AiReviewOptions();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AI_VERIFICATION_REASONING_EFFORT"] = value })
            .Build();

        AiReviewOptionsBinder.Bind(options, configuration);

        Assert.Equal(expected, options.VerificationReasoningEffort);
    }
}
