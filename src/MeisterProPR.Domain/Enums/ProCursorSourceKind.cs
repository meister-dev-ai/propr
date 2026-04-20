// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Supported git-backed ProCursor knowledge-source kinds.
/// </summary>
public enum ProCursorSourceKind
{
    /// <summary>
    ///     Git repository source.
    /// </summary>
    Repository = 0,

    /// <summary>
    ///     Azure DevOps wiki source.
    /// </summary>
    AdoWiki = 1,
}
