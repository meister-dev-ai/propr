// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers.Hosting;

/// <summary>
///     What the host does around a provider family's declared action: what it opens, what it bounds, what it
///     refuses, and what it records when the family answers, throws, or never answers at all.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ProviderActionDispatchTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private ProviderHostPrimitiveHarness _harness = null!;

    public Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();
        this._harness = ProviderHostPrimitiveHarness.Create(fixture.ConnectionString);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (fixture.IsAvailable)
        {
            await this._harness.DisposeAsync();
        }
    }

    // A dispatch opens a record and answers with it. The record carries who started it, which connection it acts
    // on and which action of which family it is, because that is what an operator's view reads from then on.
    [Fact]
    public async Task ADispatchOpensAnInvocationCarryingTheAdministratorTheConnectionAndTheAction()
    {
        var binding = await this._harness.SeedConnectionAsync("Opens a run");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.Completed("Connected."));
        var administrator = await this._harness.SeedAdministratorAsync(isPlatformAdministrator: true);

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            administrator);

        Assert.Equal(ProviderDispatchRefusal.None, outcome.Refusal);
        Assert.NotNull(outcome.Invocation);
        Assert.Equal(binding.ConnectionProfileId, outcome.Invocation.ConnectionProfileId);
        Assert.Equal("example/provider", outcome.Invocation.AddInKey);
        Assert.Equal(ScriptedActionDriver.ActionId, outcome.Invocation.ActionId);
        Assert.Equal(administrator, outcome.Invocation.InitiatingAdminId);
    }

    // The family is handed a connection it did not choose, and the only one it can act on is the one the host
    // resolved: the endpoint it receives and the handle on it are both built for that row.
    [Fact]
    public async Task TheFamilyReceivesAHandleBoundToTheResolvedConnection()
    {
        var binding = await this._harness.SeedConnectionAsync("Bound handle");
        var driver = ScriptedActionDriver.Doing((endpoint, _, context) =>
        {
            Assert.NotNull(context.Credentials);
            Assert.Same(context, endpoint.HostContext);
            return Task.FromResult(ProviderActionResult.Completed("Connected."));
        });

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.None, outcome.Refusal);
        var endpoint = Assert.Single(driver.Invoked);
        Assert.Equal("https://api.example.com/v1", endpoint.BaseUrl);
    }

    // A key carries a separator, so it travels as a field. It is checked against the family serving the
    // connection rather than taken as the answer.
    [Fact]
    public async Task AnIdentityKeyCarryingASeparatorRoundTripsAndOneNamingAnotherFamilyIsRefused()
    {
        var binding = await this._harness.SeedConnectionAsync("Key round trip");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.Completed("Connected."));
        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);

        var accepted = await dispatcher.DispatchAsync(
            connection,
            "example/provider",
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());
        Assert.Equal(ProviderDispatchRefusal.None, accepted.Refusal);

        var refused = await dispatcher.DispatchAsync(
            connection,
            "someone/else",
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.UnknownFamily, refused.Refusal);
        Assert.Null(refused.Invocation);
    }

    [Fact]
    public async Task AnActionTheFamilyDoesNotDeclareIsRefusedNamingIt()
    {
        var binding = await this._harness.SeedConnectionAsync("Undeclared action");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.Completed("Connected."));

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            "disconnect",
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.UnknownAction, outcome.Refusal);
        Assert.NotNull(outcome.Message);
        Assert.Contains("disconnect", outcome.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Invocation);
        Assert.Empty(driver.Invoked);
    }

    // A family can declare an action and ship nothing that runs it, which is a family that does not load its own
    // declaration. Refused where every other unstartable action is, so no run is opened for it.
    [Fact]
    public async Task AnActionOfAFamilyThatSuppliesNothingToRunItIsRefused()
    {
        var binding = await this._harness.SeedConnectionAsync("Declares but does not run");
        var declaration = ScriptedActionDriver.Declaring();
        var driver = Substitute.For<IAiProviderDriver>();
        driver.Declaration.Returns(declaration);
        driver.SupportedAuthModes.Returns(declaration.SupportedAuthModes);
        driver.CredentialFields.Returns(declaration.CredentialFields);

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.UnknownAction, outcome.Refusal);
        Assert.Null(outcome.Invocation);

        await using var db = this._harness.CreateContext();
        Assert.Equal(
            0,
            db.ProviderActionInvocations.Count(run => run.ConnectionProfileId == binding.ConnectionProfileId));
    }

    [Fact]
    public async Task AnActionOfAFamilyThisInstallationIsNotLicensedForIsRefusedBeforeItRuns()
    {
        var binding = await this._harness.SeedConnectionAsync("Unlicensed", requiredCapabilityKey: "example-connections");
        var driver = ScriptedActionDriver.Answering(
            ProviderActionResult.Completed("Connected."),
            ScriptedActionDriver.Declaring(capabilityKey: "example-connections"));

        this._harness.Capabilities
            .IsAvailableAsync("example-connections", Arg.Any<CancellationToken>())
            .Returns(false);

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.CapabilityUnavailable, outcome.Refusal);
        Assert.Contains("example-connections", outcome.Message!, StringComparison.Ordinal);
        Assert.Null(outcome.Invocation);
        Assert.Empty(driver.Invoked);
    }

    // A family that throws leaves a reason on the run rather than an unobserved task, and the reason is capped
    // and scrubbed like everything else it produces.
    [Fact]
    public async Task AFamilyThatThrowsProducesAFailedInvocationCarryingTheFailure()
    {
        var binding = await this._harness.SeedConnectionAsync("Throws");
        await this._harness.SeedCredentialAsync(
            binding,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["apiKey"] = "sk-the-secret-token" },
            DateTimeOffset.UtcNow.AddHours(1));

        var driver = ScriptedActionDriver.Doing((_, _, _) =>
            throw new InvalidOperationException("The vendor refused the key sk-the-secret-token."));

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderDispatchRefusal.None, outcome.Refusal);
        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
        Assert.NotNull(outcome.Invocation.TerminalMessage);
        Assert.DoesNotContain("sk-the-secret-token", outcome.Invocation.TerminalMessage, StringComparison.Ordinal);
        Assert.IsType<ProviderActionFailed>(outcome.Result);
    }

    // An address the family authored reaches an operator's browser, so the host checks it. One that fails
    // becomes a failure naming the host, and the run is closed rather than left open for a page that will never
    // be visited.
    [Fact]
    public async Task AnAddressOnAnUndeclaredHostBecomesAFailureAndNeverReachesTheBrowser()
    {
        var binding = await this._harness.SeedConnectionAsync("Undeclared destination");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.OpenUrl("https://evil.example.net/steal", awaitCompletion: true));

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var failed = Assert.IsType<ProviderActionFailed>(outcome.Result);
        Assert.Contains("evil.example.net", failed.Message, StringComparison.Ordinal);
        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
    }

    [Fact]
    public async Task AnAddressOnADeclaredHostIsReturnedAndTheRunStaysOpenWhenTheFamilyWillReportLater()
    {
        var binding = await this._harness.SeedConnectionAsync("Declared destination");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.OpenUrl("https://auth.example.com/authorize?state=abc", awaitCompletion: true));

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var open = Assert.IsType<ProviderActionOpenUrl>(outcome.Result);
        Assert.Equal("https://auth.example.com/authorize?state=abc", open.Url);
        Assert.Equal(ProviderInvocationState.Pending, outcome.Invocation!.State);
    }

    // A form submission continues the run that asked for it. A second run would orphan the first until its
    // window closed and would let a family that reads a fresh dispatch as a fresh start open a second
    // authorization at the vendor while the first is in flight.
    [Fact]
    public async Task SubmittingAFormContinuesTheSameRunAndOpensNoSecondOne()
    {
        var binding = await this._harness.SeedConnectionAsync("Form continuation");
        var initiator = await this._harness.SeedAdministratorAsync(isPlatformAdministrator: true);
        var submitted = new List<IReadOnlyDictionary<string, string>>();

        var driver = ScriptedActionDriver.Doing((_, inputs, _) =>
        {
            submitted.Add(inputs);

            return Task.FromResult(
                inputs.Count == 0
                    ? ProviderActionResult.ShowForm(
                    [
                        new ProviderDeclaredField(ScriptedActionDriver.CallbackInput, "Callback URL", ProviderFieldKind.String)
                        {
                            Scope = ProviderFieldScope.ActionInput,
                        },
                    ])
                    : ProviderActionResult.Completed("Connected."));
        });

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);

        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            initiator);

        Assert.IsType<ProviderActionShowForm>(started.Result);
        Assert.Equal(ProviderInvocationState.Pending, started.Invocation!.State);

        var continued = await dispatcher.ContinueAsync(
            connection,
            started.Invocation,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ScriptedActionDriver.CallbackInput] = "https://auth.example.com/callback?code=abc",
            });

        Assert.Equal(started.Invocation.Id, continued.Invocation!.Id);
        Assert.Equal(initiator, continued.Invocation.InitiatingAdminId);
        Assert.Equal(started.Invocation.ExpiresAt, continued.Invocation.ExpiresAt);
        Assert.Equal(ProviderInvocationState.Completed, continued.Invocation.State);
        Assert.Equal(2, submitted.Count);

        await using var db = this._harness.CreateContext();
        var runs = db.ProviderActionInvocations
            .Count(invocation => invocation.ConnectionProfileId == binding.ConnectionProfileId);
        Assert.Equal(1, runs);
    }

    // A flow can need more than one round of values — a code, then a passphrase — and each round continues the
    // run that asked, so the port, the handshake and the initiating administrator stay attached to one record.
    [Fact]
    public async Task AFamilyCanAskForAFormMoreThanOnceInOneRun()
    {
        var binding = await this._harness.SeedConnectionAsync("Two forms");
        var asked = 0;

        var driver = ScriptedActionDriver.Doing((_, _, _) =>
        {
            asked++;

            return Task.FromResult(
                asked < 3
                    ? ProviderActionResult.ShowForm([])
                    : ProviderActionResult.Completed("Connected."));
        });

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);
        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var second = await dispatcher.ContinueAsync(connection, started.Invocation!, Values("first"));
        Assert.IsType<ProviderActionShowForm>(second.Result);
        Assert.Equal(started.Invocation!.Id, second.Invocation!.Id);
        Assert.Equal(ProviderInvocationState.Pending, second.Invocation.State);

        var third = await dispatcher.ContinueAsync(connection, second.Invocation, Values("second"));

        Assert.Equal(started.Invocation.Id, third.Invocation!.Id);
        Assert.Equal(ProviderInvocationState.Completed, third.Invocation.State);

        await using var db = this._harness.CreateContext();
        Assert.Equal(
            1,
            db.ProviderActionInvocations.Count(run => run.ConnectionProfileId == binding.ConnectionProfileId));
    }

    // The values an action collects belong to the run. They are never written anywhere, which keeps a pasted
    // callback address carrying a live authorization code out of the database.
    [Fact]
    public async Task NoSubmittedValueIsPersistedInAnyColumn()
    {
        var binding = await this._harness.SeedConnectionAsync("Transient values");
        const string Pasted = "https://auth.example.com/callback?code=a-live-authorization-code";

        var driver = ScriptedActionDriver.Doing((_, inputs, _) => Task.FromResult(
            inputs.Count == 0
                ? ProviderActionResult.ShowForm([])
                : ProviderActionResult.Completed("Connected.")));

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);
        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        await dispatcher.ContinueAsync(
            connection,
            started.Invocation!,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ScriptedActionDriver.CallbackInput] = Pasted,
            });

        await using var db = this._harness.CreateContext();
        var run = db.ProviderActionInvocations.Single(invocation => invocation.Id == started.Invocation!.Id);
        var profile = db.AiConnectionProfiles.Single(each => each.Id == binding.ConnectionProfileId);

        Assert.DoesNotContain("a-live-authorization-code", run.TerminalMessage ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("a-live-authorization-code", run.WaitingFor ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("a-live-authorization-code", profile.ProtectedSecret ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(profile.ProviderSettings);
    }

    // The host redacts a family's output of the secret-marked inputs the declaration names, and it knows nothing
    // about a name the declaration does not carry. A value submitted under one is refused rather than forwarded.
    [Fact]
    public async Task AValueUnderAnUndeclaredNameIsRefusedAndNeverReachesTheFamily()
    {
        var binding = await this._harness.SeedConnectionAsync("Undeclared input");
        var seen = new List<IReadOnlyDictionary<string, string>>();

        var driver = ScriptedActionDriver.Doing((_, inputs, _) =>
        {
            seen.Add(inputs);

            return Task.FromResult(
                inputs.Count == 0
                    ? ProviderActionResult.ShowForm([])
                    : ProviderActionResult.Completed("Connected."));
        });

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);
        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var outcome = await dispatcher.ContinueAsync(
            connection,
            started.Invocation!,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["clientSecret"] = "a-live-secret" });

        Assert.Equal(ProviderDispatchRefusal.InvalidInput, outcome.Refusal);
        Assert.Contains("clientSecret", outcome.Message!, StringComparison.Ordinal);
        Assert.Single(seen);
    }

    // A family reading a declared shape parses it without checking, so a value of the wrong shape is refused
    // here and not at the family's first use of it.
    [Fact]
    public async Task AValueOfTheWrongShapeIsRefusedNamingTheField()
    {
        var binding = await this._harness.SeedConnectionAsync("Wrong shape");
        var declaration = ScriptedActionDriver.Declaring(
        [
            new ProviderDeclaredAction(
                ScriptedActionDriver.ActionId,
                "Connect account",
                [
                    new ProviderDeclaredField("attempts", "Attempts", ProviderFieldKind.Int)
                    {
                        Scope = ProviderFieldScope.ActionInput,
                    },
                ]),
        ]);

        var driver = ScriptedActionDriver.Doing(
            (_, inputs, _) => Task.FromResult(
                inputs.Count == 0
                    ? ProviderActionResult.ShowForm([])
                    : ProviderActionResult.Completed("Connected.")),
            declaration);

        var dispatcher = this.Dispatcher(driver);
        var connection = Connection(binding);
        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var outcome = await dispatcher.ContinueAsync(
            connection,
            started.Invocation!,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["attempts"] = "twice" });

        Assert.Equal(ProviderDispatchRefusal.InvalidInput, outcome.Refusal);
        Assert.Contains("Attempts", outcome.Message!, StringComparison.Ordinal);
    }

    // The family and the credential come from the connection, the window and the state from the run. A caller
    // pairing one connection with another's run is refused rather than running the two together.
    [Fact]
    public async Task ARunStartedOnAnotherConnectionCannotBeContinuedHere()
    {
        var binding = await this._harness.SeedConnectionAsync("Owning connection");
        var other = await this._harness.SeedConnectionAsync("Other connection");

        var driver = ScriptedActionDriver.Doing((_, inputs, _) => Task.FromResult(
            inputs.Count == 0
                ? ProviderActionResult.ShowForm([])
                : ProviderActionResult.Completed("Connected.")));

        var dispatcher = this.Dispatcher(driver);
        var started = await dispatcher.DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        var outcome = await dispatcher.ContinueAsync(Connection(other), started.Invocation!, Values("code"));

        Assert.Equal(ProviderDispatchRefusal.InvocationNotOnThisConnection, outcome.Refusal);
        Assert.Equal(ProviderInvocationState.Pending, started.Invocation!.State);
    }

    [Fact]
    public async Task SubmittingAgainstARunThatRanOutOfItsWindowIsRefusedNamingTheExpiry()
    {
        var binding = await this._harness.SeedConnectionAsync("Expired continuation");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var driver = ScriptedActionDriver.Doing((_, inputs, _) => Task.FromResult(
            inputs.Count == 0
                ? ProviderActionResult.ShowForm([])
                : ProviderActionResult.Completed("Connected.")));

        var dispatcher = this.Dispatcher(driver, clock);
        var connection = Connection(binding);
        var started = await dispatcher.DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        clock.Advance(ProviderHostLimits.MaximumInvocationWindow + TimeSpan.FromMinutes(1));
        var expired = await this.Invocations(clock).GetAsync(started.Invocation!.Id);

        var outcome = await dispatcher.ContinueAsync(
            connection,
            expired!,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["callbackUrl"] = "x" });

        Assert.Equal(ProviderDispatchRefusal.InvocationNotPending, outcome.Refusal);
        Assert.Contains("window", outcome.Message!, StringComparison.OrdinalIgnoreCase);
    }

    // A family that blocks without watching its signal is not cut. What the host guarantees is that it holds
    // nothing: the call returns, the run stays readable, and the window expires it.
    [Fact]
    public async Task AFamilyThatNeverAnswersDoesNotHoldTheCallAndItsRunExpires()
    {
        var binding = await this._harness.SeedConnectionAsync("Never answers");
        using var blocked = new SemaphoreSlim(0);

        var driver = ScriptedActionDriver.Doing(async (_, _, _) =>
        {
            await blocked.WaitAsync(TimeSpan.FromSeconds(30));
            return ProviderActionResult.Completed("Too late.");
        });

        // A short wait on the real clock, because what is being shown is that the call returns while the family
        // is still inside its action. The host's own wait is the same mechanism with a longer number.
        var outcome = await this.Dispatcher(driver, dispatchWait: TimeSpan.FromMilliseconds(200))
            .DispatchAsync(
                Connection(binding),
                driver.Declaration.Key,
                ScriptedActionDriver.ActionId,
                Guid.NewGuid())
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ProviderDispatchRefusal.None, outcome.Refusal);
        Assert.Null(outcome.Result);
        Assert.Equal(ProviderInvocationState.Pending, outcome.Invocation!.State);

        // The run is expired by its window, whether or not the family ever comes back.
        var afterTheWindow = new FakeTimeProvider(outcome.Invocation.ExpiresAt + TimeSpan.FromMinutes(1));
        var expired = await this.Invocations(afterTheWindow).GetAsync(outcome.Invocation.Id);

        Assert.Equal(ProviderInvocationState.Expired, expired!.State);
        blocked.Release();
    }

    // The window is the family's statement about its own flow, not one number applied to every family.
    [Theory]
    [InlineData(null, 30)]
    [InlineData(600, 10)]
    public async Task TheWindowComesFromTheFamilysDeclaration(int? declaredSeconds, int expectedMinutes)
    {
        var binding = await this._harness.SeedConnectionAsync($"Window {declaredSeconds}");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var declaration = ScriptedActionDriver.Declaring(
            window: declaredSeconds is null
                ? null
                : new ProviderInvocationWindow(TimeSpan.FromSeconds(declaredSeconds.Value)));

        var driver = ScriptedActionDriver.Answering(
            ProviderActionResult.OpenUrl("https://auth.example.com/authorize", awaitCompletion: true),
            declaration);

        var outcome = await this.Dispatcher(driver, clock).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(
            clock.GetUtcNow() + TimeSpan.FromMinutes(expectedMinutes),
            outcome.Invocation!.ExpiresAt,
            TimeSpan.FromSeconds(1));
    }

    // A window whose declaration names a configured field reads that connection's value, so two connections of
    // one family can legitimately have different windows.
    [Fact]
    public async Task AWindowDerivedFromAConfiguredFieldReadsThatConnectionsValue()
    {
        var binding = await this._harness.SeedConnectionAsync(
            "Derived window",
            providerSettings: new Dictionary<string, string>(StringComparer.Ordinal) { ["window"] = "120" });

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var declaration = ScriptedActionDriver.Declaring(
            window: new ProviderInvocationWindow(TimeSpan.FromMinutes(10), "window"),
            fields:
            [
                new ProviderDeclaredField("window", "Window in seconds", ProviderFieldKind.Int),
            ]);

        var driver = ScriptedActionDriver.Answering(
            ProviderActionResult.OpenUrl("https://auth.example.com/authorize", awaitCompletion: true),
            declaration);

        var connection = Connection(binding) with
        {
            ProviderSettings = new Dictionary<string, string>(StringComparer.Ordinal) { ["window"] = "120" },
        };

        var outcome = await this.Dispatcher(driver, clock).DispatchAsync(
            connection,
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(
            clock.GetUtcNow() + TimeSpan.FromSeconds(120),
            outcome.Invocation!.ExpiresAt,
            TimeSpan.FromSeconds(1));
    }

    // An operator whose deployment cannot satisfy the family's requirement would otherwise see a bare timeout.
    [Fact]
    public async Task AnExpiredRunSaysWhatItWasWaitingForIncludingThePortAndTheCoLocationRequirement()
    {
        var binding = await this._harness.SeedConnectionAsync("Co-located");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var declaration = ScriptedActionDriver.Declaring(coLocated: true, opensListener: true);
        var driver = ScriptedActionDriver.Answering(
            ProviderActionResult.OpenUrl("https://auth.example.com/authorize", awaitCompletion: true),
            declaration);

        var outcome = await this.Dispatcher(driver, clock).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Contains("1455", outcome.Invocation!.WaitingFor!, StringComparison.Ordinal);
        Assert.Contains("same one", outcome.Invocation.WaitingFor!, StringComparison.Ordinal);

        clock.Advance(ProviderHostLimits.MaximumInvocationWindow + TimeSpan.FromMinutes(1));
        var expired = await this.Invocations(clock).GetAsync(outcome.Invocation.Id);

        Assert.Equal(ProviderInvocationState.Expired, expired!.State);
        Assert.Equal(outcome.Invocation.WaitingFor, expired.TerminalMessage);
    }

    // A host that stops mid-flow leaves runs nothing can report against, because the family went with the
    // process and the socket a completion would arrive on is closed.
    [Fact]
    public async Task EveryRunThisHostStillHadOpenIsExpiredWhenItStops()
    {
        var binding = await this._harness.SeedConnectionAsync("Stopped mid-flow");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.OpenUrl("https://auth.example.com/authorize", awaitCompletion: true));

        using var cancellations = new ProviderInvocationCancellation();
        var invocations = this.Invocations();
        var outcome = await this.Dispatcher(driver, cancellations: cancellations).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid());

        Assert.Equal(ProviderInvocationState.Pending, outcome.Invocation!.State);

        var stopping = new ProviderInvocationShutdownExpiry(
            invocations,
            cancellations,
            NullLogger<ProviderInvocationShutdownExpiry>.Instance);
        await stopping.StopAsync(CancellationToken.None);

        var read = await invocations.GetAsync(outcome.Invocation.Id);

        Assert.Equal(ProviderInvocationState.Expired, read!.State);
        Assert.Equal(outcome.Invocation.WaitingFor, read.TerminalMessage);
    }

    // The add-in written against the contract assembly alone, driven the way an operator drives it rather than
    // by a test calling its steps. What it proves is that the dispatch path reaches a family that knows nothing
    // about this host: the lease, the stored handshake and the address it wants opened all go through the host.
    [Fact]
    public async Task TheAddInWrittenAgainstTheContractAloneRunsThroughTheDispatchPath()
    {
        var binding = await this._harness.SeedConnectionAsync("Example add-in", addInKey: "example/provider");
        var initiator = await this._harness.SeedAdministratorAsync(isPlatformAdministrator: true);
        var driver = new Ai.Providers.ExampleAddIn.ExampleProviderDriver();

        var outcome = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            Ai.Providers.ExampleAddIn.ExampleProviderDriver.ConnectActionId,
            initiator);

        var open = Assert.IsType<ProviderActionOpenUrl>(outcome.Result);
        Assert.StartsWith("https://api.example.com/authorize", open.Url, StringComparison.Ordinal);
        Assert.True(open.AwaitCompletion);
        Assert.Equal(ProviderInvocationState.Pending, outcome.Invocation!.State);

        await using var db = this._harness.CreateContext();
        var entry = db.ProviderKeyedEntries.Single(each => each.AddInKey == "example/provider");
        var lease = db.ProviderResourceLeases.Single(each => each.AddInKey == "example/provider");

        // The host writes the acting principal, so a completion arriving with no session of its own cannot be
        // claimed by anyone other than the administrator who started the flow.
        Assert.Equal(initiator, entry.ActingPrincipalId);
        Assert.Equal(binding.ConnectionProfileId, lease.ConnectionProfileId);
    }

    // The run is opened before the host names the initiating administrator, and that read is the first thing the
    // caller's token reaches. A browser that leaves while it is in flight would otherwise leave a row saying a
    // run is under way until its window lapses, with nothing having started.
    [Fact]
    public async Task ARequestAbandonedBeforeTheFamilyIsInvokedClosesTheRunItOpened()
    {
        var binding = await this._harness.SeedConnectionAsync("Abandoned during setup");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.Completed("Connected."));
        using var abandoned = new CancellationTokenSource();

        var dispatcher = this.Dispatcher(
            driver,
            decorateOwnerRoles: inner => new OwnerRolesFailingToNameTheAdministrator(
                inner,
                ct =>
                {
                    abandoned.Cancel();
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult<string?>(null);
                }));

        var outcome = await dispatcher.DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid(),
            abandoned.Token);

        Assert.Equal(ProviderInvocationState.Failed, outcome.Invocation!.State);
        Assert.Empty(driver.Invoked);

        // Read back rather than taken from the outcome, because the row is what an operator's view polls.
        var stored = await this.Invocations().GetAsync(outcome.Invocation.Id);
        Assert.Equal(ProviderInvocationState.Failed, stored!.State);
    }

    // Recording the terminal state is what the setup handler exists to do, so it does not depend on the caller
    // still waiting: a setup that fails while the request behind it is being abandoned still leaves a closed run.
    [Fact]
    public async Task ASetupFailureUnderAnAbandonedRequestStillRecordsTheTerminalState()
    {
        var binding = await this._harness.SeedConnectionAsync("Failed under an abandoned request");
        var driver = ScriptedActionDriver.Answering(ProviderActionResult.Completed("Connected."));
        using var abandoned = new CancellationTokenSource();

        var dispatcher = this.Dispatcher(
            driver,
            decorateOwnerRoles: inner => new OwnerRolesFailingToNameTheAdministrator(
                inner,
                _ =>
                {
                    abandoned.Cancel();
                    throw new InvalidOperationException("The administrator directory is unavailable.");
                }));

        var outcome = await dispatcher.DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            Guid.NewGuid(),
            abandoned.Token);

        var stored = await this.Invocations().GetAsync(outcome.Invocation!.Id);
        Assert.Equal(ProviderInvocationState.Failed, stored!.State);

        // The host's own failure is not repeated to the operator, so what the row carries names the action and
        // points at the log rather than quoting what the data layer said.
        Assert.DoesNotContain("directory is unavailable", stored.TerminalMessage ?? string.Empty, StringComparison.Ordinal);
    }

    // A run is the record of what an administrator did to a connection: which action, when, and how it ended.
    // Deleting the connection took every one of its runs with it, so the one thing that said what had been done
    // to it went with it.
    [Fact]
    public async Task DeletingAConnectionKeepsTheRunsItWasActedOnBy()
    {
        var binding = await this._harness.SeedConnectionAsync("Retired connection");
        var initiator = Guid.NewGuid();

        var driver = ScriptedActionDriver.Doing((_, _, _) => Task.FromResult(ProviderActionResult.Completed("Connected.")));

        var started = await this.Dispatcher(driver).DispatchAsync(
            Connection(binding),
            driver.Declaration.Key,
            ScriptedActionDriver.ActionId,
            initiator);

        await using (var db = this._harness.CreateContext())
        {
            await db.AiConnectionProfiles
                .Where(profile => profile.Id == binding.ConnectionProfileId)
                .ExecuteDeleteAsync();
        }

        await using var reader = this._harness.CreateContext();
        var kept = await reader.ProviderActionInvocations
            .AsNoTracking()
            .SingleAsync(run => run.Id == started.Invocation!.Id);

        Assert.Null(kept.ConnectionProfileId);
        Assert.Equal("Retired connection", kept.ConnectionDisplayName);
        Assert.Equal(initiator, kept.InitiatingAdminId);
        Assert.Equal(ProviderInvocationState.Completed, kept.State);
        Assert.Equal(ScriptedActionDriver.ActionId, kept.ActionId);
    }

    // The run outlives the connection it acted on, so nothing else would ever clear one whose connection is
    // gone. The sweep runs where a run is opened, so a family whose actions nobody starts accumulates nothing.
    [Fact]
    public async Task OpeningARunClearsTheFamilysRunsPastTheRetentionWindow()
    {
        var binding = await this._harness.SeedConnectionAsync("Long-running family");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var old = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(5), null);
        await invocations.TryCloseAsync(old.Id, ProviderInvocationState.Completed, "Connected.");

        var stillPending = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(5), null);

        clock.Advance(ProviderHostLimits.InvocationHistoryRetention + TimeSpan.FromDays(1));

        var opened = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(5), null);

        await using var db = this._harness.CreateContext();
        var kept = await db.ProviderActionInvocations
            .AsNoTracking()
            .Where(run => run.AddInKey == binding.AddInKey)
            .Select(run => run.Id)
            .ToListAsync();

        Assert.DoesNotContain(old.Id, kept);

        // A run that never reached a terminal state is still pending to whoever reads it, whatever its age.
        Assert.Contains(stillPending.Id, kept);
        Assert.Contains(opened.Id, kept);
    }

    // A finished run inside the window is the record an operator reads back, so it stays.
    [Fact]
    public async Task ARunInsideTheRetentionWindowIsKept()
    {
        var binding = await this._harness.SeedConnectionAsync("Recent family");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var invocations = this.Invocations(clock);

        var recent = await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(5), null);
        await invocations.TryCloseAsync(recent.Id, ProviderInvocationState.Completed, "Connected.");

        clock.Advance(ProviderHostLimits.InvocationHistoryRetention - TimeSpan.FromDays(1));
        await invocations.OpenAsync(binding, "connect", Guid.NewGuid(), TimeSpan.FromMinutes(5), null);

        await using var db = this._harness.CreateContext();
        Assert.True(await db.ProviderActionInvocations.AsNoTracking().AnyAsync(run => run.Id == recent.Id));
    }

    private static Dictionary<string, string> Values(string value)
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ScriptedActionDriver.CallbackInput] = value,
        };
    }

    /// <summary>
    ///     The owner-role reader with one step of the host's post-open setup replaced, so a test can decide what
    ///     happens between the run being opened and the family being invoked.
    /// </summary>
    /// <param name="inner">The reader every other member is answered by.</param>
    /// <param name="describeAdministrator">What naming the initiating administrator does instead.</param>
    private sealed class OwnerRolesFailingToNameTheAdministrator(
        IProviderConnectionOwnerRoles inner,
        Func<CancellationToken, Task<string?>> describeAdministrator) : IProviderConnectionOwnerRoles
    {
        public Task<bool> HoldsOwnerRoleAsync(AiConnectionDto connection, Guid? adminId, CancellationToken ct = default)
        {
            return inner.HoldsOwnerRoleAsync(connection, adminId, ct);
        }

        public Task<bool> HoldsOwnerRoleAsync(
            MeisterProPRDbContext db,
            AiConnectionDto connection,
            Guid? adminId,
            CancellationToken ct = default)
        {
            return inner.HoldsOwnerRoleAsync(db, connection, adminId, ct);
        }

        public string DescribeRequirement(AiConnectionDto connection)
        {
            return inner.DescribeRequirement(connection);
        }

        public Task<string?> DescribeAdministratorAsync(Guid? adminId, CancellationToken ct = default)
        {
            return describeAdministrator(ct);
        }
    }

    private static AiConnectionDto Connection(ProviderAddInBinding binding)
    {
        return new AiConnectionDto(
            binding.ConnectionProfileId,
            null,
            binding.ConnectionDisplayName,
            binding.AddInKey,
            "https://api.example.com/v1",
            binding.AddInKey + ":ApiKey",
            AiDiscoveryMode.ManualOnly,
            IsActive: true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private ProviderActionInvocations Invocations(TimeProvider? timeProvider = null)
    {
        return new ProviderActionInvocations(this._harness.Contexts, timeProvider ?? TimeProvider.System);
    }

    private ProviderActionDispatcher Dispatcher(
        IAiProviderDriver driver,
        TimeProvider? timeProvider = null,
        ProviderInvocationCancellation? cancellations = null,
        TimeSpan? dispatchWait = null,
        Func<IProviderConnectionOwnerRoles, IProviderConnectionOwnerRoles>? decorateOwnerRoles = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var registry = new AiProviderRegistry([driver]);
        var invocations = this.Invocations(clock);
        var signals = cancellations ?? new ProviderInvocationCancellation();
        IProviderConnectionOwnerRoles ownerRoles = new ProviderConnectionOwnerRoles(this._harness.Contexts);
        ownerRoles = decorateOwnerRoles?.Invoke(ownerRoles) ?? ownerRoles;

        var contexts = new ProviderConnectionContextFactory(
            Substitute.For<IHttpMessageHandlerFactory>(),
            this._harness.Contexts,
            this._harness.Codec,
            this._harness.Capabilities,
            ownerRoles,
            registry,
            invocations,
            signals,
            clock);

        return new ProviderActionDispatcher(
            registry,
            invocations,
            signals,
            contexts,
            this._harness.Capabilities,
            ownerRoles,
            clock,
            NullLogger<ProviderActionDispatcher>.Instance,
            dispatchWait);
    }
}
