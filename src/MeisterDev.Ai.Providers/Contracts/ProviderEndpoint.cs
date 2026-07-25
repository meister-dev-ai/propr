// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Everything a driver needs to reach a provider: where it is, how to authenticate, and any transport
///     defaults. The same shape serves configuration-time probing and runtime calls, because both answer the
///     same question. A host projects its own stored connection onto this.
/// </summary>
/// <param name="ProviderKind">Provider family that selects the driver.</param>
/// <param name="BaseUrl">Exact configured provider base URL.</param>
/// <param name="AuthMode">Authentication mode to use.</param>
/// <param name="Secret">Unprotected secret material for the chosen auth mode; never logged or serialized.</param>
/// <param name="DefaultHeaders">Optional headers appended to every request.</param>
/// <param name="DefaultQueryParams">Optional query parameters appended to every request.</param>
public sealed record ProviderEndpoint(
    AiProviderKind ProviderKind,
    string BaseUrl,
    AiAuthMode AuthMode,
    string? Secret = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null);
