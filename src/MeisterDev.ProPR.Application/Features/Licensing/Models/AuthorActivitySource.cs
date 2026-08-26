// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Which kind of finished work first put an author into a month.
/// </summary>
/// <remarks>
///     Recorded as a fact about the row, not as a dimension of the count: an author who was both reviewed and
///     answered in the same month is one author, and the first observation is the one whose source is kept.
/// </remarks>
public enum AuthorActivitySource
{
    /// <summary>A completed review of a pull request the author opened.</summary>
    Review = 0,

    /// <summary>A completed answer to a comment the author wrote.</summary>
    MentionAnswer = 1,
}
