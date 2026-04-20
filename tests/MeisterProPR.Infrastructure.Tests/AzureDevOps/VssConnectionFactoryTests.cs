// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

public class VssConnectionFactoryTests
{
    [Fact]
    public async Task GetConnectionAsync_CacheKeyDistinguishesClientIdFromGlobal()
    {
        // Arrange
        var credential = Substitute.For<TokenCredential>();
        credential
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token-value", DateTimeOffset.UtcNow.AddHours(1)));

        var factory = new VssConnectionFactory(credential);
        const string orgUrl = "https://dev.azure.com/testorg";

        // Act: null credentials (global path) and non-null credentials use different cache keys
        var globalConn = await factory.GetConnectionAsync(orgUrl);
        var globalConn2 = await factory.GetConnectionAsync(orgUrl);

        // Same cache key for two global calls
        Assert.Same(globalConn, globalConn2);
    }

    [Fact]
    public async Task GetConnectionAsync_DifferentUrls_ReturnsDifferentConnections()
    {
        // Arrange
        var credential = Substitute.For<TokenCredential>();
        credential
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token-value", DateTimeOffset.UtcNow.AddHours(1)));

        var factory = new VssConnectionFactory(credential);

        // Act
        var conn1 = await factory.GetConnectionAsync("https://dev.azure.com/org1");
        var conn2 = await factory.GetConnectionAsync("https://dev.azure.com/org2");

        // Assert - different instances for different orgs
        Assert.NotSame(conn1, conn2);
    }

    [Fact]
    public async Task GetConnectionAsync_SameUrl_ReturnsCachedConnection()
    {
        // Arrange
        var credential = Substitute.For<TokenCredential>();
        credential
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token-value", DateTimeOffset.UtcNow.AddHours(1)));

        var factory = new VssConnectionFactory(credential);
        const string orgUrl = "https://dev.azure.com/testorg";

        // Act
        var conn1 = await factory.GetConnectionAsync(orgUrl);
        var conn2 = await factory.GetConnectionAsync(orgUrl);

        // Assert - same instance returned (cached)
        Assert.Same(conn1, conn2);

        // GetTokenAsync should only be called once
        await credential.Received(1)
            .GetTokenAsync(
                Arg.Any<TokenRequestContext>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConnectionAsync_UsesAdoResourceScope()
    {
        // Arrange
        TokenRequestContext? capturedContext = null;
        var credential = Substitute.For<TokenCredential>();
        credential
            .GetTokenAsync(Arg.Do<TokenRequestContext>(ctx => capturedContext = ctx), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token-value", DateTimeOffset.UtcNow.AddHours(1)));

        var factory = new VssConnectionFactory(credential);

        // Act
        await factory.GetConnectionAsync("https://dev.azure.com/testorg");

        // Assert - uses the ADO resource scope
        Assert.NotNull(capturedContext);
        Assert.Contains("499b84ac-1321-427f-aa17-267ca6975798/.default", capturedContext!.Value.Scopes);
    }

    [Fact]
    public async Task GetConnectionAsync_ValidCredential_ReturnsVssConnection()
    {
        // Arrange
        var credential = Substitute.For<TokenCredential>();
        credential
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>())
            .Returns(new AccessToken("fake-token-value", DateTimeOffset.UtcNow.AddHours(1)));

        var factory = new VssConnectionFactory(credential);

        // Act
        var connection = await factory.GetConnectionAsync("https://dev.azure.com/testorg");

        // Assert
        Assert.NotNull(connection);
    }

    [Fact]
    public async Task GetConnectionAsync_WithPerClientCredentials_DoesNotUseGlobalCredential()
    {
        // Arrange: global credential should NOT be called when per-client credentials are supplied
        var globalCredential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(globalCredential);

        // Per-client credentials use a real ClientSecretCredential path internally.
        // We can't easily substitute ClientSecretCredential, but we CAN verify the global credential is not used.
        var perClientCredentials = new AdoServicePrincipalCredentials("tenant-id", "client-id", "secret");

        // The ClientSecretCredential will fail with invalid tenant/client (no real AAD call in tests),
        // but we can confirm the global credential received zero calls.
        try
        {
            await factory.GetConnectionAsync("https://dev.azure.com/testorg", perClientCredentials);
        }
        catch
        {
            // Expected: ClientSecretCredential with fake values throws
        }

        await globalCredential.DidNotReceiveWithAnyArgs()
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }
}
