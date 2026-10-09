// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Crawling.Configuration;

/// <summary>Identifies whether a configuration update succeeded, was unavailable, or conflicted with protected state.</summary>
public enum CrawlConfigurationUpdateResult
{
    /// <summary>The configuration was missing or did not belong to the requested owner.</summary>
    NotFound = 0,

    /// <summary>The update was persisted.</summary>
    Updated = 1,

    /// <summary>The current lifecycle or required admission protection prevented the update.</summary>
    Conflict = 2,
}
