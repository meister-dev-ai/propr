// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The installation's random identifier for anonymous usage statistics.
///     <para>
///         It has its own table so that deleting it starts a new identity without discarding the operator's
///         opt-out and the send history. The product has no regeneration command; deleting this row is the
///         documented way to report as a different installation.
///     </para>
/// </summary>
public sealed class UsageStatisticsIdentityRecord
{
    /// <summary>Fixed key. The identity is installation-wide.</summary>
    public int Id { get; set; }

    /// <summary>A locally generated random value. It is not derived from the host or the license.</summary>
    public Guid InstanceId { get; set; }

    /// <summary>When this identity was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
