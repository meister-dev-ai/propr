// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Reflection;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.ExampleAddIn;
using MeisterDev.Ai.Providers.Hosting;
using NSubstitute;

namespace MeisterDev.Ai.Providers.Tests.Hosting;

/// <summary>
///     Covers the primitives a family reaches the host through, driven from a project that holds the contract and
///     nothing else.
/// </summary>
/// <remarks>
///     The set is closed on purpose: left open, each family would build its own HTTP client, its own database
///     access, its own locking and its own state table. What these assert is that a real credential flow can be
///     written with the seven, and that none of them lets a family name a connection the host did not hand it.
/// </remarks>
public sealed class ProviderHostPrimitiveTests
{
    [Fact]
    public async Task AFlowCanTakeALeaseKeepAHandshakeAndSendTheOperatorToTheVendor()
    {
        var context = new StubActionContext();

        await using var action = new ExampleConnectAction();
        var result = await action.StartAsync(context, "the-state");

        var openUrl = Assert.IsType<ProviderActionOpenUrl>(result);
        Assert.True(openUrl.AwaitCompletion);
        Assert.Contains("the-state", openUrl.Url, StringComparison.Ordinal);
        Assert.Equal(ExampleConnectAction.ListenerResource, context.LeasedResource);
        Assert.Equal("the-state", context.WrittenEntryKey);
    }

    // The port is held from the moment the operator is sent to the vendor until the callback arrives, and the
    // family is what gives it back. A flow that dropped the lease would hold the port for the life of the host
    // and refuse every later connection of this family.
    [Fact]
    public async Task AFlowGivesTheListenerPortBackWhenTheInvocationEnds()
    {
        var context = new StubActionContext();
        var action = new ExampleConnectAction();

        await action.StartAsync(context, "the-state");
        Assert.False(context.ListenerReleased);

        await action.CompleteAsync(context, "the-state", "the-code");

        Assert.True(context.ListenerReleased);
    }

    [Fact]
    public async Task AFlowGivesTheListenerPortBackWhenTheHandshakeCannotBeKept()
    {
        var context = new StubActionContext { StoreWriteFails = true };
        var action = new ExampleConnectAction();

        await Assert.ThrowsAsync<InvalidOperationException>(() => action.StartAsync(context, "the-state"));

        Assert.True(context.ListenerReleased);
    }

    // The state is opaque and arbitrary. Interpolated raw, a value carrying '&' or '#' ends the parameter and the
    // vendor is asked for something other than what the flow meant.
    [Fact]
    public async Task AFlowEscapesTheStateItPutsInTheVendorsQueryString()
    {
        var context = new StubActionContext();

        await using var action = new ExampleConnectAction();
        var result = await action.StartAsync(context, "a&b=c#d");

        var openUrl = Assert.IsType<ProviderActionOpenUrl>(result);
        Assert.EndsWith("?state=a%26b%3Dc%23d", openUrl.Url, StringComparison.Ordinal);
    }

    // A refused exchange answers with a status and a body describing the refusal. Storing that body puts the
    // refusal where the access token belongs, and reporting healthy on top of it leaves a connection that is
    // marked usable and fails on its first model call.
    [Fact]
    public async Task ARefusedTokenExchangeIsNotStoredAndIsNotReportedHealthy()
    {
        var context = new StubActionContext { TokenExchangeStatus = HttpStatusCode.Unauthorized };

        await using var action = new ExampleConnectAction();
        var result = await action.CompleteAsync(context, "the-state", "the-code");

        Assert.Contains("401", Assert.IsType<ProviderActionFailed>(result).Message, StringComparison.Ordinal);
        Assert.Null(context.CredentialSession.Written);
        Assert.False(context.CredentialSession.Completed);
        Assert.Equal(AiCredentialHealth.NeedsReauthorization, context.ReportedHealth);
        Assert.NotNull(context.ReportedFailure);
    }

