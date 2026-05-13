// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.Loader;
using MeisterProPR.Api.Features.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Infrastructure.DependencyInjection;
using MeisterProPR.ProCursor.Core;
using ArchUnitArchitecture = ArchUnitNET.Domain.Architecture;

namespace MeisterProPR.Application.Tests.Architecture;

internal static class ArchitectureTestContext
{
    internal static readonly ArchUnitArchitecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(ReviewOrchestrationService).Assembly,
            typeof(InfrastructureServiceExtensions).Assembly,
            typeof(ProCursorGateway).Assembly,
            typeof(ManagedRemoteProCursorGateway).Assembly,
            typeof(IProCursorGateway).Assembly)
        .Build();
}
