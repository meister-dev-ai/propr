// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorCarryForwardVerificationTests
{
    [Fact]
    public async Task ReviewAsync_CarriedForwardResultsRemainOutOfVerificationScope()
    {
        var test = new FileByFileReviewOrchestratorTests();
        await test.ReviewAsync_CarriedForwardResults_DoNotContributePostingCandidates();
    }
}
