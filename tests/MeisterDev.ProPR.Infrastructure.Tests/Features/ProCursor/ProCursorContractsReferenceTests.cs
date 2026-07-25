// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;
using MeisterDev.ProPR.Infrastructure.Features.ProCursor.Remote;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.ProCursor;

public sealed class ProCursorContractsReferenceTests
{
    [Fact]
    public void InfrastructureAndContracts_TypesResolveFromDedicatedContractsAssembly()
    {
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(ProCursorTokenUsageResponse).Assembly.GetName().Name);
        Assert.Equal("MeisterDev.ProPR.ProCursor.Contracts", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }

    [Fact]
    public void SharedKeyConstants_NoLongerCompileFromInfrastructureAssembly()
    {
        Assert.NotEqual("MeisterDev.ProPR.Infrastructure", typeof(ProCursorSharedKeyAuthenticationDefaults).Assembly.GetName().Name);
    }
}
