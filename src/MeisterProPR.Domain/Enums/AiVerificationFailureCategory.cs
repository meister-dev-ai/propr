// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Normalized failure categories for provider verification and runtime diagnostics.
/// </summary>
public enum AiVerificationFailureCategory
{
    /// <summary>Authentication credentials are missing or invalid.</summary>
    Credentials = 0,

    /// <summary>The configured endpoint could not be reached.</summary>
    EndpointReachability = 1,

    /// <summary>The provider rejected the caller because of authorization or permission issues.</summary>
    Authorization = 2,

    /// <summary>The provider returned a rejection that does not fit a more specific category.</summary>
    ProviderRejected = 3,

    /// <summary>The selected model or binding does not satisfy required capabilities.</summary>
    CapabilityMismatch = 4,

    /// <summary>An unknown error occurred.</summary>
    Unknown = 5,
}
