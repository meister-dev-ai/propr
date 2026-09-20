// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.BedrockAddIn.Tests;

/// <summary>
///     How a stored Bedrock profile turns into the credential the AWS SDK signs with.
/// </summary>
/// <remarks>
///     Two stored shapes reach this. The current one is the named fields the family declares, which the host
///     hands over as declared values. Before those existed a credential was one string, and a Bedrock profile
///     packed the pair into it with a colon; those rows are still in service and are not rewritten, so both
///     shapes have to resolve to the same credential.
/// </remarks>
public sealed class BedrockEndpointResolutionTests
{
    private const string ApiKey = "ABSKQmVkcm9ja0FQSUtleS1FWEFNUExF";


    [Fact]
    public void AnApiKeyCountsAsACredential()
    {
        Assert.True(BedrockEndpointResolution.HasCredential(Packed(BedrockProviderDriver.ApiKeyAuth, ApiKey)));
    }

    [Theory]
    [InlineData("s3.eu-central-1.amazonaws.com", false)]
    [InlineData("sts.amazonaws.com", false)]
    [InlineData("bedrock-runtime.eu-central-1.amazonaws.com", true)]
    [InlineData("bedrock.eu-central-1.amazonaws.com", true)]

    // An interface VPC endpoint puts the endpoint id in front of the service label. BedrockClientFactory
    // handles this form as a PrivateLink ServiceURL, so refusing it here would reject an address the factory
    // goes on to support, and the traffic an operator put on PrivateLink could never be configured.
    [InlineData("vpce-0a1b2c3d-abcdefgh.bedrock-runtime.eu-central-1.vpce.amazonaws.com", true)]
    [InlineData("vpce-0a1b2c3d-abcdefgh.s3.eu-central-1.vpce.amazonaws.com", false)]
    public void OnlyABedrockServiceHostCounts(string host, bool expected)
    {
        Assert.Equal(expected, BedrockEndpointResolution.IsBedrockHost(host));
        Assert.True(BedrockEndpointResolution.IsAwsHost(host));
    }

    // The region sits before 'vpce' on an interface endpoint and before 'amazonaws' on a public one.
    [Theory]
    [InlineData("bedrock-runtime.eu-central-1.amazonaws.com", "eu-central-1")]
    [InlineData("vpce-0a1b2c3d-abcdefgh.bedrock-runtime.eu-central-1.vpce.amazonaws.com", "eu-central-1")]
    public void TheRegionIsReadFromEitherHostForm(string host, string expected)
    {
        Assert.Equal(expected, BedrockEndpointResolution.RegionFromHost(host));
    }

    [Fact]
    public void AHostAndAParameterNamingDifferentRegionsIsAConflict()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/awsBedrock",
            "https://bedrock-runtime.eu-central-1.amazonaws.com",
            BedrockProviderDriver.ApiKeyAuth,
            DefaultQueryParams: new Dictionary<string, string> { ["region"] = "us-east-1" });

        Assert.Equal(("eu-central-1", "us-east-1"), BedrockEndpointResolution.DescribeRegionConflict(endpoint));
    }

    [Fact]
    public void AParameterAgreeingWithTheHostIsNoConflict()
    {
        var endpoint = new ProviderEndpoint(
            "meisterdev/awsBedrock",
            "https://bedrock-runtime.eu-central-1.amazonaws.com",
            BedrockProviderDriver.ApiKeyAuth,
            DefaultQueryParams: new Dictionary<string, string> { ["region"] = "eu-central-1" });

        Assert.Null(BedrockEndpointResolution.DescribeRegionConflict(endpoint));
    }

    private static ProviderEndpoint Endpoint(string mode, IReadOnlyDictionary<string, string> declaredValues)
    {
        return new ProviderEndpoint(
            "meisterdev/awsBedrock",
            "https://bedrock-runtime.eu-central-1.amazonaws.com",
            mode)
        {
            DeclaredValues = declaredValues,
        };
    }

    private static ProviderEndpoint Packed(string mode, string secret)
    {
        return new ProviderEndpoint(
            "meisterdev/awsBedrock",
            "https://bedrock-runtime.eu-central-1.amazonaws.com",
            mode,
            secret);
    }
}
