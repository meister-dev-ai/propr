// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     Which dimension a stated license limit constrains.
///     <para>
///         Known values are <c>authorsPerMonth</c>, <c>clients</c>, <c>runners</c>, and
///         <c>concurrentReviews</c>.
///     </para>
///     <para>
///         Further keys are added rather than replacing these, so code written against the current set keeps
///         working. A client has to tolerate a value it does not know, because a newer installation can send one.
///     </para>
/// </summary>
public enum LicenseLimitKey
{
    /// <summary>Distinct pull request authors within one calendar month.</summary>
    AuthorsPerMonth = 1,

    /// <summary>Clients configured on the installation.</summary>
    Clients = 2,

    /// <summary>Runners enrolled with the installation.</summary>
    Runners = 3,

    /// <summary>Reviews executing at the same time.</summary>
    ConcurrentReviews = 4,
}
