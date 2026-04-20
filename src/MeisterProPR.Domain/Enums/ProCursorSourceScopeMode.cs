// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Determines which ProCursor sources are available to one crawl configuration.
/// </summary>
public enum ProCursorSourceScopeMode
{
    /// <summary>
    ///     All sources available to the client are included in the crawl configuration.
    /// </summary>
    AllClientSources = 0,

    /// <summary>
    ///     Only selected sources are included in the crawl configuration.
    /// </summary>
    SelectedSources = 1,
}
