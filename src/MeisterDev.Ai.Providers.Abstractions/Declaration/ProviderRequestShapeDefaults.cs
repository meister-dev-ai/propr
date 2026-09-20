// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     The host-owned constraints a refused request parameter tightens. The rules are the host's; which wire
///     parameter name maps onto one is the family's, because the spelling differs per vendor.
/// </summary>
public enum ProviderRequestShapeConstraint
{
    /// <summary>The endpoint serves this model only as a stream.</summary>
    Streaming = 0,

    /// <summary>The endpoint refuses a request that asks for the response to be stored provider-side.</summary>
    StoredOutput = 1,

    /// <summary>The endpoint refuses a system-role message and the instruction has to be carried another way.</summary>
    SystemRole = 2,

    /// <summary>The model refuses a sampling temperature.</summary>
    Temperature = 3,

    /// <summary>The model refuses an output-token ceiling under the name the request used.</summary>
    MaxOutputTokens = 4,
}

/// <summary>
///     What a family knows about the shape of a request its endpoint accepts, stated once so a new connection
///     starts with it.
/// </summary>
/// <remarks>
///     <para>
///         A declared default seeds the host's request-shape state once, when the connection is created, and
///         never again. An operator's own override is sticky and wins over it, so upgrading a family does not
///         revert a fix an operator made. What the host learns from a refusal may tighten a value the operator
///         never set, and records that it did.
///     </para>
///     <para>
///         A null leaves a constraint unstated, which is different from stating that the endpoint has no such
///         constraint: an unstated constraint seeds nothing and leaves the host's own default in place.
///     </para>
/// </remarks>
/// <param name="RequiresStreaming">The endpoint serves only streamed responses.</param>
/// <param name="RefusesStoredOutput">The endpoint refuses a request that asks for provider-side storage.</param>
/// <param name="RefusesSystemRole">The endpoint refuses a system-role message.</param>
/// <param name="AcceptsTemperature">The endpoint's models accept a sampling temperature.</param>
/// <param name="AcceptsMaxOutputTokens">The endpoint's models accept an output-token ceiling.</param>
/// <param name="RefusedParameterNames">
///     The vendor's own spelling for each parameter whose refusal the host should read as tightening a
///     constraint — <c>max_completion_tokens</c> for one family, <c>max_tokens</c> for another.
/// </param>
public sealed record ProviderRequestShapeDefaults(
    bool? RequiresStreaming = null,
    bool? RefusesStoredOutput = null,
    bool? RefusesSystemRole = null,
    bool? AcceptsTemperature = null,
    bool? AcceptsMaxOutputTokens = null,
    IReadOnlyDictionary<string, ProviderRequestShapeConstraint>? RefusedParameterNames = null)
{
    // Held in a field so the copy is made wherever the map is set: a positional argument, an object initializer
    // and a `with` expression all reach the property, and a property with an accessor body cannot carry a field
    // initializer of its own.
    private readonly IReadOnlyDictionary<string, ProviderRequestShapeConstraint>? _refusedParameterNames =
        Held(RefusedParameterNames);

    /// <summary>A family that states nothing about the shape of a request its endpoint accepts.</summary>
    public static ProviderRequestShapeDefaults Unstated { get; } = new();

    /// <summary>
    ///     The vendor's own spelling for each parameter whose refusal the host should read as tightening a
    ///     constraint, or null when the family names none.
    /// </summary>
    public IReadOnlyDictionary<string, ProviderRequestShapeConstraint>? RefusedParameterNames
    {
        get => this._refusedParameterNames;
        init => this._refusedParameterNames = Held(value);
    }

    /// <summary>The vendor spellings this family maps onto host-owned constraints; empty when it names none.</summary>
    public IReadOnlyDictionary<string, ProviderRequestShapeConstraint> RefusedParameters =>
        this.RefusedParameterNames ?? ImmutableDictionary<string, ProviderRequestShapeConstraint>.Empty;

    // Copied into an immutable map, because this type reaches the host as part of a family's declaration, which
    // is a process-wide instance every add-in can reach: a read-only dictionary over a Dictionary is cast back to
    // it in one line. Ordinal, matching what the empty case has always used, so a lookup answers the same way
    // whether the family named parameters or not.
    private static IReadOnlyDictionary<string, ProviderRequestShapeConstraint>? Held(IReadOnlyDictionary<string, ProviderRequestShapeConstraint>? value)
    {
        return value?.ToImmutableDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    }
}
