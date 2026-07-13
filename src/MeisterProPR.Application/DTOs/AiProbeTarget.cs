// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Provider-neutral description of a probe/verify target, passed to <c>IAiProviderDriver.ValidateProbeTarget</c>
///     so each provider driver can enforce its own base-URL, egress, and auth-shape rules without the controller
///     branching on provider kind.
/// </summary>
/// <param name="BaseUrl">The connection base URL to probe.</param>
/// <param name="AuthMode">The configured authentication mode.</param>
/// <param name="HasApiKey">Whether a non-empty API key was supplied.</param>
public sealed record AiProbeTarget(string BaseUrl, AiAuthMode AuthMode, bool HasApiKey);
