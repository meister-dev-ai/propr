// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Infrastructure.Features.Licensing.Support;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Licensing;

/// <summary>The catalog an operator reads capabilities from, against the declared set.</summary>
public sealed class StaticPremiumCapabilityCatalogTests
{
    // The catalog is what an operator sees; PremiumCapabilityKey.All is what the product calls canonical.
    // They had drifted, so a licensing page listed capabilities in a different order from the declared one.
    [Fact]
    public void TheCapabilityCatalog_ListsCapabilitiesInTheCanonicalOrder()
    {
        var catalogOrder = new StaticPremiumCapabilityCatalog().GetAll().Select(c => c.Key).ToArray();

        Assert.Equal(PremiumCapabilityKey.All, catalogOrder);
    }
}
