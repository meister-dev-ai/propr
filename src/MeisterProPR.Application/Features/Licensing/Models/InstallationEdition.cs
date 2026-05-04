// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Licensing.Models;

/// <summary>Customer-facing product edition for one ProPR installation.</summary>
public enum InstallationEdition
{
    /// <summary>Community edition.</summary>
    Community = 0,

    /// <summary>Commercial edition.</summary>
    Commercial = 1,
}
