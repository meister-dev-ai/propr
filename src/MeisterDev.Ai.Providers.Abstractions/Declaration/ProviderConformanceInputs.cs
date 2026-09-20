// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     The inputs the shared driver checks need from a family and cannot derive from the driver.
/// </summary>
/// <remarks>
///     <para>
///         The checks are the same for every family; the inputs are not. Which authentication mode a family
///         reads, and what its vendor's usage payload looks like, differ per family, and a check that guessed at
///         either would pass a driver it should refuse or refuse one it should pass.
///     </para>
///     <para>
///         The inputs sit on the declaration, not in a hand-written list beside the checks, so the same checks
///         also run against a family built outside this repository, where no such list exists.
///     </para>
/// </remarks>
/// <param name="CredentialAuthMode">
///     The authentication mode the checks read the family's credential fields against, as the family declared
///     it. The family states it, because an authentication mode belongs to the family that declares it and the
///     host declares none.
/// </param>
/// <param name="RecordedUsagePayload">
///     A usage payload recorded from this family's vendor, as JSON, replayed through
///     <see cref="Drivers.IAiProviderDriver.ReadUsage" /> so the check can assert the counter relationship on the
///     result. It holds the counts in the form the family's client library hands them to the host, and the host
///     prices against that same form. It is recorded, not synthesised: the check exists to catch a vendor that
///     reports its input count exclusive of the cache buckets, and a synthesised payload would only restate the
///     assumption of whoever wrote the check. A family that records none fails the check.
/// </param>
public sealed record ProviderConformanceInputs(string CredentialAuthMode, string RecordedUsagePayload = "");
