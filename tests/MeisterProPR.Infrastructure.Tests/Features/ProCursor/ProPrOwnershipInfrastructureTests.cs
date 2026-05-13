// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Features.ProCursor.Broker;
using MeisterProPR.Infrastructure.Repositories;

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProPrOwnershipInfrastructureTests
{
    [Fact]
    public void ProPrOwnedImplementations_CompileFromInfrastructureAssembly()
    {
        Assert.Equal("MeisterProPR.Infrastructure", typeof(LocalProPrScmBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.Infrastructure", typeof(LocalProPrEmbeddingBroker).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.Infrastructure", typeof(ProCursorKnowledgeSourceRepository).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.Infrastructure", typeof(ProCursorRemoteOptions).Assembly.GetName().Name);
    }
}
