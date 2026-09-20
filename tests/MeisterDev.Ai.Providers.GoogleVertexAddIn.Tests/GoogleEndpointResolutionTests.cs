// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.GoogleVertexAddIn.Tests;

/// <summary>
///     Covers the address a call is made to, which on Vertex carries the project and the location and on the
///     Gemini API carries neither.
/// </summary>
public sealed class GoogleEndpointResolutionTests
{
    private const string Project = "168253061309";

    // The location a Vertex host names is the location the model is addressed under, so a regional connection
    // reaches the region it states.
    [Fact]
    public void AHostNamingALocationAddressesTheModelUnderThatLocation()
    {
        Assert.Equal(
            "https://europe-west4-aiplatform.googleapis.com/v1/projects/168253061309/locations/europe-west4"
            + "/publishers/google/models/gemini-2.5-flash:generateContent",
            Uri(VertexOn("https://europe-west4-aiplatform.googleapis.com"), "gemini-2.5-flash"));
    }

    // A host naming no location is the global endpoint, which Vertex addresses as the location "global". Google
    // serves its newest models there only.
    [Fact]
    public void AHostNamingNoLocationAddressesTheModelGlobally()
    {
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/168253061309/locations/global"
            + "/publishers/google/models/gemini-3.8-flash:generateContent",
            Uri(VertexOn("https://aiplatform.googleapis.com"), "gemini-3.8-flash"));
    }

    // A model id is written the way the surface addresses one, whichever host the connection names, so an
    // operator enters the id Google documents and nothing else.
    [Theory]
    [InlineData("https://europe-west4-aiplatform.googleapis.com", "europe-west4")]
    [InlineData("https://aiplatform.googleapis.com", "global")]
    public void ABareModelIdIsQualifiedOnEitherHost(string baseUrl, string location)
    {
        Assert.Contains(
            $"/locations/{location}/publishers/google/models/gemini-2.5-flash:generateContent",
            Uri(VertexOn(baseUrl), "gemini-2.5-flash"),
            StringComparison.Ordinal);
    }

    // The project is not in the host and cannot be derived from it, so a connection that states none cannot be
    // addressed at all and says so rather than building a URL that names no project.
    [Theory]
    [InlineData("https://europe-west4-aiplatform.googleapis.com")]
    [InlineData("https://aiplatform.googleapis.com")]
    public void AVertexConnectionStatingNoProjectCannotBeAddressed(string baseUrl)
    {
        var endpoint = new ProviderEndpoint(
            GoogleVertexProviderDriver.FamilyKey,
            baseUrl,
            GoogleVertexProviderDriver.GcpAdcAuth);

        var refusal = Assert.Throws<InvalidOperationException>(() => GoogleEndpointResolution.BuildModelUri(endpoint, "gemini-2.5-flash", "generateContent"));

        Assert.Contains("project", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    // The Gemini API addresses a model without a project or a location, so neither appears in its URL.
    [Fact]
    public void TheGeminiApiAddressesAModelWithoutAProjectOrALocation()
    {
        var endpoint = new ProviderEndpoint(
            GoogleVertexProviderDriver.FamilyKey,
            "https://generativelanguage.googleapis.com",
            GoogleVertexProviderDriver.ApiKeyAuth);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            GoogleEndpointResolution.BuildModelUri(endpoint, "gemini-2.5-flash", "generateContent").ToString());
    }

    // A host whose own label merely ends with the Vertex suffix is not the Vertex surface. Treating it as one
    // reads its leading characters as a location and builds an address against them.
    [Theory]
    [InlineData("https://aiplatform.googleapis.com", true)]
    [InlineData("https://europe-west4-aiplatform.googleapis.com", true)]
    [InlineData("https://us-central1-aiplatform.googleapis.com", true)]
    [InlineData("https://AIPLATFORM.GOOGLEAPIS.COM", true)]
    [InlineData("https://notaiplatform.googleapis.com", false)]
    [InlineData("https://xaiplatform.googleapis.com", false)]
    [InlineData("https://generativelanguage.googleapis.com", false)]
    [InlineData("https://aiplatform.googleapis.com.example.invalid", false)]
    public void OnlyTheGlobalHostAndARegionalOneAreTheVertexSurface(string baseUrl, bool expected)
    {
        Assert.Equal(expected, GoogleEndpointResolution.IsVertex(baseUrl));
    }

    [Theory]
    [InlineData("aiplatform.googleapis.com", null)]
    [InlineData("europe-west4-aiplatform.googleapis.com", "europe-west4")]
    [InlineData("EUROPE-WEST4-AIPLATFORM.GOOGLEAPIS.COM", "europe-west4")]
    [InlineData("notaiplatform.googleapis.com", null)]
    public void ALocationIsReadOnlyFromItsOwnLabel(string host, string? expected)
    {
        Assert.Equal(expected, GoogleEndpointResolution.LocationFromHost(host));
    }

    private static ProviderEndpoint VertexOn(string baseUrl)
    {
        return new ProviderEndpoint(
            GoogleVertexProviderDriver.FamilyKey,
            baseUrl,
            GoogleVertexProviderDriver.GcpAdcAuth)
        {
            DefaultQueryParams = new Dictionary<string, string>(StringComparer.Ordinal) { ["project"] = Project },
        };
    }

    private static string Uri(ProviderEndpoint endpoint, string remoteModelId)
    {
        return GoogleEndpointResolution.BuildModelUri(endpoint, remoteModelId, "generateContent").ToString();
    }
}
