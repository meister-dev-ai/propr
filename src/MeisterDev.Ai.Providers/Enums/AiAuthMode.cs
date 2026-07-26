// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Enums;

/// <summary>
///     Authentication modes supported by AI providers.
/// </summary>
/// <remarks>
///     A mode listed here is a mode the system can describe and store, not necessarily one a driver in this build
///     can use. Whether a mode is offered to an operator is decided by the drivers that are registered — see
///     <c>IAiProviderDriverRegistry.RegisteredKinds</c> — so opening this enum ahead of a driver cannot produce a
///     configuration that fails only at review time.
/// </remarks>
public enum AiAuthMode
{
    /// <summary>Authenticate with a provider-specific API key.</summary>
    ApiKey = 0,

    /// <summary>Authenticate with Azure identity credentials.</summary>
    AzureIdentity = 1,

    /// <summary>
    ///     Authenticate with an <c>x-api-key</c> header rather than a bearer token. Anthropic's own API expects
    ///     the key in that header and rejects <c>Authorization</c>, so the difference is a wire-level one rather
    ///     than a naming preference.
    /// </summary>
    XApiKey = 2,

    /// <summary>
    ///     Authenticate with AWS Signature Version 4. The credential is three fields rather than one — access key
    ///     id, secret access key, and an optional session token — which is why the stored credential is an
    ///     envelope; see <see cref="Contracts.ProviderSecretEnvelope" />.
    /// </summary>
    SigV4 = 3,

    /// <summary>
    ///     Authenticate with Google Application Default Credentials: either the ambient credentials of the host
    ///     the product runs on, or a service-account JSON document supplied by the operator.
    /// </summary>
    GcpAdc = 4,
}
