// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     A host name the installation has been observed running on.
///     <para>
///         The rows form a set, so several replicas of one installation accumulate rather than reporting each
///         other as a change.
///     </para>
/// </summary>
public sealed class LicensingReplicaHostnameRecord
{
    /// <summary>The host name, which is also the key.</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>When this host name was first observed.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When it was last observed.</summary>
    public DateTimeOffset LastSeenAt { get; set; }
}
