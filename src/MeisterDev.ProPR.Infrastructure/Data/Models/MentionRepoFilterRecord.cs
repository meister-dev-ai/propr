// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One repository a mention configuration answers on.
/// </summary>
/// <remarks>
///     Keyed on the provider-native repository id rather than the display name the crawl filter matches on.
///     At the point the filter is applied the scan holds only each pull request's repository id, so an id is
///     the only key it can evaluate without widening that contract, and it goes on matching after somebody
///     renames the repository.
/// </remarks>
public sealed class MentionRepoFilterRecord
{
    /// <summary>Unique identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>The configuration this filter belongs to.</summary>
    public Guid MentionConfigurationId { get; set; }

    /// <summary>Provider-native repository identifier. What the scan matches on.</summary>
    public string RepositoryId { get; set; } = string.Empty;

    /// <summary>Provider key backing the guided repository selection, when one was used.</summary>
    public string? SourceProvider { get; set; }

    /// <summary>Provider-aware canonical source reference for the selected repository, when available.</summary>
    public string? CanonicalSourceRef { get; set; }

    /// <summary>Human-readable repository name, kept for display only. Never matched on.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    ///     When this repository was claimed. Comments published before it are never answered.
    /// </summary>
    /// <remarks>
    ///     The provider lists every open pull request regardless of age, and a repository claimed for the
    ///     first time has no scan watermark yet, so without a floor the first scan would answer every
    ///     question ever asked in every open pull request and bill for all of them. Claiming a repository
    ///     says what happens from now on, not what should have happened.
    /// </remarks>
    public DateTimeOffset ClaimedAt { get; set; }

    /// <summary>Owning configuration.</summary>
    public MentionConfigurationRecord? MentionConfiguration { get; set; }
}
