// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Runner.Contracts;
using Microsoft.Extensions.Time.Testing;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     The loop an operator depends on being right when everything else is broken: it must respect its own
///     capacity, keep running through every refusal the control plane can give, and hand back what it holds
///     when it is told to stop.
/// </summary>
public sealed class RunnerWorkLoopTests
{
    // Capacity belongs to the asking side. A full runner that asked anyway would make the control plane's
    // answer depend on capacity it cannot see, which is the coordination this design exists to avoid.
    [Fact]
    public async Task AFullRunner_StopsAskingForWork()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        var executor = new BlockingExecutor();
        using var loop = CreateLoop(handler, executor, capacity: 2);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 2, "The runner never filled its capacity.");
        var asksAtCapacity = handler.LeaseRequests.Count;
        await Task.Delay(150, CancellationToken.None);

        Assert.Equal(2, loop.InFlightCount);
        Assert.Equal(asksAtCapacity, handler.LeaseRequests.Count);

        executor.Release();
        await loop.StopAsync(CancellationToken.None);
    }

    // Every ask carries the free-slot count, which is what lets the control plane answer without tracking
    // anybody's capacity.
    [Fact]
    public async Task EveryAsk_SaysHowMuchRoomThereIs()
    {
        var handler = new RecordingHandler();
        handler.AlwaysNoWork();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 3);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.LeaseRequests.Count >= 1, "The runner never asked for work.");
        await loop.StopAsync(CancellationToken.None);

        Assert.All(handler.LeaseRequests, request => Assert.Equal(3, request.FreeSlots));
        Assert.All(handler.LeaseRequests, request => Assert.Equal(RunnerContractVersion.Current, request.ContractVersion));
    }

    // A control plane that is down must not take the host with it. An operator diagnosing an outage needs a
    // running container reporting that it cannot connect, not a crash loop.
    [Fact]
    public async Task AnUnreachableControlPlane_KeepsTheRunnerAliveAndRetrying()
    {
        var handler = new RecordingHandler();
        handler.AlwaysThrow();
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, health: health);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => health.Read().Current == RunnerHealthState.Status.Disconnected, "The runner never reported being disconnected.");

        // Still asking. Reporting the state once and then exiting would satisfy the assertion above, and
        // is exactly the behaviour this test exists to rule out.
        var asksSoFar = handler.LeaseRequests.Count;
        await WaitUntilAsync(() => handler.LeaseRequests.Count > asksSoFar, "The runner stopped retrying after reporting disconnected.");

        Assert.Equal(RunnerHealthState.Status.Disconnected, health.Read().Current);

        await loop.StopAsync(CancellationToken.None);
    }

    // Version skew during a rolling upgrade. The refusal is terminal for leasing and must be visible, but
    // exiting would hide it behind a restart loop and tell an operator nothing.
    [Fact]
    public async Task ARefusedContractVersion_IsReportedRatherThanFatal()
    {
        var handler = new RecordingHandler();
        handler.AlwaysRespond(HttpStatusCode.Conflict, new RunnerContractError(RunnerContractError.UnsupportedContractVersion, "too new"));
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, health: health);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => health.Read().Current == RunnerHealthState.Status.Refused, "The runner never reported being refused.");

        var asksSoFar = handler.LeaseRequests.Count;
        await WaitUntilAsync(() => handler.LeaseRequests.Count > asksSoFar, "The runner stopped retrying after a refused contract.");

        var (status, detail) = health.Read();
        Assert.Equal(RunnerHealthState.Status.Refused, status);
        Assert.Equal("too new", detail);

        await loop.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AFullSlotPool_LeavesTheRunnerIdleRatherThanFailing()
    {
        var handler = new RecordingHandler();
        handler.AlwaysRespond(HttpStatusCode.TooManyRequests, new RunnerContractError(RunnerContractError.SlotLimitReached, "all 3 slots held"));
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, health: health);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => health.Read().Detail == "all 3 slots held", "The runner never reported the slot refusal.");

        var asksSoFar = handler.LeaseRequests.Count;
        await WaitUntilAsync(() => handler.LeaseRequests.Count > asksSoFar, "The runner stopped asking once the slot pool was full.");

        Assert.Equal(RunnerHealthState.Status.Idle, health.Read().Current);

        await loop.StopAsync(CancellationToken.None);
    }

    // A planned restart must cost its jobs nothing. A lease left to expire keeps its job unavailable for
    // the whole lease duration and spends one of that job's reclaim attempts on something that was not a
    // failure at all.
    [Fact]
    public async Task ShuttingDown_ReleasesEveryLeaseItHolds()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        var executor = new BlockingExecutor();
        using var loop = CreateLoop(handler, executor, capacity: 2);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 2, "The runner never filled its capacity.");

        // Deliberately still holding both when shutdown starts: releasing first would leave nothing to
        // drain and the test would pass without exercising the drain at all.
        await loop.StopAsync(CancellationToken.None);

        // Matched by identity, not counted. Two releases of the wrong job, or of the right jobs at the
        // wrong generation, would satisfy a count and still be a lease handed back that nobody held.
        Assert.Equal(
            handler.LeasedJobs.Select(j => (j.JobId, j.LeaseGeneration)).OrderBy(x => x.JobId).ToArray(),
            handler.ReleasedLeases.OrderBy(x => x.JobId).ToArray());
    }

    // Exactly once. A job cancelled by the drain is not a failure, and treating it as one would release a
    // lease the drain already returned, handing back something another runner may by then hold.
    [Fact]
    public async Task ADrainedJob_HasItsLeaseReturnedExactlyOnce()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        using var loop = CreateLoop(handler, new BlockingExecutor(), capacity: 1);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 1, "The runner never took a job.");
        await loop.StopAsync(CancellationToken.None);
        await Task.Delay(100, CancellationToken.None);

        var held = Assert.Single(handler.LeasedJobs);
        Assert.Equal([(held.JobId, held.LeaseGeneration)], handler.ReleasedLeases);
    }

    [Fact]
    public async Task AnIdleRunnerShuttingDown_ReleasesNothing()
    {
        var handler = new RecordingHandler();
        handler.AlwaysNoWork();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.LeaseRequests.Count >= 1, "The runner never asked for work.");
        await loop.StopAsync(CancellationToken.None);

        Assert.Empty(handler.ReleaseRequests);
    }

    // A job that throws must cost the job its lease and nothing else. Taking the loop down with it would
    // strand every other job the runner holds.
    [Fact]
    public async Task AJobThatFails_ReturnsItsLeaseAndLeavesTheLoopRunning()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        using var loop = CreateLoop(handler, new ThrowingExecutor(), capacity: 1);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.ReleaseRequests.Count >= 1, "The failed job never had its lease returned.");
        await WaitUntilAsync(() => handler.LeaseRequests.Count >= 2, "The loop stopped asking after a failure.");

        await loop.StopAsync(CancellationToken.None);
    }


    // A review outlives its lease many times over, so renewal has to run beside the work rather than at
    // pipeline milestones. Without this a healthy review loses its job to an expiry it never noticed.
    [Fact]
    public async Task AJobInFlight_HasItsLeaseRenewedWhileItRuns()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero));
        using var loop = CreateLoop(handler, new BlockingExecutor(), capacity: 1, time: time);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 1, "The runner never took a job.");

        time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => !handler.Heartbeats.IsEmpty, "The lease was never renewed.");

        var held = Assert.Single(handler.LeasedJobs);
        Assert.Contains((held.JobId, held.LeaseGeneration), handler.Heartbeats);

        await loop.StopAsync(CancellationToken.None);
    }

    // The heartbeat is the only channel that reaches a job already running. A refused renewal means the
    // job is somebody else's now, and continuing would have two runners reviewing the same revision.
    [Fact]
    public async Task ALeaseLostMidReview_StopsTheJobItBelongedTo()
    {
        var handler = new RecordingHandler
        {
            HeartbeatResponder = () => RecordingHandler.Json(
                HttpStatusCode.OK, new { accepted = false, expiresAt = (DateTimeOffset?)null, stopReason = "LeaseNoLongerHeld" }),
        };
        handler.AlwaysLease();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero));
        using var loop = CreateLoop(handler, new BlockingExecutor(), capacity: 1, time: time);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 1, "The runner never took a job.");

        time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => loop.InFlightCount == 0, "The job kept running after its lease was gone.");

        // Not released: the lease is already somebody else's, and handing it back would return a job the
        // holder is working on.
        Assert.Empty(handler.ReleaseRequests);

        await loop.StopAsync(CancellationToken.None);
    }

    // An unreachable control plane has not said the lease is gone. Abandoning the review on a transient
    // fault would throw away everything it has already spent.
    [Fact]
    public async Task AnUnansweredHeartbeat_DoesNotAbandonTheReview()
    {
        var handler = new RecordingHandler
        {
            HeartbeatResponder = () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        handler.AlwaysLease();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero));
        using var loop = CreateLoop(handler, new BlockingExecutor(), capacity: 1, time: time);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.InFlightCount == 1, "The runner never took a job.");

        time.Advance(TimeSpan.FromSeconds(25));
        await WaitUntilAsync(() => !handler.Heartbeats.IsEmpty, "The lease was never renewed.");

        Assert.Equal(1, loop.InFlightCount);

        await loop.StopAsync(CancellationToken.None);
    }


    // A host ships with a token, not a credential. Without this it can start, look healthy, and never be
    // able to make a single call — every other operation presents the credential enrollment produces.
    [Fact]
    public async Task AHostWithOnlyARegistrationToken_EnrolsBeforeAskingForWork()
    {
        var handler = new RecordingHandler();
        handler.AlwaysNoWork();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, credential: null, registrationToken: "operator-issued");

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.LeaseRequests.Count >= 1, "The runner never asked for work.");

        Assert.Equal(["operator-issued"], handler.Enrollments);

        await loop.StopAsync(CancellationToken.None);
    }

    // Enrollment is once. A host that re-enrolled on every cycle would burn a single-use token immediately
    // and then be unable to work at all.
    [Fact]
    public async Task AnEnrolledHost_DoesNotEnrolAgain()
    {
        var handler = new RecordingHandler();
        handler.AlwaysNoWork();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, credential: null, registrationToken: "operator-issued");

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => handler.LeaseRequests.Count >= 3, "The runner did not keep asking for work.");

        Assert.Single(handler.Enrollments);

        await loop.StopAsync(CancellationToken.None);
    }

    // Neither a credential nor a way to get one is a configuration fault an operator has to see. Exiting
    // would hide it behind whatever restarts the container.
    [Fact]
    public async Task AHostWithNeitherCredentialNorToken_SaysSoAndKeepsRunning()
    {
        var handler = new RecordingHandler();
        handler.AlwaysNoWork();
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, health: health, credential: null, registrationToken: null);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => health.Read().Current == RunnerHealthState.Status.Refused,
            "The runner never reported that it cannot enrol.");

        Assert.Empty(handler.LeaseRequests);
        Assert.Empty(handler.Enrollments);

        await loop.StopAsync(CancellationToken.None);
    }

    // A refused token is an operator's problem, not a crash. The host keeps saying so.
    [Fact]
    public async Task ARefusedEnrollment_IsReportedAndRetried()
    {
        var handler = new RecordingHandler
        {
            EnrollmentResponder = () => RecordingHandler.Json(
                HttpStatusCode.Unauthorized,
                new RunnerContractError(RunnerContractError.RegistrationRevoked, "That token has been used.")),
        };
        handler.AlwaysNoWork();
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, health: health, credential: null, registrationToken: "spent");

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(
            () => health.Read().Current == RunnerHealthState.Status.Refused,
            "The runner never reported the refusal.");

        Assert.Empty(handler.LeaseRequests);

        await loop.StopAsync(CancellationToken.None);
    }


    // Measured at roughly nine hundred requests a second against a rate-limited endpoint before this was
    // fixed: the refusal path took an early `continue` that skipped the loop's only wait. With a clock
    // that does not advance, a paced loop enrols exactly once.
    [Fact]
    public async Task AHostThatCannotEnrol_WaitsBetweenAttemptsInsteadOfSpinning()
    {
        var handler = new RecordingHandler
        {
            EnrollmentResponder = () => RecordingHandler.Json(
                HttpStatusCode.TooManyRequests,
                new RunnerContractError("rate_limited", "Slow down.")),
        };
        handler.AlwaysNoWork();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 8, 9, 0, 0, TimeSpan.Zero));
        using var loop = CreateLoop(handler, new NoopExecutor(), capacity: 1, time: time, credential: null, registrationToken: "rejected");

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !handler.Enrollments.IsEmpty, "The runner never tried to enrol.");

        // The clock is frozen, so a loop that waits cannot reach a second attempt however long this runs.
        await Task.Delay(300, CancellationToken.None);
        Assert.Single(handler.Enrollments);

        await loop.StopAsync(CancellationToken.None);
    }

    // A failure spends one of the job's reclaim attempts; a drain costs it nothing. The release has to
    // say which, or a host that fails every attempt hands back cleanly and re-leases its own failure
    // forever at full relay cost.
    [Fact]
    public async Task AFailedJobsRelease_SaysItFailed()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLease();
        using var loop = CreateLoop(handler, new ThrowingExecutor(), capacity: 1);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !handler.ReleaseRequests.IsEmpty, "The runner never released the failed job's lease.");
        await loop.StopAsync(CancellationToken.None);

        var release = JsonDocument.Parse(handler.ReleaseRequests.First());
        Assert.Equal(RunnerLeaseReleaseReasons.Failure, release.RootElement.GetProperty("reason").GetString());
    }

    // The credential rides on every call. An advertised replica address that is not https would leak it
    // on every proxied tool call, relayed completion, and ingest batch — so the job is never started, the
    // lease goes straight back, and the operator sees the misconfiguration instead of a slow review.
    [Fact]
    public async Task AnInsecureAdvertisedReplica_HasItsLeaseHandedStraightBack()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLeaseFrom("http://replica-2.internal:8080");
        var executor = new CountingExecutor();
        var health = new RunnerHealthState();
        using var loop = CreateLoop(handler, executor, capacity: 1, health: health);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !handler.ReleasedLeases.IsEmpty, "The runner never handed the lease back.");
        await loop.StopAsync(CancellationToken.None);

        Assert.Equal(0, executor.Calls);
        Assert.Equal(RunnerHealthState.Status.Refused, health.Read().Current);
        var granted = handler.LeasedJobs.First();
        Assert.Contains(handler.ReleasedLeases, released => released.JobId == granted.JobId);
    }

    // The release drops per-lease state only the granting replica holds. Routed through the load balancer
    // it succeeds in the database and leaks the budget scope, the tools, and the workspace registration
    // until the lease would have expired anyway.
    [Fact]
    public async Task AFailedJobsLease_IsReturnedToTheReplicaThatGrantedIt()
    {
        var handler = new RecordingHandler();
        handler.AlwaysLeaseFrom("https://replica-2.invalid");
        using var loop = CreateLoop(handler, new ThrowingExecutor(), capacity: 1);

        await loop.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => !handler.ReleaseUris.IsEmpty, "The runner never released the failed job's lease.");
        await loop.StopAsync(CancellationToken.None);

        Assert.All(handler.ReleaseUris, uri => Assert.Equal("replica-2.invalid", uri.Host));
    }

    private static RunnerWorkLoop CreateLoop(
        RecordingHandler handler,
        IRunnerJobExecutor executor,
        int capacity,
        RunnerHealthState? health = null,
        TimeProvider? time = null,
        string? credential = "already-enrolled",
        string? registrationToken = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://control-plane.invalid/") };
        var options = Options.Create(
            new RunnerHostOptions
            {
                ControlPlaneUrl = "http://control-plane.invalid",
                Credential = credential,
                RegistrationToken = registrationToken,
                Capacity = capacity,
                PollIntervalSeconds = 1,
                MaxBackoffSeconds = 5,
            });

        return new RunnerWorkLoop(
            new ControlPlaneClient(http, NullLogger<ControlPlaneClient>.Instance),
            // Already enrolled, so the loop goes straight to asking for work. Enrollment has its own tests.
            new RunnerCredentialStore(options),
            executor,
            // Pointed at a directory of this test's own, so purging is real but touches nothing else.
            new WorkspaceFetcher(options, NullLogger<WorkspaceFetcher>.Instance),
            options,
            health ?? new RunnerHealthState(),
            time ?? TimeProvider.System,
            NullLogger<RunnerWorkLoop>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20, CancellationToken.None);
        }

        Assert.Fail(because);
    }

    private sealed record LeaseAsk(int FreeSlots, int ContractVersion);

    /// <summary>A control plane that answers however the test needs and remembers what it was asked.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage>? _leaseResponder;

        public ConcurrentBag<LeaseAsk> LeaseRequests { get; } = [];

        public ConcurrentBag<string> ReleaseRequests { get; } = [];

        /// <summary>Every enrollment attempt seen, as the token presented.</summary>
        public ConcurrentBag<string> Enrollments { get; } = [];

        /// <summary>How the control plane answers an enrollment. Issues a credential unless a test says otherwise.</summary>
        public Func<HttpResponseMessage> EnrollmentResponder { get; set; } = () =>
            Json(HttpStatusCode.OK, new { runnerId = Guid.NewGuid(), credential = "issued-credential", expiresAt = (DateTimeOffset?)null });

        /// <summary>Job and generation of every renewal seen.</summary>
        public ConcurrentBag<(Guid JobId, int LeaseGeneration)> Heartbeats { get; } = [];

        /// <summary>How the control plane answers a renewal. Accepted, unless a test says otherwise.</summary>
        public Func<HttpResponseMessage> HeartbeatResponder { get; set; } = () =>
            Json(HttpStatusCode.OK, new { accepted = true, expiresAt = (DateTimeOffset?)null, stopReason = "None" });

        /// <summary>Manifests actually handed out, so a release can be matched against what was held.</summary>
        public ConcurrentBag<RunnerJobManifest> LeasedJobs { get; } = [];

        /// <summary>Job and generation of every release seen.</summary>
        public ConcurrentBag<(Guid JobId, int LeaseGeneration)> ReleasedLeases { get; } = [];

        /// <summary>The full URI of every release, so a test can see which replica it went to.</summary>
        public ConcurrentBag<Uri> ReleaseUris { get; } = [];

        public void AlwaysNoWork() => this._leaseResponder = _ => new HttpResponseMessage(HttpStatusCode.NoContent);

        public void AlwaysThrow() => this._leaseResponder = _ => throw new HttpRequestException("connection refused");

        public void AlwaysLease() => this._leaseResponder = _ =>
        {
            var manifest = Manifest();
            this.LeasedJobs.Add(manifest);
            return Json(HttpStatusCode.OK, manifest);
        };

        /// <summary>Leases jobs whose manifests name an advertised replica address.</summary>
        public void AlwaysLeaseFrom(string servedBy) => this._leaseResponder = _ =>
        {
            var manifest = Manifest() with { ServedBy = servedBy };
            this.LeasedJobs.Add(manifest);
            return Json(HttpStatusCode.OK, manifest);
        };

        public void AlwaysRespond(HttpStatusCode status, RunnerContractError error) =>
            this._leaseResponder = _ => Json(status, error);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);

            if (path.EndsWith("/lease", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(body);
                this.LeaseRequests.Add(
                    new LeaseAsk(
                        document.RootElement.GetProperty("freeSlots").GetInt32(),
                        document.RootElement.GetProperty("contractVersion").GetInt32()));
                return this._leaseResponder!(request);
            }

            if (path.EndsWith("/runners/register", StringComparison.Ordinal))
            {
                using var enrollment = JsonDocument.Parse(body);
                this.Enrollments.Add(enrollment.RootElement.GetProperty("registrationToken").GetString() ?? string.Empty);
                return this.EnrollmentResponder();
            }

            if (path.EndsWith("/lease/heartbeat", StringComparison.Ordinal))
            {
                using var beat = JsonDocument.Parse(body);
                this.Heartbeats.Add(
                    (
                        beat.RootElement.GetProperty("jobId").GetGuid(),
                        beat.RootElement.GetProperty("leaseGeneration").GetInt32()));
                return this.HeartbeatResponder();
            }

            if (path.EndsWith("/lease/release", StringComparison.Ordinal))
            {
                this.ReleaseRequests.Add(body);
                this.ReleaseUris.Add(request.RequestUri!);
                using var released = JsonDocument.Parse(body);
                this.ReleasedLeases.Add(
                    (
                        released.RootElement.GetProperty("jobId").GetGuid(),
                        released.RootElement.GetProperty("leaseGeneration").GetInt32()));
                return Json(HttpStatusCode.OK, new { released = true });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        internal static HttpResponseMessage Json(HttpStatusCode status, object payload)
        {
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    Encoding.UTF8,
                    "application/json"),
            };
        }

        private static RunnerJobManifest Manifest()
        {
            return new RunnerJobManifest(
                RunnerContractVersion.Current,
                Guid.NewGuid(),
                Guid.NewGuid(),
                1,
                new RunnerReviewTarget(
                    "forgejo", "https://forge.invalid", "project", "repo", "repo", "1", 1, 1,
                    "title", null, "feature", "main", "head", "base", [], []),
                new RunnerWorkspaceReference("runners/execution/workspace/x/1", "head", "base", 1024),
                new RunnerModelBinding("reviewer", "model", "OpenAi", "None", null, null, null, false, true, true),
                [],
                new RunnerPromptConfiguration(null, null, new Dictionary<string, string>()),
                [],
                [],
                null,
                new RunnerTraceContext(null, null));
        }
    }

    private sealed class NoopExecutor : IRunnerJobExecutor
    {
        public Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingExecutor : IRunnerJobExecutor
    {
        private int _calls;

        public int Calls => this._calls;

        public Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct)
        {
            Interlocked.Increment(ref this._calls);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExecutor : IRunnerJobExecutor
    {
        public Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct) =>
            throw new InvalidOperationException("the review blew up");
    }

    /// <summary>Holds every job open until released, so capacity can be observed at its limit.</summary>
    private sealed class BlockingExecutor : IRunnerJobExecutor
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(RunnerJobManifest manifest, CancellationToken ct) => this._gate.Task.WaitAsync(ct);

        public void Release() => this._gate.TrySetResult();
    }
}
