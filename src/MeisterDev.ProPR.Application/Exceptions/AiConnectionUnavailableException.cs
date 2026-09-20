// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Thrown when a review resolves a model on a connection profile this host cannot serve, so no runtime is
///     built for it.
/// </summary>
/// <remarks>
///     A distinct type because this is a configuration problem and not a provider failure: nothing about the
///     profile changes by trying again, so it must not reach the classification that decides whether a call is
///     worth repeating. Derives from <see cref="InvalidOperationException" /> so callers that only care that
///     resolution failed keep working.
/// </remarks>
public sealed class AiConnectionUnavailableException : InvalidOperationException
{
    /// <summary>Initializes a new instance of the <see cref="AiConnectionUnavailableException" /> class.</summary>
    /// <param name="connection">The profile the review resolved to.</param>
    /// <param name="availability">What stands in the way of using it.</param>
    public AiConnectionUnavailableException(AiConnectionDto connection, AiConnectionAvailabilityDto availability)
        : base(BuildMessage(connection, availability))
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(availability);

        this.ConnectionId = connection.Id;
        this.Availability = availability;
    }

    /// <summary>The profile that cannot be used.</summary>
    public Guid ConnectionId { get; }

    /// <summary>What stands in the way of using the profile, as the profile reported it.</summary>
    public AiConnectionAvailabilityDto Availability { get; }

    private static string BuildMessage(AiConnectionDto connection, AiConnectionAvailabilityDto availability)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(availability);

        return $"The AI connection '{connection.DisplayName}' ({connection.Id}) cannot be used for a review "
               + $"because {DescribeReason(availability)}.";
    }

    private static string DescribeReason(AiConnectionAvailabilityDto availability)
    {
        var identity = string.IsNullOrWhiteSpace(availability.ProviderIdentity)
            ? "(blank)"
            : availability.ProviderIdentity;

        return availability.Reason switch
        {
            AiConnectionUnavailableReason.ProviderFamilyAbsent =>
                $"its provider family '{identity}' is not installed on this host",
            AiConnectionUnavailableReason.ProviderFamilyNotPermitted =>
                $"its provider family '{identity}' is not permitted for the tenant",
            _ => $"this build cannot resolve {DescribeUnresolvedValues(availability.UnresolvedValues)}",
        };
    }

    // Names every unresolved value, because each one is a separate thing to correct and an operator shown only
    // the first would fix it and meet the same refusal.
    private static string DescribeUnresolvedValues(IReadOnlyList<AiUnresolvedValueDto> unresolvedValues)
    {
        if (unresolvedValues.Count == 0)
        {
            return "a value stored on it";
        }

        return "its stored " + string.Join(
            ", ",
            unresolvedValues.Select(unresolved => $"{unresolved.Field} value '{unresolved.Value}'"));
    }
}
