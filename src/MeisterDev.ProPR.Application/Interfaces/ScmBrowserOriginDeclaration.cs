// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Credential-free native browser origins consumed by the host's existing origin policy.</summary>
public record ScmBrowserOriginDeclaration(
    ScmProvider Provider,
    IReadOnlyList<string> AllowedOrigins,
    IReadOnlyList<string> AllowedHostSuffixes);
