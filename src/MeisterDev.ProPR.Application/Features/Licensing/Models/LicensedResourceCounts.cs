// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What the installation currently holds for the dimensions a license states limits for.
///     <para>
///         The numbers are read for reporting. Nothing decides entitlement from them: no license check, no
///         activation and no capability resolution reads this type. They do reach a public document, because a
///         commercial installation reports them in its daily usage statistics snapshot.
///     </para>
///     <para>
///         Every member here is a measured count, so none of them is nullable. A dimension the installation does
///         not measure has no member on this record rather than a member reporting zero, because zero is a real
///         count and a missing measurement is not.
///     </para>
/// </summary>
/// <param name="Clients">
///     Clients the installation holds. Without multi-tenancy that is the clients under the System tenant and
///     the ones carrying no tenant, which are the clients the installation can list and delete.
/// </param>
/// <param name="EnrolledRunners">
///     Runners that can still be given work: enrolled, not revoked, and holding a credential that has not
///     expired. A revoked runner and one whose credential has expired both fail authentication, so neither can
///     lease and neither occupies a licensed place. A host that comes back after its credential expired
///     enrolls as a new row and occupies one from then on.
/// </param>
/// <param name="ReviewsInProgress">Review jobs currently executing.</param>
public sealed record LicensedResourceCounts(long Clients, long EnrolledRunners, long ReviewsInProgress);