    // Two connections of one family cannot bind one port, so the second is told which connection holds it rather
    // than failing on the bind with an error that names nothing.
    [Fact]
    public async Task AFlowThatLosesTheLeaseFailsNamingTheConnectionHoldingIt()
    {
        var context = new StubActionContext { LeaseHeldBy = "Production OpenAI" };

        await using var action = new ExampleConnectAction();
        var result = await action.StartAsync(context, "the-state");

        Assert.Contains("Production OpenAI", Assert.IsType<ProviderActionFailed>(result).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFlowCanClaimTheHandshakeExchangeTheCodeAndStoreTheCredential()
    {
        var context = new StubActionContext();

        await using var action = new ExampleConnectAction();
        var result = await action.CompleteAsync(context, "the-state", "the-code");

        Assert.IsType<ProviderActionCompleted>(result);
        Assert.Equal("the-state", context.ClaimedEntryKey);
        Assert.True(context.CredentialSession.Completed);
        Assert.Contains("accessToken", context.CredentialSession.Written!.Keys);
        Assert.Equal(AiCredentialHealth.Healthy, context.ReportedHealth);
        Assert.Equal("The account is connected.", context.ReportedCompletion);
    }

    // Each refusal is a different thing for an operator to do, so a flow can say which one happened. A single
    // "could not claim" would leave a duplicate browser delivery and a hijacked state indistinguishable.
    [Theory]
    [InlineData(ProviderClaimRefusal.NoSuchEntry, "did not start here")]
    [InlineData(ProviderClaimRefusal.Expired, "took too long")]
    [InlineData(ProviderClaimRefusal.AlreadyConsumed, "already completed")]
    [InlineData(ProviderClaimRefusal.WrongPrincipal, "Another administrator")]
    public async Task EachWayAClaimCanFailIsDistinguishable(ProviderClaimRefusal refusal, string expected)
    {
        var context = new StubActionContext { ClaimRefusal = refusal };

        await using var action = new ExampleConnectAction();
        var result = await action.CompleteAsync(context, "the-state", "the-code");

        Assert.Contains(expected, Assert.IsType<ProviderActionFailed>(result).Message, StringComparison.Ordinal);
        Assert.Contains(expected, context.ReportedFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowCanAskTheOperatorForValuesWithoutPersistingThem()
    {
        var fields = new List<ProviderDeclaredField>
        {
            new("callbackUrl", "Callback URL", ProviderFieldKind.String) { Scope = ProviderFieldScope.ActionInput },
        };

        var form = Assert.IsType<ProviderActionShowForm>(ExampleConnectAction.AskForTheCallbackUrl(fields));

        Assert.All(form.Fields, field => Assert.Equal(ProviderFieldScope.ActionInput, field.Scope));
    }

    // The vocabulary is closed by construction rather than by convention, so a family cannot return a result the
    // host has no handling for.
    [Fact]
    public void TheResultVocabularyCannotBeExtendedFromOutsideTheContract()
    {
        var results = typeof(ProviderActionResult).Assembly
            .GetExportedTypes()
            .Where(type => type.IsSubclassOf(typeof(ProviderActionResult)))
            .ToList();

        Assert.Equal(4, results.Count);
        Assert.All(results, type => Assert.True(type.IsSealed));
        Assert.Empty(
            typeof(ProviderActionResult)
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(constructor => constructor.GetParameters().Length == 0));
    }

    // A family acts on the connection it was handed. None of the primitives takes an identifier it could pass a
    // different value for, and that stops one family's action reaching another tenant's connection.
    [Fact]
    public void NoPrimitiveTakesAConnectionIdentifierTheCallerChooses()
    {
        var offenders = typeof(IProviderConnectionContext).Assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface && type.Namespace == typeof(IProviderConnectionContext).Namespace)
            .SelectMany(type => type.GetMethods())
            .SelectMany(method => method.GetParameters().Select(parameter => $"{method.Name}({parameter.Name})"))
            .Where(named => named.Contains("connection", StringComparison.OrdinalIgnoreCase)
                            || named.Contains("tenant", StringComparison.OrdinalIgnoreCase)
                            || named.Contains("client", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.Empty(offenders);
    }

    // A family deals in named values. The stored envelope and the protection around it stay host-side, so a
    // family holding the row cannot read another connection's credential by decoding it itself.
    [Fact]
    public void NoPrimitiveExposesTheStoredEnvelopeOrAnythingThatProtectsIt()
    {
        var contract = typeof(IProviderConnectionContext).Assembly;

        var leaked = contract
            .GetExportedTypes()
            .Where(type => type.IsInterface && type.Namespace == typeof(IProviderConnectionContext).Namespace)
            .SelectMany(type => type.GetMethods().Concat(type.GetProperties().Select(property => property.GetMethod!)))
            .SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType))
            .SelectMany(Unwrap)
            .Where(type => type.Assembly != contract
                           && type.Assembly != typeof(object).Assembly
                           && type.Assembly != typeof(HttpClient).Assembly)
            .Select(type => type.FullName ?? type.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Empty(leaked);
    }

    /// <summary>Every type a signature names, with by-ref and generic wrapping taken off.</summary>
    /// <param name="type">The type as the signature declares it.</param>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        var root = type.IsByRef || type.IsArray ? type.GetElementType()! : type;

        if (!root.IsGenericType)
        {
            yield return root;
            yield break;
        }

        yield return root.GetGenericTypeDefinition();
        foreach (var argument in root.GenericTypeArguments.SelectMany(Unwrap))
        {
            yield return argument;
        }
    }

    /// <summary>A host that records what the flow asked of it, standing in for the implementations.</summary>
    private sealed class StubActionContext : IProviderActionContext
    {
        public StubActionContext()
        {
            this.Http = Substitute.For<IProviderHttpClientFactory>();
            this.Http.Create(Arg.Any<ProviderHttpPurpose>(), Arg.Any<IReadOnlyList<DelegatingHandler>?>())
                .Returns(_ => new HttpClient(new ScriptedHandler(this.TokenExchangeStatus)));
        }

        public string? LeasedResource { get; private set; }

        public bool ListenerReleased { get; private set; }

        public string? LeaseHeldBy { get; init; }

        public bool StoreWriteFails { get; init; }

        public HttpStatusCode TokenExchangeStatus { get; init; } = HttpStatusCode.OK;

        public string? WrittenEntryKey { get; private set; }

        public string? ClaimedEntryKey { get; private set; }

        public ProviderClaimRefusal ClaimRefusal { get; init; } = ProviderClaimRefusal.None;

        public AiCredentialHealth? ReportedHealth { get; private set; }

        public string? ReportedCompletion { get; private set; }

        public string? ReportedFailure { get; private set; }

        public StubCredentialSession CredentialSession { get; } = new();

        public IProviderHttpClientFactory Http { get; }

        public IProviderCredentialSessions Credentials => new StubSessions(this.CredentialSession);

        public IProviderLeases Leases => new StubLeases(this);

        public IProviderKeyedStore Store => new StubStore(this);

        public IProviderHealthSignal Health => new StubHealth(this);

        public CancellationToken Cancellation => CancellationToken.None;

        public IProviderInvocationReporter Invocation => new StubInvocation(this);

        private sealed class StubLeases(StubActionContext owner) : IProviderLeases
        {
            public Task<ProviderLeaseOutcome> AcquireAsync(string resourceName, TimeSpan timeout, CancellationToken ct = default)
            {
                if (owner.LeaseHeldBy is { } holder)
                {
                    return Task.FromResult(new ProviderLeaseOutcome(null, holder));
                }

                owner.LeasedResource = resourceName;
                return Task.FromResult(new ProviderLeaseOutcome(new StubLease(resourceName, owner), null));
            }
        }

        private sealed class StubLease(string resourceName, StubActionContext owner) : IProviderNamedLease
        {
            public string ResourceName => resourceName;

            public ValueTask DisposeAsync()
            {
                owner.ListenerReleased = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class StubStore(StubActionContext owner) : IProviderKeyedStore
        {
            public Task WriteAsync(
                string entryKey,
                IReadOnlyDictionary<string, string> values,
                DateTimeOffset expiresAt,
                CancellationToken ct = default)
            {
                if (owner.StoreWriteFails)
                {
                    throw new InvalidOperationException("The handshake could not be kept.");
                }

                owner.WrittenEntryKey = entryKey;
                return Task.CompletedTask;
            }

            public Task<ProviderClaimOutcome> ClaimAsync(string entryKey, CancellationToken ct = default)
            {
                owner.ClaimedEntryKey = entryKey;

                return Task.FromResult(
                    owner.ClaimRefusal == ProviderClaimRefusal.None
                        ? new ProviderClaimOutcome(
                            ProviderClaimRefusal.None,
                            new Dictionary<string, string>(StringComparer.Ordinal) { ["codeVerifier"] = "a-verifier" })
                        : ProviderClaimOutcome.Refused(owner.ClaimRefusal));
            }
        }

        private sealed class StubHealth(StubActionContext owner) : IProviderHealthSignal
        {
            public Task ReportAsync(AiCredentialHealth health, string? cause = null, CancellationToken ct = default)
            {
                owner.ReportedHealth = health;
                return Task.CompletedTask;
            }
        }

        private sealed class StubInvocation(StubActionContext owner) : IProviderInvocationReporter
        {
            public Task ReportCompletedAsync(string message, CancellationToken ct = default)
            {
                owner.ReportedCompletion = message;
                return Task.CompletedTask;
            }

            public Task ReportFailedAsync(string message, CancellationToken ct = default)
            {
                owner.ReportedFailure = message;
                return Task.CompletedTask;
            }
        }

        private sealed class StubSessions(StubCredentialSession session) : IProviderCredentialSessions
        {
            public Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default)
                => session.ReadAsync(ct);

            public Task<IProviderCredentialSession> OpenAsync(CancellationToken ct = default)
                => Task.FromResult<IProviderCredentialSession>(session);
        }

        private sealed class ScriptedHandler(HttpStatusCode status) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("a-token") });
        }
    }

    /// <summary>Records what the flow read and wrote, so the lock-read-exchange-write shape can be asserted.</summary>
    private sealed class StubCredentialSession : IProviderCredentialSession
    {
        public IReadOnlyDictionary<string, string>? Written { get; private set; }

        public bool Cleared { get; private set; }

        public bool Completed { get; private set; }

        public Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default)
            => Task.FromResult(
                new ProviderStoredCredential(
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["refreshToken"] = "the-old-one" },
                    DateTimeOffset.UtcNow.AddMinutes(5)));

        public Task WriteAsync(
            IReadOnlyDictionary<string, string> fields,
            DateTimeOffset expiresAt,
            CancellationToken ct = default)
        {
            this.Written = fields;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            this.Written = null;
            this.Cleared = true;
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CancellationToken ct = default)
        {
            this.Completed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
