// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The index of findings ProPR has already posted on a pull request, so a later review increment can
///     recognise a concern it has raised before and keep it off the pull request a second time.
/// </summary>
/// <remarks>
///     Deliberately a corpus of its own rather than a widening of thread memory. Thread memory is written only
///     when a human resolves a thread, and only when the resolution is corroborated, so a finding that is still
///     open has no memory record at all. That is precisely the window in which duplicates appear.
///     <para>
///         Rows are written once per review job, after that job finishes publishing. A lookup therefore only
///         ever sees earlier jobs, which is what makes this check strictly cross-increment.
///     </para>
///     <para>Neither method throws: duplicate protection degrades, it never fails a review.</para>
/// </remarks>
public interface IPostedFindingIndex
{
    /// <summary>
    ///     Looks the candidate finding up against findings earlier increments already posted on this pull
    ///     request. The comparison is on the finding text alone, with no file, no line and no severity in the
    ///     key, because all three have been observed drifting between increments while the concern stayed the
    ///     same.
    /// </summary>
    Task<PostedFindingMatchDto> FindDuplicateAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string findingMessage,
        CancellationToken ct = default);

    /// <summary>
    ///     Indexes the findings a review job posted. Idempotent per provider thread, so republishing after a
    ///     partial failure refreshes rows instead of duplicating them.
    /// </summary>
    Task RecordPostedFindingsAsync(
        IReadOnlyList<PostedFindingEntry> entries,
        CancellationToken ct = default);
}
