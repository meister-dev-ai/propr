// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     How a request address is built from a base URL that may already carry query parameters.
/// </summary>
public sealed class ProviderEndpointAddressTests
{
    [Fact]
    public void ThePathSuffixIsAppendedToTheBasePath()
    {
        Assert.Equal(
            "https://gateway.example.com/v1/models",
            ProviderEndpointAddress.For(Endpoint("https://gateway.example.com/v1"), "models").ToString());
    }

    [Fact]
    public void AQueryOnTheBaseUrlSurvives()
    {
        Assert.Equal(
            "https://gateway.example.com/v1/models?tenant=acme",
            ProviderEndpointAddress.For(Endpoint("https://gateway.example.com/v1?tenant=acme"), "models").ToString());
    }

    [Fact]
    public void ADeclaredDefaultIsAppendedAlongsideIt()
    {
        var address = ProviderEndpointAddress.For(
            Endpoint("https://gateway.example.com/v1?tenant=acme", new() { ["api-version"] = "2024-02-01" }),
            "models");

        Assert.Equal("https://gateway.example.com/v1/models?tenant=acme&api-version=2024-02-01", address.ToString());
    }

    [Fact]
    public void ADeclaredDefaultReplacesABaseUrlParameterOfTheSameName()
    {
        var address = ProviderEndpointAddress.For(
            Endpoint("https://gateway.example.com/v1?api-version=old", new() { ["api-version"] = "new" }),
            "models");

        Assert.Equal("https://gateway.example.com/v1/models?api-version=new", address.ToString());
    }

    // Asserted on AbsoluteUri because ToString unescapes for display. What matters is that the separator
    // inside a value stays encoded, so the value does not split into two parameters on the wire.
    [Fact]
    public void ADeclaredDefaultIsEscaped()
    {
        var address = ProviderEndpointAddress.For(
            Endpoint("https://gateway.example.com/v1", new() { ["route"] = "a b&c" }),
            "models");

        Assert.Equal("https://gateway.example.com/v1/models?route=a%20b%26c", address.AbsoluteUri);
    }

    private static ProviderEndpoint Endpoint(string baseUrl, Dictionary<string, string>? defaults = null)
    {
        return new ProviderEndpoint("meisterdev/openAi", baseUrl, "meisterdev/openAi:ApiKey", DefaultQueryParams: defaults);
    }
}
