// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common.Compatibility;

namespace MeisterDev.ProPR.Application.Tests.Features.Providers;

public sealed class CanonicalSourceProjectionTests
{
    [Theory]
    [InlineData(null, "value")]
    [InlineData("github", null)]
    [InlineData(" ", "value")]
    [InlineData("github", "")]
    public void IncompleteCanonicalFieldsUseHistoricalRepositoryFallback(string? provider, string? value)
    {
        var result = CanonicalSourceProjection.Resolve(provider, value, "repository");
        Assert.Equal("azureDevOps", result.Provider);
        Assert.Equal("repository", result.Value);
    }

    [Fact]
    public void CompleteFieldsRemainUnchanged()
    {
        var result = CanonicalSourceProjection.Resolve("custom", " native ", "repository");
        Assert.Equal("custom", result.Provider);
        Assert.Equal(" native ", result.Value);
    }
}
