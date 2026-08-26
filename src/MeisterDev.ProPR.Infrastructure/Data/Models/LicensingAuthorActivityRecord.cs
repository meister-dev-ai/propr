// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One author in one calendar month. The month and the author key are the key, so a month holds an author
///     once however much work ProPR finished for them within it.
/// </summary>
public sealed class LicensingAuthorActivityRecord
{
    /// <summary>The first day of the UTC month this row covers.</summary>
    public DateOnly ActivityMonth { get; set; }

    /// <summary>The host-scoped key naming the author.</summary>
    public string AuthorKey { get; set; } = string.Empty;

    /// <summary>Whether this author is left out of the count.</summary>
    public bool Excluded { get; set; }

    /// <summary>The provider family of the host that issued the identifier.</summary>
    public ScmProvider Provider { get; set; }

    /// <summary>The host that issued the identifier.</summary>
    public string HostBaseUrl { get; set; } = string.Empty;

    /// <summary>The author's identifier as that host issues it.</summary>
    public string ExternalUserId { get; set; } = string.Empty;

    /// <summary>When the month first held this author.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>Which kind of finished work put the author into the month.</summary>
    public AuthorActivitySource FirstSeenSource { get; set; }
}
