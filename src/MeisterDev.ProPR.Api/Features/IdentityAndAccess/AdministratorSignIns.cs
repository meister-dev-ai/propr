// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess;

/// <summary>The steps every sign-in of a platform administrator takes, whichever way the administrator signed in.</summary>
internal static class AdministratorSignIns
{
    /// <summary>
    ///     Records that a platform administrator has signed in, for the usage-statistics consent gate.
    ///     <para>
    ///         This opens the gate on an installation that shows no consent notice, which is any installation
    ///         with a commercial license. A community installation opens its gate when the notice renders, and
    ///         is unaffected here.
    ///     </para>
    ///     <para>
    ///         Sign-in must not depend on this call, so a failure is ignored. The gate stays shut until the
    ///         next sign-in, and nothing is sent while it is shut.
    ///     </para>
    /// </summary>
    public static async Task RecordAsync(UsageStatisticsService? usageStatisticsService, AppUser user, CancellationToken ct)
    {
        if (usageStatisticsService is null || user.GlobalRole != AppUserRole.Admin)
        {
            return;
        }

        try
        {
            await usageStatisticsService.RecordAdministratorSignInAsync(ct);
        }
        // Caught broadly on purpose. The contract is that sign-in never fails for this, and the write can fail with
        // exceptions of the persistence layer that this method does not enumerate.
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
