// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Controls metadata badges in published SCM finding comments.</summary>
public enum FindingBadgeStyle
{
    /// <summary>Uses flat-square images hosted by Shields.io.</summary>
    ShieldsIo = 0,

    /// <summary>Uses inline-code spans without external images.</summary>
    Plain = 1,
}
