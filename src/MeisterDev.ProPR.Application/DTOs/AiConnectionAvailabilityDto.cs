// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Whether a connection profile can be used as it is stored.</summary>
public enum AiConnectionAvailabilityState
{
    /// <summary>Every stored value resolved and the tenant permits the provider family.</summary>
    Available = 0,

    /// <summary>Something stands in the way of using the profile as it is stored.</summary>
    Unavailable = 1,
}

/// <summary>Why a connection profile cannot be used as it is stored.</summary>
/// <remarks>
///     The reasons stay distinct because their remedies do: a family that is not installed is fixed by
///     installing it, a family the tenant does not permit by amending the family allow-list, an endpoint the
///     tenant does not permit by amending the endpoint allow-list, and a stored value this build cannot read by
///     correcting the row. One state for all four would send an operator to the wrong one.
/// </remarks>
public enum AiConnectionUnavailableReason
{
    /// <summary>The stored provider identity names no family this build has.</summary>
    ProviderFamilyAbsent = 0,

    /// <summary>The family is present, and the tenant's allow-list does not permit it.</summary>
    ProviderFamilyNotPermitted = 1,

    /// <summary>The family is usable, and the profile holds a stored value this build cannot resolve.</summary>
    StoredValueUnresolved = 2,

    /// <summary>
    ///     The family is permitted, and the tenant's endpoint allow-list does not permit where this profile's
    ///     traffic would go: its own base URL, or a host its provider family declares it reaches.
    /// </summary>
    EndpointNotPermitted = 3,
}

/// <summary>Whether a connection profile can be used, and what stands in the way when it cannot.</summary>
/// <param name="State">Whether the profile can be used.</param>
/// <param name="Reason">What stands in the way, or <see langword="null" /> when the profile can be used.</param>
/// <param name="ProviderIdentity">
///     The provider identity the reason is about, or <see langword="null" /> when the reason is not about the
///     provider family. Carried separately because a family this build does not have cannot be reported in
///     <see cref="AiConnectionDto.ProviderKind" />, and an operator installing or permitting one needs the key
///     as it is stored.
/// </param>
/// <param name="UnresolvedValues">
///     The stored values this build could not resolve, all of them rather than the first, because each is a
///     separate thing to correct.
/// </param>
public sealed record AiConnectionAvailabilityDto(
    AiConnectionAvailabilityState State,
    AiConnectionUnavailableReason? Reason,
    string? ProviderIdentity,
    IReadOnlyList<AiUnresolvedValueDto> UnresolvedValues)
{
    /// <summary>A profile with nothing standing in the way of its use.</summary>
    public static AiConnectionAvailabilityDto Available { get; } =
        new(AiConnectionAvailabilityState.Available, null, null, []);
}
