// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Api.Features.Licensing.Controllers;
using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Api.Tests.Features.Licensing;

/// <summary>
///     What the override endpoint's payload accepts, read through the serializer configuration the API applies to
///     its own requests rather than a copy of it. An override can only take a capability away, so the enable state
///     has to be unrepresentable on the wire rather than refused after it has bound.
/// </summary>
public sealed class LicensingOverrideContractTests
{
    private static readonly JsonSerializerOptions ApiOptions = CreateApiOptions();

    [Theory]
    [InlineData("default", PremiumCapabilityOverrideState.Default)]
    [InlineData("disabled", PremiumCapabilityOverrideState.Disabled)]
    public void PatchPayload_WithAnOverrideStateTheEndpointAccepts_BindsIt(
        string wireValue,
        PremiumCapabilityOverrideState expectedState)
    {
        var payload = Deserialize($$"""{"capabilityOverrides":[{"key":"mention-answering","overrideState":"{{wireValue}}"}]}""");

        Assert.NotNull(payload.CapabilityOverrides);
        var overrideRequest = Assert.Single(payload.CapabilityOverrides);
        Assert.Equal("mention-answering", overrideRequest.Key);
        Assert.Equal(expectedState, overrideRequest.OverrideState);
    }

    [Fact]
    public void PatchPayload_WithTheEnableState_IsRefusedAndNamesTheStatesItAccepts()
    {
        var refusal = Assert.Throws<JsonException>(() =>
            Deserialize("""{"capabilityOverrides":[{"key":"mention-answering","overrideState":"enabled"}]}"""));

        Assert.Contains("default", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(PremiumCapabilityOverrideState), refusal.Message, StringComparison.Ordinal);
    }

    // The refusal is written for whoever sent the request, so it names the states rather than the .NET type the
    // value failed to convert to.
    [Fact]
    public void PatchPayload_WithAnUnknownOverrideState_IsRefusedTheSameWay()
    {
        var refusal = Assert.Throws<JsonException>(() =>
            Deserialize("""{"capabilityOverrides":[{"key":"mention-answering","overrideState":"forced"}]}"""));

        Assert.Contains("default", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", refusal.Message, StringComparison.Ordinal);
    }

    // The states are stored as numbers, and the general enum converter would take a number as one. Only the names
    // the contract declares are accepted, so neither the enable state's number nor any other arrives that way.
    [Theory]
    [InlineData("1")]
    [InlineData("\"1\"")]
    [InlineData("2")]
    public void PatchPayload_WithAnOverrideStateInItsNumericForm_IsRefusedAndNamesTheStatesItAccepts(string wireValue)
    {
        var refusal = Assert.Throws<JsonException>(() =>
            Deserialize($$"""{"capabilityOverrides":[{"key":"mention-answering","overrideState":{{wireValue}}}]}"""));

        Assert.Contains("default", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOverrideState_IsWrittenAsItsCamelCaseName()
    {
        var json = JsonSerializer.Serialize(PremiumCapabilityOverrideState.Disabled, ApiOptions);

        Assert.Equal("\"disabled\"", json);
    }

    private static PatchLicensingOverridesRequest Deserialize(string json)
    {
        var payload = JsonSerializer.Deserialize<PatchLicensingOverridesRequest>(json, ApiOptions);
        Assert.NotNull(payload);

        return payload;
    }

    /// <summary>
    ///     The serializer configuration the API installs on its controllers, so that the converter order these
    ///     tests rely on is the order requests are actually read with.
    /// </summary>
    private static JsonSerializerOptions CreateApiOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Program.ConfigureJsonSerialization(options);

        return options;
    }
}
