// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Customer review availability independent of crawler activation.</summary>
public enum ReviewTargetLifecycle
{
    Enabled = 0,
    Disabled = 1,
    Removed = 2,
}
