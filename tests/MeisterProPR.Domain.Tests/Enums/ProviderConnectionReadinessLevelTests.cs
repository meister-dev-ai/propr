// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Tests.Enums;

public sealed class ProviderConnectionReadinessLevelTests
{
    [Fact]
    public void ProviderConnectionReadinessLevel_DefinesExpectedOrderedValues()
    {
        Assert.Equal(0, (int)ProviderConnectionReadinessLevel.Unknown);
        Assert.Equal(1, (int)ProviderConnectionReadinessLevel.Configured);
        Assert.Equal(2, (int)ProviderConnectionReadinessLevel.Degraded);
        Assert.Equal(3, (int)ProviderConnectionReadinessLevel.OnboardingReady);
        Assert.Equal(4, (int)ProviderConnectionReadinessLevel.WorkflowComplete);
    }
}
