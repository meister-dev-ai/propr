// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Identifies a provider throttle refusal.
/// </summary>
/// <remarks>
///     The typed refusal preserves rich-read classification and the legacy controlled failure response.
/// </remarks>
internal sealed class ProviderThrottledException : InvalidOperationException
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
