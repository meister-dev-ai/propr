// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Infrastructure.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Crawling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What a source-control client reaches the network with, composed the way the host composes it.
/// </summary>
/// <remarks>
///     A host name that resolved publicly when the connection was saved can be rebound to an internal address
///     afterwards, so the address the socket connects to is what decides. Redirects stay enabled for these
///     clients, because GitHub and GitLab answer some routes with one, and a hop to another host opens another
///     connection that the same check runs on.
/// </remarks>
public sealed class ScmProviderEgressCompositionTests
{
    /// <summary>The named clients the source-control providers send every request through.</summary>
    public static TheoryData<string> ClientNames =>
    [
        "GitHubProvider",
        "GitLabProvider",
        "ForgejoProvider",
    ];

    [Theory]
    [MemberData(nameof(ClientNames))]
    public async Task ARequestToABlockedAddressIsRefusedWithoutTheOptIn(string clientName)
    {
        using var host = Compose(allowPrivateEgress: false);
        using var client = host.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        // A literal in a range the guard refuses, so nothing is resolved and no request leaves the machine.
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("https://169.254.169.254/api/v4/projects")));

        Assert.Contains("blocked egress address", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ClientNames))]
    public async Task APrivateHostIsReachedWithTheOptIn(string clientName)
    {
        using var server = LoopbackServer.Start();
        using var host = Compose(allowPrivateEgress: true);
        using var client = host.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        using var response = await client.GetAsync(server.Address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ClientNames))]
    public async Task ARedirectIsFollowed(string clientName)
    {
        using var server = LoopbackServer.Start();
        using var host = Compose(allowPrivateEgress: true);
        using var client = host.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        using var response = await client.GetAsync(server.RedirectingAddress);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(LoopbackServer.Body, await response.Content.ReadAsStringAsync());
    }

    // A redirect hop is one more connection, and every connection the client opens goes through the address
    // check on its transport. Both hops of this exchange are on an address the check refuses, and both are
    // put through the client: the chain cannot be walked in one request from here, because the first hop is
    // the loopback server the check refuses before it ever sees the second.
    [Theory]
    [MemberData(nameof(ClientNames))]
    public async Task BothHopsOfARedirectToABlockedAddressAreRefusedWithoutTheOptIn(string clientName)
    {
        using var server = LoopbackServer.Start();
        using var host = Compose(allowPrivateEgress: false);
        using var client = host.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        using var unguarded = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var redirect = await unguarded.GetAsync(server.BlockedRedirectingAddress);

        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        var hop = redirect.Headers.Location;
        Assert.NotNull(hop);

        // Each request fails where a connection fails. Both the reason the guard gives and the destination it
        // names are asserted: a connection refused by the operating system, or a name that does not resolve,
        // reports the host as well and would otherwise stand in for the check.
        var refusedAtTheFirstHop = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.BlockedRedirectingAddress));
        var refusedAtTheSecondHop = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(hop));

        Assert.Contains("blocked egress address", refusedAtTheFirstHop.Message, StringComparison.Ordinal);
        Assert.Contains(server.BlockedRedirectingAddress.Host, refusedAtTheFirstHop.Message, StringComparison.Ordinal);
        Assert.Contains("blocked egress address", refusedAtTheSecondHop.Message, StringComparison.Ordinal);
        Assert.Contains(hop.Host, refusedAtTheSecondHop.Message, StringComparison.Ordinal);
    }

    // The guard has to be the primary handler and not a delegating one: a handler further out can be
    // bypassed by a request that never reaches it, and the address check has to run on every connection the
    // client opens, redirect hops included.
    [Theory]
    [MemberData(nameof(ClientNames))]
    public void TheGuardIsTheClientsOwnTransportAndFollowsRedirects(string clientName)
    {
        using var host = Compose(allowPrivateEgress: false);
        using var chain = host.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        var transport = Assert.IsType<SocketsHttpHandler>(Innermost(chain));

        Assert.NotNull(transport.ConnectCallback);
        Assert.True(transport.AllowAutoRedirect);
    }

    [Theory]
    [MemberData(nameof(ClientNames))]
    public void TheGuardIsOmittedWhereTheInstallationPermitsPrivateEgress(string clientName)
    {
        using var host = Compose(allowPrivateEgress: true);
        using var chain = host.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);

        var transport = Assert.IsType<SocketsHttpHandler>(Innermost(chain));

        Assert.Null(transport.ConnectCallback);
    }

    /// <summary>The handler nearest the wire in a named client's chain.</summary>
    /// <param name="handler">The chain the factory built.</param>
    private static HttpMessageHandler Innermost(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating && delegating.InnerHandler is not null)
        {
            handler = delegating.InnerHandler;
        }

        return handler;
    }

    private static ServiceProvider Compose(bool allowPrivateEgress)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [EgressUrlPolicy.PrivateEgressOptIn] = allowPrivateEgress ? "true" : "false",
                })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructureSupport(configuration, environment: null, includeProviderOperationalServices: false);
        services.AddCrawlingModule(configuration);

        return services.BuildServiceProvider();
    }

    /// <summary>A source-control host on loopback, answering one body and redirecting to it from a second path.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        /// <summary>What the server answers with.</summary>
        public const string Body = """{"id":1}""";

        /// <summary>The link-local address the second redirect hands out, which no request may reach.</summary>
        private const string BlockedRedirectTarget = "http://169.254.169.254/api/v1/user";

        /// <summary>How many ports are tried before the failure to bind one is reported.</summary>
        private const int PortAttempts = 5;

        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private Task _loop = Task.CompletedTask;

        private LoopbackServer(HttpListener listener, int port)
        {
            this._listener = listener;
            this.Address = new Uri($"http://127.0.0.1:{port}/api/v1/user");
            this.RedirectingAddress = new Uri($"http://127.0.0.1:{port}/moved");
            this.BlockedRedirectingAddress = new Uri($"http://127.0.0.1:{port}/moved-to-metadata");
        }

        /// <summary>Where the server answers directly.</summary>
        public Uri Address { get; }

        /// <summary>Where the server answers with a redirect to <see cref="Address" />.</summary>
        public Uri RedirectingAddress { get; }

        /// <summary>Where the server answers with a redirect to the cloud metadata address.</summary>
        public Uri BlockedRedirectingAddress { get; }

        public static LoopbackServer Start()
        {
            var (listener, port) = OpenOnAFreePort();
            var server = new LoopbackServer(listener, port);
            server._loop = Task.Run(server.ServeAsync);

            return server;
        }

        public void Dispose()
        {
            this._shutdown.Cancel();
            this._listener.Close();

            // The loop's failures are this server's failures, so they are waited for and surface on the test
            // that used it instead of being discarded with the task.
            this._loop.GetAwaiter().GetResult();
            this._shutdown.Dispose();
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!this._shutdown.IsCancellationRequested)
                {
                    var context = await this._listener.GetContextAsync();
                    var path = context.Request.Url?.AbsolutePath;
                    if (path == this.RedirectingAddress.AbsolutePath || path == this.BlockedRedirectingAddress.AbsolutePath)
                    {
                        context.Response.StatusCode = (int)HttpStatusCode.Found;
                        context.Response.RedirectLocation = path == this.RedirectingAddress.AbsolutePath
                            ? this.Address.ToString()
                            : BlockedRedirectTarget;
                        context.Response.Close();
                        continue;
                    }

                    var payload = Encoding.UTF8.GetBytes(Body);
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = payload.Length;
                    await context.Response.OutputStream.WriteAsync(payload);
                    context.Response.Close();
                }
            }
            catch (Exception exception)
                when (this._shutdown.IsCancellationRequested && exception is HttpListenerException or ObjectDisposedException)
            {
                // Disposal closed the listener while it was waiting, which is how this loop ends. A failure of
                // either kind at any other time is the server's and is left to surface.
            }
        }

        /// <summary>Opens a listener on a port nothing else on the machine holds.</summary>
        /// <remarks>
        ///     A port is found free by binding it and letting it go again, so anything else on the machine can
        ///     take it before this listener binds it, and the bind then throws. A fresh port is tried a bounded
        ///     number of times and the last attempt throws, so a machine that cannot carry a loopback listener
        ///     fails the test instead of looping forever.
        /// </remarks>
        private static (HttpListener Listener, int Port) OpenOnAFreePort()
        {
            var attempt = 0;

            while (true)
            {
                attempt++;
                var port = FreePort();
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                var started = false;

                try
                {
                    listener.Start();
                    started = true;
                    return (listener, port);
                }
                catch (HttpListenerException) when (attempt < PortAttempts)
                {
                    // The port was taken between the probe and the bind, so another one is tried.
                }
                finally
                {
                    if (!started)
                    {
                        listener.Close();
                    }
                }
            }
        }

        /// <summary>A port the operating system chose, and nothing held at the moment it was picked.</summary>
        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }
    }
}
