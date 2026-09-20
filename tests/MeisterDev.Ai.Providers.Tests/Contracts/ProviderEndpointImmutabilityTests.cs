// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     An endpoint keeps what it was built with, whatever the caller does to the collections afterwards.
/// </summary>
/// <remarks>
///     An endpoint is handed to a provider family and held for the life of the client it builds, which is longer
///     than the work that built it: a review builds one client and calls it for as long as the review runs. A
///     caller that kept its own dictionary could otherwise rewrite the headers of a request already in flight.
/// </remarks>
public sealed class ProviderEndpointImmutabilityTests
{
    [Fact]
    public void RewritingTheDictionaryACallerPassedChangesNothingOnTheEndpoint()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Tenant"] = "first" };
        var query = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-version"] = "2026-01-01" };
        var declared = new Dictionary<string, string>(StringComparer.Ordinal) { ["project"] = "first" };

        var endpoint = new ProviderEndpoint("tests/family", "https://api.example.com/v1", "tests/family:ApiKey", "key", headers, query)
        {
            DeclaredValues = declared,
        };

        headers["X-Tenant"] = "second";
        headers["X-Added"] = "late";
        query["api-version"] = "2030-01-01";
        declared["project"] = "second";

        Assert.Equal("first", endpoint.DefaultHeaders!["X-Tenant"]);
        Assert.DoesNotContain("X-Added", endpoint.DefaultHeaders.Keys, StringComparer.Ordinal);
        Assert.Equal("2026-01-01", endpoint.DefaultQueryParams!["api-version"]);
        Assert.Equal("first", endpoint.DeclaredValues["project"]);
    }

    // A copy sets the property, not the field, so the copy has to make one too.
    [Fact]
    public void ACopyKeepsWhatItWasGivenAsWell()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Tenant"] = "first" };
        var endpoint = new ProviderEndpoint("tests/family", "https://api.example.com/v1", "tests/family:ApiKey")
            with
            {
                DefaultHeaders = headers
            };

        headers["X-Tenant"] = "second";

        Assert.Equal("first", endpoint.DefaultHeaders!["X-Tenant"]);
    }

    [Fact]
    public void AnEndpointGivenNoCollectionsReadsAsEmpty()
    {
        var endpoint = new ProviderEndpoint("tests/family", "https://api.example.com/v1", "tests/family:ApiKey");

        Assert.Null(endpoint.DefaultHeaders);
        Assert.Null(endpoint.DefaultQueryParams);
        Assert.Empty(endpoint.DeclaredValues);
    }
}
