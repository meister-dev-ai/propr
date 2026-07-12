// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Builds the <see cref="SessionPolicy" /> from configuration, applying defaults when the keys are
///     absent. Session lifetimes are operator-tunable without a rebuild via
///     <c>MEISTER_SESSION_IDLE_MINUTES</c> and <c>MEISTER_SESSION_ABSOLUTE_HOURS</c>.
/// </summary>
public static class SessionPolicyFactory
{
    /// <summary>Configuration key for the idle timeout, in minutes.</summary>
    public const string IdleMinutesKey = "MEISTER_SESSION_IDLE_MINUTES";

    /// <summary>Configuration key for the absolute session lifetime, in hours.</summary>
    public const string AbsoluteHoursKey = "MEISTER_SESSION_ABSOLUTE_HOURS";

    /// <summary>Default idle timeout: eight hours (a working day away ends the session).</summary>
    public const int DefaultIdleMinutes = 480;

    /// <summary>Default absolute lifetime: three days from sign-in.</summary>
    public const int DefaultAbsoluteHours = 72;

    /// <summary>
    ///     Reads the session policy from configuration. Both values must be positive; a non-positive or
    ///     unparsable value is a configuration error and throws.
    /// </summary>
    public static SessionPolicy FromConfiguration(IConfiguration configuration)
    {
        var idleMinutes = ReadPositiveInt(configuration, IdleMinutesKey, DefaultIdleMinutes);
        var absoluteHours = ReadPositiveInt(configuration, AbsoluteHoursKey, DefaultAbsoluteHours);

        return new SessionPolicy
        {
            IdleTimeout = TimeSpan.FromMinutes(idleMinutes),
            AbsoluteLifetime = TimeSpan.FromHours(absoluteHours),
        };
    }

    private static int ReadPositiveInt(IConfiguration configuration, string key, int fallback)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        if (!int.TryParse(raw, out var value) || value <= 0)
        {
            throw new InvalidOperationException($"{key} must be a positive integer when set.");
        }

        return value;
    }
}
