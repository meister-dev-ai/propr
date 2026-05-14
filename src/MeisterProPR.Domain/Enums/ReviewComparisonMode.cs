// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Controls whether one or more review strategies execute against a PR snapshot.</summary>
public enum ReviewComparisonMode
{
    /// <summary>Execute one resolved strategy.</summary>
    Single,

    /// <summary>Execute multiple strategies and retain comparable outputs.</summary>
    SideBySideInternal,

    /// <summary>Execute a non-posting comparison strategy beside the publishing strategy.</summary>
    Shadow,

    /// <summary>Execute against a preserved snapshot or fixture without live publication.</summary>
    Replay,
}
