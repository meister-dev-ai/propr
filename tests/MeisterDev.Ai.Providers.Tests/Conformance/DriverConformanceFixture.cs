// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Conformance;

/// <summary>
///     What a driver has to supply to be measured by the shared conformance suite: itself, an endpoint it would
///     accept, and one it must refuse.
/// </summary>
/// <remarks>
///     The fixture is deliberately small. Anything a driver could vary here that the suite then tolerates is a
///     difference the suite stops measuring, so only the facts that genuinely differ between provider families
///     belong in it — where the driver is reached and what its own rules reject. The assertions stay in one place.
/// </remarks>
/// <param name="Name">Display name for the test case.</param>
/// <param name="Create">Builds the driver, with private egress and plain http both forbidden.</param>
/// <param name="AcceptableBaseUrl">A public https endpoint this driver's own base-URL rules accept.</param>
/// <param name="RejectedBaseUrl">
///     A public https endpoint this driver's own rules reject, or <see langword="null" /> for a driver that
///     accepts any host — the OpenAI-compatible class exists precisely to accept the long tail.
/// </param>
/// <param name="CredentialAuthMode">The auth mode whose credential this driver requires.</param>
public sealed record DriverConformanceFixture(
    string Name,
    Func<IAiProviderDriver> Create,
    string AcceptableBaseUrl,
    string? RejectedBaseUrl,
    AiAuthMode CredentialAuthMode = AiAuthMode.ApiKey)
{
    public override string ToString()
    {
        return this.Name;
    }
}
