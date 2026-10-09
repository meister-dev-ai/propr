// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Reflection;
using MeisterDev.ProPR.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

public sealed class AiConnectionCreationContractTests
{
    [Fact]
    public void CreationDeclaresConflictForDuplicateCorrelation()
    {
        var action = typeof(ClientAiConnectionsController).GetMethod(nameof(ClientAiConnectionsController.CreateAiConnection));
        Assert.NotNull(action);
        var responses = action.GetCustomAttributes<ProducesResponseTypeAttribute>();
        Assert.Contains(responses, response => response.StatusCode == StatusCodes.Status409Conflict);
    }
}
