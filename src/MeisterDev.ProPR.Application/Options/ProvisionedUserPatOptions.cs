// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Options;

/// <summary>The lifetime limit for personal access tokens owned by provisioned users.</summary>
public sealed class ProvisionedUserPatOptions
{
    /// <summary>The installation setting that limits the lifetime in days.</summary>
    public const string MaximumDaysKey = "MEISTER_PROVISIONED_USER_PAT_MAX_DAYS";

    /// <summary>The maximum lifetime in days, or null when the installation imposes no limit.</summary>
    public int? MaximumDays { get; set; }
}
