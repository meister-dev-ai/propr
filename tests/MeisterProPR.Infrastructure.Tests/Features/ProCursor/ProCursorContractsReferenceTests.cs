// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;

namespace MeisterProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProCursorContractsReferenceTests
{
    [Fact]
    public void InfrastructureAndContracts_TypesResolveFromDedicatedContractsAssembly()
    {
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(ProCursorTokenUsageResponse).Assembly.GetName().Name);
        Assert.Equal("MeisterProPR.ProCursor.Contracts", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }

    [Fact]
    public void SharedKeyConstants_NoLongerCompileFromInfrastructureAssembly()
    {
        Assert.NotEqual("MeisterProPR.Infrastructure", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }
}
