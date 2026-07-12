// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.ProCursor.Service.Tests.Support;

namespace MeisterProPR.ProCursor.Service.Tests.Controllers;

public sealed class InternalProCursorAuthTests(ProCursorServiceFactory factory)
    : IClassFixture<ProCursorServiceFactory>
{
    [Fact]
    public async Task InternalEndpoint_WithoutSharedKey_Returns401()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/internal/procursor/clients/{Guid.NewGuid():D}/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternalEndpoint_WithInvalidSharedKey_Returns401()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, "wrong-key");

        var response = await client.GetAsync($"/internal/procursor/clients/{Guid.NewGuid():D}/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task InternalEndpoint_WithSameLengthWrongSharedKey_Returns401()
    {
        // A wrong key of the SAME length as the configured key exercises the constant-time byte comparison
        // itself (not just the length mismatch), guarding against a regression in the FixedTimeEquals check.
        var wrongKey = new string('x', ProCursorServiceFactory.SharedKey.Length);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ProCursorSharedKeyAuthenticationDefaults.HeaderName, wrongKey);

        var response = await client.GetAsync($"/internal/procursor/clients/{Guid.NewGuid():D}/sources");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
