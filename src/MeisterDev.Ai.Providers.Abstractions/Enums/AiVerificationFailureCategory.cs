// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     Normalized failure categories for provider verification and runtime diagnostics.
/// </summary>
/// <remarks>
///     <see cref="Unknown" /> holds zero so an uninitialized value, and a stored value this build cannot resolve,
///     read as an uncategorized failure. With a specific category at zero they would read as that category and
///     send an operator to fix something the provider never complained about. The member names are what persists
///     and what the published schema carries, so the numbering is internal.
/// </remarks>
public enum AiVerificationFailureCategory
{
    /// <summary>The cause is not known, or a stored category this build cannot resolve.</summary>
    Unknown = 0,

    /// <summary>Authentication credentials are missing or invalid.</summary>
    Credentials = 1,

    /// <summary>The configured endpoint could not be reached.</summary>
    EndpointReachability = 2,

    /// <summary>The provider rejected the caller because of authorization or permission issues.</summary>
    Authorization = 3,

    /// <summary>The provider returned a rejection that does not fit a more specific category.</summary>
    ProviderRejected = 4,

    /// <summary>The selected model or binding does not satisfy required capabilities.</summary>
    CapabilityMismatch = 5,
}
