// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

public sealed class ProPrOwnershipBoundaryTests
{
    [Fact]
    public void SharedBrokerAbstractions_CompileFromContractsAssembly()
    {
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(IProCursorScmBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(IProCursorEmbeddingBroker).Assembly.GetName().Name);
    }
}
