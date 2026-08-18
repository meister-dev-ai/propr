// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>Singleton row holding the usage-statistics preference, consent state and send history.</summary>
public sealed class UsageStatisticsSettingsRecord
{
    /// <summary>Fixed key. The setting is installation-wide.</summary>
    public int Id { get; set; }

    /// <summary>
    ///     The community toggle. Defaults to on, and is not changed when a license is installed or removed, so
    ///     returning to the community edition restores the preference the operator last set.
    /// </summary>
    public bool CommunityOptIn { get; set; }

    /// <summary>
    ///     When an administrator was first able to see what is sent. Null means nothing may be sent, which is
    ///     the initial state of both a fresh install and an upgraded one.
    /// </summary>
    public DateTimeOffset? ConsentGateSatisfiedAt { get; set; }

    /// <summary>When the consent notice was dismissed. Dismissal hides the notice; it changes nothing else.</summary>
    public DateTimeOffset? NoticeDismissedAt { get; set; }

    /// <summary>When a send was last attempted, successful or not.</summary>
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>Whether that attempt reached the receiver.</summary>
    public bool? LastAttemptSucceeded { get; set; }

    /// <summary>A short operator-facing description of the last outcome.</summary>
    public string? LastAttemptDetail { get; set; }

    /// <summary>When a snapshot last reached the receiver. Also the start of the next observation window.</summary>
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>The newest release the receiver reported.</summary>
    public string? LatestVersion { get; set; }

    /// <summary>
    ///     Security advisories from the most recent successful ping, as the JSON array the receiver sent.
    ///     <para>
    ///         Stored whole rather than normalised into rows, because it is display material with no history
    ///         and nothing queries it. Replacing the whole value keeps it matching the last response.
    ///     </para>
    /// </summary>
    public string? AdvisoriesJson { get; set; }

    /// <summary>When the version and advisory information arrived.</summary>
    public DateTimeOffset? UpdateInformationReceivedAt { get; set; }

    /// <summary>When this row last changed.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The user who last changed the toggle, when the change came from a person.</summary>
    public Guid? UpdatedByUserId { get; set; }
}
