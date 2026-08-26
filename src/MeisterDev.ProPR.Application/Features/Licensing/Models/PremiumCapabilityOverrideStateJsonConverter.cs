// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Reads and writes <see cref="PremiumCapabilityOverrideState" /> as its camelCase name, which is the form
///     the emitted contract declares.
///     <para>
///         Anything else is refused with a message naming the states an override can carry, so that a caller
///         still sending the removed enable state is told what to send instead. That covers the number a state
///         is stored as well as its name: the general enum converter accepts the numeric form, which would let
///         <c>1</c> arrive as a value the contract does not declare. Its refusal also names the .NET type the
///         value failed to convert to rather than what the endpoint accepts.
///     </para>
///     <para>
///         The read path is the strict one, and it is the reason the converter exists. The write path is lenient:
///         a value the enum does not define is written as the default name rather than refused, because a write
///         happens while a response body is already being produced.
///     </para>
///     <para>
///         Registered in the serializer options ahead of the general enum converter, since the first converter
///         that accepts a type handles it. For the states the enum defines both write the same camelCase names.
///     </para>
/// </summary>
public sealed class PremiumCapabilityOverrideStateJsonConverter : JsonConverter<PremiumCapabilityOverrideState>
{
    private static readonly PremiumCapabilityOverrideState[] States = Enum.GetValues<PremiumCapabilityOverrideState>();

    private static readonly string AcceptedStates = string.Join(", ", States.Select(ToWireName));

    /// <inheritdoc />
    public override PremiumCapabilityOverrideState Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;

        if (value is not null)
        {
            foreach (var state in States)
            {
                if (string.Equals(value, ToWireName(state), StringComparison.OrdinalIgnoreCase))
                {
                    return state;
                }
            }
        }

        throw new JsonException($"An override state has to be one of: {AcceptedStates}.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        PremiumCapabilityOverrideState value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);

        // A value the enum does not define is written as the default name instead of being refused. Stored rows
        // still carry the removed enable state, and a refusal here would be raised after the response body had
        // begun, which leaves the caller with a truncated payload or a 500 rather than an answer it can read.
        // The default name is what the removed state amounts to now: it granted rather than withheld, and no
        // state in this build grants.
        var name = States.Contains(value)
            ? ToWireName(value)
            : ToWireName(PremiumCapabilityOverrideState.Default);

        writer.WriteStringValue(name);
    }

    private static string ToWireName(PremiumCapabilityOverrideState state)
    {
        return JsonNamingPolicy.CamelCase.ConvertName(state.ToString());
    }
}
