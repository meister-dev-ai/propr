// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Selectable strategy used to generate review findings for a job.</summary>
public enum ReviewStrategy
{
    /// <summary>Current per-file review pipeline.</summary>
    FileByFile,

    /// <summary>Planned PR-wide staged agentic review pipeline.</summary>
    PrWideAgentic,
}
