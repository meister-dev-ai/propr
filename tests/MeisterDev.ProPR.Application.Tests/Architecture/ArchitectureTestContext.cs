// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using ArchUnitNET.Loader;
using MeisterDev.ProPR.Api.Features.ProCursor;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.ProCursor.Core;
using ArchUnitArchitecture = ArchUnitNET.Domain.Architecture;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

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
