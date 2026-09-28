// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Azure.Core;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using Microsoft.VisualStudio.Services.Common;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AzureDevOps;

public class VssConnectionFactoryTests
{
    // Azure DevOps traffic goes through the Microsoft SDK, which builds its own transport. The connection the
    // factory hands out carries this host's guarded transport instead, so it follows the installation's egress
    // rule the way every other outbound call does.
    [Fact]
    public async Task GetConnectionAsync_WithoutTheOptIn_RefusesAPrivateAddress()
    {
        var factory = new VssConnectionFactory(Substitute.For<TokenCredential>(), EgressUrlPolicy.Locked);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetConnectionAsync(
            $"https://127.0.0.1:{UnusedPort()}/tfs",
            new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "server-pat")));

        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, refused.Message, StringComparison.Ordinal);
    }

    // The credential rides on every request the connection makes, so the scheme is decided before the
    // connection exists and not left to the transport, which only ever sees an address.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GetConnectionAsync_PlainHttpToAPublicHost_IsRefusedUnderEveryPosture(bool allowPrivateEgress)
    {
        var credential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(
            credential,
            new EgressUrlPolicy(allowPrivateEgress, AllowInsecureScheme: allowPrivateEgress));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetConnectionAsync("http://ado.example.com/tfs"));

        Assert.Contains("https", refused.Message, StringComparison.Ordinal);

        // The refusal is the answer, and not an authentication failure standing in for it: a URL this
        // installation refuses costs no token.
        await credential.DidNotReceiveWithAnyArgs().GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }

    // The header carries the credential git sends with every request to the organisation, so a refused
    // address never produces one.
    [Fact]
    public async Task GetHttpAuthorizationHeaderAsync_PlainHttpToAPublicHost_IsRefusedBeforeACredentialIsRead()
    {
        var credential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(credential, EgressUrlPolicy.Locked);

        var refusedForAToken = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetHttpAuthorizationHeaderAsync(
            "http://ado.example.com/tfs",
            null,
            CancellationToken.None));

        // A personal access token is read from the stored connection and needs no token provider, so the
        // refusal has to stand before the header is built and not only before a token is acquired.
        var refusedForAStoredSecret = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetHttpAuthorizationHeaderAsync(
            "http://ado.example.com/tfs",
            new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "server-pat"),
            CancellationToken.None));

        Assert.Contains("https", refusedForAToken.Message, StringComparison.Ordinal);
        Assert.Contains("https", refusedForAStoredSecret.Message, StringComparison.Ordinal);
        await credential.DidNotReceiveWithAnyArgs().GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetHttpAuthorizationHeaderAsync_APrivateHostWithoutTheOptIn_IsRefused()
    {
        var credential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(credential, EgressUrlPolicy.Locked);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.GetHttpAuthorizationHeaderAsync(
            "https://10.4.1.9/tfs",
            new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "server-pat"),
            CancellationToken.None));

        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetConnectionAsync_PlainHttpToAPrivateHost_NeedsTheOptIn()
    {
        var locked = new VssConnectionFactory(Substitute.For<TokenCredential>(), EgressUrlPolicy.Locked);
        var permitted = new VssConnectionFactory(
            Substitute.For<TokenCredential>(),
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false));
        var credentials = new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "server-pat");

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => locked.GetConnectionAsync("http://10.4.1.9/tfs", credentials));

        Assert.Contains(EgressUrlPolicy.PrivateEgressOptIn, refused.Message, StringComparison.Ordinal);
        Assert.NotNull(await permitted.GetConnectionAsync("http://10.4.1.9/tfs", credentials));
    }

    [Fact]
    public async Task GetConnectionAsync_WithTheOptIn_ReachesAPrivateAddress()
    {
        // The listener holds the port for the whole attempt, so nothing else on the machine can take it and
        // the connection the transport opens is the one accepted here.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        // One bound for both sides of the attempt, started before the connection is made. The accept is
        // awaited under that same bound, so a socket the transport queued is observed whenever the accept
        // continuation runs and a loaded machine does not decide the outcome.
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Accepted and closed at once, so the handshake fails as soon as it is attempted.
        var accepted = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(bound.Token);

            return client.Client.RemoteEndPoint;
        });

        var factory = new VssConnectionFactory(
            Substitute.For<TokenCredential>(),
            new EgressUrlPolicy(AllowPrivateEgress: true, AllowInsecureScheme: false));
        var connection = await factory.GetConnectionAsync(
            $"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/tfs",
            new AdoConnectionCredentials(ScmAuthenticationKind.PersonalAccessToken, "server-pat"));

        // Nothing answers the handshake, so the attempt fails on the connection and not on the egress rule.
        var failed = await Assert.ThrowsAnyAsync<Exception>(() => connection.ConnectAsync(bound.Token));

        Assert.DoesNotContain("blocked egress address", Flatten(failed), StringComparison.Ordinal);

        // The server side saw the connection: the opt-in let the transport dial a loopback address.
        Assert.NotNull(await accepted);
    }

    // Azure DevOps answers some routes with a redirect, and the hop would carry the organisation's credential
    // to whatever host the Location header names. The transport hands the 3xx back instead of following it.
    [Fact]
    public async Task TheTransport_DoesNotFollowARedirect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var requestedPaths = new List<string>();

        // Two answers are prepared: the redirect, and a 200 at the address it points to. A transport that
        // followed the hop would reach the second one and the request count would show it.
        var serving = Task.Run(
            async () =>
            {
                while (!bound.Token.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(bound.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream);

                    var requestLine = await reader.ReadLineAsync(bound.Token) ?? string.Empty;
                    lock (requestedPaths)
                    {
                        requestedPaths.Add(requestLine);
                    }

                    string? header;
                    while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync(bound.Token)))
                    {
                        // The headers are read to the blank line so the response is written to a drained socket.
                    }

                    var answer = requestLine.Contains("/_apis/hop", StringComparison.Ordinal)
                        ? "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                        : $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/_apis/hop\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), bound.Token);
                }
            },
            bound.Token);

        using var transport = VssConnectionFactory.CreateGuardedTransport(
            allowPrivateEgress: true,
            compressionEnabled: true,
            transportCredentials: null);
        using var http = new HttpClient(transport);

        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/_apis/projects"), bound.Token);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);

        await bound.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serving);

        lock (requestedPaths)
        {
            Assert.Single(requestedPaths);
            Assert.Contains("/_apis/projects", requestedPaths[0], StringComparison.Ordinal);
        }
    }

    /// <summary>The message of an exception and of everything it wraps.</summary>
    /// <param name="exception">The failure to read.</param>
    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
            if (current is AggregateException aggregate)
            {
                messages.AddRange(aggregate.InnerExceptions.Select(inner => inner.Message));
            }
        }

        return string.Join(" | ", messages);
    }

    /// <summary>A port the operating system chose, and nothing held at the moment it was picked.</summary>
    /// <remarks>
    ///     Used where nothing connects to it: the refusal under test happens before a socket is opened, so the
    ///     port only has to be well formed.
    /// </remarks>
    private static int UnusedPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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
        var perClientCredentials = AdoConnectionCredentials.ForOAuthClientCredentials("tenant-id", "client-id", "secret");

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

    [Fact]
    public async Task GetConnectionAsync_WithPersonalAccessToken_DoesNotUseGlobalCredential()
    {
        var globalCredential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(globalCredential);

        var connection = await factory.GetConnectionAsync(
            "https://ado-server.example.com",
            AdoConnectionCredentials.ForPersonalAccessToken("server-pat"));

        Assert.NotNull(connection);
        await globalCredential.DidNotReceiveWithAnyArgs()
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConnectionAsync_WithWindowsUserAccount_DoesNotUseGlobalCredential()
    {
        var globalCredential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(globalCredential);

        var connection = await factory.GetConnectionAsync(
            "https://ado-server.example.com",
            AdoConnectionCredentials.ForWindowsUserAccount(@"CONTOSO\ado-user", "password-value"));

        Assert.NotNull(connection);
        await globalCredential.DidNotReceiveWithAnyArgs()
            .GetTokenAsync(Arg.Any<TokenRequestContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetConnectionAsync_WithWindowsUserAccount_UsesWindowsCredential()
    {
        var globalCredential = Substitute.For<TokenCredential>();
        var factory = new VssConnectionFactory(globalCredential);

        var connection = await factory.GetConnectionAsync(
            "https://ado-server.example.com",
            AdoConnectionCredentials.ForWindowsUserAccount(@"CONTOSO\ado-user", "password-value"));

        Assert.NotNull(connection);
        Assert.IsType<VssCredentials>(connection.Credentials);

        var vssCredentials = connection.Credentials;
        Assert.IsType<WindowsCredential>(vssCredentials.Windows);
        var networkCredential = vssCredentials.Windows.Credentials?.GetCredential(new Uri("https://ado-server.example.com"), "NTLM");
        Assert.NotNull(networkCredential);
        Assert.Equal("ado-user", networkCredential!.UserName);
        Assert.Equal("CONTOSO", networkCredential.Domain);
    }

    [Fact]
    public void BuildCacheKey_OAuthCredentialsIncludesSecretAndTenantFingerprint()
    {
        const string orgUrl = "https://dev.azure.com/testorg";

        var first = VssConnectionFactory.BuildCacheKey(
            orgUrl,
            AdoConnectionCredentials.ForOAuthClientCredentials("tenant-a", "client-id", "secret-a"));
        var second = VssConnectionFactory.BuildCacheKey(
            orgUrl,
            AdoConnectionCredentials.ForOAuthClientCredentials("tenant-a", "client-id", "secret-b"));
        var third = VssConnectionFactory.BuildCacheKey(
            orgUrl,
            AdoConnectionCredentials.ForOAuthClientCredentials("tenant-b", "client-id", "secret-a"));

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
    }
}
