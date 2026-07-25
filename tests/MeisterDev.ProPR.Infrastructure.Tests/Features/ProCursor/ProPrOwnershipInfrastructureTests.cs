// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Broker;
using MeisterDev.ProPR.Infrastructure.Repositories;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProPrOwnershipInfrastructureTests
{
    [Fact]
    public void ProPrOwnedImplementations_CompileFromInfrastructureAssembly()
    {
        Assert.Equal("MeisterDev.ProPR.Infrastructure", typeof(LocalProPrScmBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.Infrastructure", typeof(LocalProPrEmbeddingBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.Infrastructure", typeof(ProCursorKnowledgeSourceRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.Infrastructure", typeof(ProCursorRemoteOptions).Assembly.GetName().Name);
    }
}
