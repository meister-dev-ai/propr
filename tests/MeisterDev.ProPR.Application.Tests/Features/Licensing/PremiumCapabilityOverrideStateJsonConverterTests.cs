// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

public sealed class PremiumCapabilityOverrideStateJsonConverterTests
{
    // Stored rows still carry the removed enable state, so a value the enum does not define can reach the write
    // path. A refusal there would come after the response body had begun, so such a value is written as a name
    // the contract declares instead.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Serialize_AnUndeclaredState_WritesTheDefaultName(int undeclaredValue)
    {
        var json = JsonSerializer.Serialize((PremiumCapabilityOverrideState)undeclaredValue, CreateOptions());

        Assert.Equal("\"default\"", json);
    }

    [Theory]
    [InlineData(PremiumCapabilityOverrideState.Default, "\"default\"")]
    [InlineData(PremiumCapabilityOverrideState.Disabled, "\"disabled\"")]
    public void Serialize_ADeclaredState_WritesItsCamelCaseName(
        PremiumCapabilityOverrideState state,
        string expectedJson)
    {
        var json = JsonSerializer.Serialize(state, CreateOptions());

        Assert.Equal(expectedJson, json);
    }

    // Reading stays strict in both forms the general enum converter would have accepted: the number a state is
    // stored as, and a name the contract does not declare.
    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("\"enabled\"")]
    [InlineData("\"forced\"")]
    public void Deserialize_AValueTheContractDoesNotDeclare_IsRefusedAndNamesTheStatesItAccepts(string json)
    {
        var refusal = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PremiumCapabilityOverrideState>(json, CreateOptions()));

        Assert.Contains("default", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", refusal.Message, StringComparison.Ordinal);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new PremiumCapabilityOverrideStateJsonConverter());

        return options;
    }
}
