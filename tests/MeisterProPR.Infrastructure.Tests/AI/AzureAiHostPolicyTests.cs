// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AzureAiHostPolicyTests
{
    [Theory]
    [InlineData("myorg.openai.azure.com")]
    [InlineData("myorg.services.ai.azure.com")]
    [InlineData("myorg.cognitiveservices.azure.com")]
    [InlineData("MYORG.OPENAI.AZURE.COM")]
    [InlineData("private.eastus.openai.azure.com")]
    [InlineData("myorg.openai.azure.com.")] // trailing FQDN dot normalized
    public void IsAzureAiHost_ForAzureHost_ReturnsTrue(string host)
    {
        Assert.True(AzureAiHostPolicy.IsAzureAiHost(host));
    }

    [Theory]
    [InlineData("evil.openai.azure.com.attacker.com")] // suffix is not the true suffix
    [InlineData("openai.azure.com.evil.com")]
    [InlineData("xopenai.azure.com")] // missing the leading dot boundary
    [InlineData("openai.azure.com")] // apex, no subdomain
    [InlineData("internal.corp.example")]
    [InlineData("evil.com")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAzureAiHost_ForNonAzureHost_ReturnsFalse(string? host)
    {
        Assert.False(AzureAiHostPolicy.IsAzureAiHost(host));
    }
}
