// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Tests.Extensions;

public sealed class AuthHelpersTests
{
    [Fact]
    public void RequireAdmin_GlobalAdmin_ReturnsNull()
    {
        var context = new DefaultHttpContext();
        context.Items["IsAdmin"] = true;

        var result = AuthHelpers.RequireAdmin(context);

        Assert.Null(result);
    }

    [Fact]
    public void RequireAdmin_AuthenticatedNonAdmin_ReturnsForbidden()
    {
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();

        var result = AuthHelpers.RequireAdmin(context);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status403Forbidden, objectResult.StatusCode);
    }

    [Fact]
    public void RequireAuthenticated_UnauthenticatedCaller_ReturnsUnauthorized()
    {
        var result = AuthHelpers.RequireAuthenticated(new DefaultHttpContext());

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }

    [Fact]
    public void RequireAnyClientRole_ClientAdministratorAssignment_ReturnsNull()
    {
        var context = new DefaultHttpContext();
        context.Items["UserId"] = Guid.NewGuid().ToString();
        context.Items["ClientRoles"] = new Dictionary<Guid, ClientRole>
        {
            [Guid.NewGuid()] = ClientRole.ClientAdministrator,
        };

        var result = AuthHelpers.RequireAnyClientRole(context, ClientRole.ClientAdministrator);

        Assert.Null(result);
    }

    [Fact]
    public void RequireClientRole_UnauthenticatedCaller_ReturnsUnauthorized()
    {
        var result = AuthHelpers.RequireClientRole(new DefaultHttpContext(), Guid.NewGuid(), ClientRole.ClientUser);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
    }
}
