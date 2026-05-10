// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using MeisterProPR.Api.Features.ProCursor;
using ArchUnitArchitecture = ArchUnitNET.Domain.Architecture;

namespace MeisterProPR.Application.Tests.Architecture;

internal static class ArchitectureTestContext
{
    internal static readonly ArchUnitArchitecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(MeisterProPR.Application.Services.ReviewOrchestrationService).Assembly,
            typeof(MeisterProPR.Infrastructure.DependencyInjection.InfrastructureServiceExtensions).Assembly,
            typeof(MeisterProPR.ProCursor.Core.ProCursorGateway).Assembly,
            typeof(ManagedRemoteProCursorGateway).Assembly,
            typeof(MeisterProPR.Application.Interfaces.IProCursorGateway).Assembly)
        .Build();
}
