// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     A provider asked the caller to slow down.
/// </summary>
/// <remarks>
///     Carried as its own type so a caller can tell "wait" from "no". Everything else a provider refuses is a
///     failure of that one call; this one says the next calls would fail as well.
/// </remarks>
internal sealed class ProviderThrottledException : Exception
{
    public ProviderThrottledException()
    {
    }

    public ProviderThrottledException(string message)
        : base(message)
    {
    }

    public ProviderThrottledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
