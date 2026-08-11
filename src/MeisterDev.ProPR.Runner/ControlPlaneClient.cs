// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Net;
using System.Net.Http.Json;
using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner;

/// <summary>What a lease request came back with.</summary>
public enum LeaseOutcome
{
    /// <summary>A job was leased and a manifest returned.</summary>
    Leased = 0,

    /// <summary>Nothing matched. The ordinary answer on a quiet queue, and not an error.</summary>
    NoWork,

    /// <summary>Every entitled runner slot is held, or the installation is not licensed for runners.</summary>
    NoSlot,

    /// <summary>This runner's registration is no longer usable.</summary>
    RegistrationRejected,

    /// <summary>The control plane cannot serve this runner's contract version.</summary>
    ContractRejected,

    /// <summary>The control plane is deliberately draining and is issuing no new leases.</summary>
    Draining,

    /// <summary>The control plane could not be reached or answered unusably.</summary>
    Unreachable,
}

/// <summary>The answer to asking for work.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Manifest">The manifest, when a job was leased.</param>
/// <param name="Detail">Operator-readable detail for a refusal.</param>
public sealed record LeaseResult(LeaseOutcome Outcome, RunnerJobManifest? Manifest = null, string? Detail = null);

/// <summary>
///     The runner's side of the contract, over HTTP.
///     <para>
///         Every refusal the control plane can give is mapped to a named outcome rather than to an
///         exception. A runner has to keep running through all of them: a full slot pool, a quiet queue,
///         and an unreachable control plane are all conditions it should report and retry, not die on. The
///         one that is genuinely terminal for the loop, a contract the control plane cannot serve, is
///         reported so an operator sees why the host is idle instead of watching it crash-loop.
///     </para>
/// </summary>
public sealed partial class ControlPlaneClient(HttpClient http, ILogger<ControlPlaneClient> logger)
{
    /// <summary>Asks for a job, saying how much room this runner has.</summary>
    /// <param name="freeSlots">How many more jobs this runner can take.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<LeaseResult> RequestLeaseAsync(int freeSlots, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                "runners/lease",
                new { freeSlots, contractVersion = RunnerContractVersion.Current },
                ct);

            switch (response.StatusCode)
            {
                case HttpStatusCode.NoContent:
                    return new LeaseResult(LeaseOutcome.NoWork);

                case HttpStatusCode.OK:
                {
                    var manifest = await response.Content.ReadFromJsonAsync<RunnerJobManifest>(ct);
                    return manifest is null
                        ? new LeaseResult(LeaseOutcome.Unreachable, Detail: "The control plane returned an unreadable manifest.")
                        : new LeaseResult(LeaseOutcome.Leased, manifest);
                }

                case HttpStatusCode.Conflict:
                    return new LeaseResult(LeaseOutcome.ContractRejected, Detail: await ReadErrorAsync(response, ct));

                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new LeaseResult(LeaseOutcome.RegistrationRejected, Detail: await ReadErrorAsync(response, ct));

                case HttpStatusCode.TooManyRequests:
                    return new LeaseResult(LeaseOutcome.NoSlot, Detail: await ReadErrorAsync(response, ct));

                // A drain is a deliberate operator action, not a capacity problem. Reporting it as one would
                // have a host report "no slot is free" throughout an upgrade, with no reason given.
                case HttpStatusCode.ServiceUnavailable:
                    return new LeaseResult(LeaseOutcome.Draining, Detail: await ReadErrorAsync(response, ct));

                default:
                    return new LeaseResult(
                        LeaseOutcome.Unreachable,
                        Detail: $"The control plane answered {(int)response.StatusCode}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogControlPlaneUnreachable(logger, ex.Message);
            return new LeaseResult(LeaseOutcome.Unreachable, Detail: ex.Message);
        }
    }

    /// <summary>
    ///     Exchanges an operator-issued registration token for a credential.
    ///     <para>
    ///         The one call that presents no credential, because obtaining one is the point. The token is
    ///         single-use: a host that enrolls twice with the same token is refused the second time, which
    ///         is what stops a leaked token from enrolling a fleet.
    ///     </para>
    /// </summary>
    /// <param name="token">The registration token.</param>
    /// <param name="displayName">The name this host reports for itself.</param>
    /// <param name="tags">The tags it declares.</param>
    /// <param name="ct">The cancellation token.</param>
    public async Task<CredentialResult> EnrollAsync(string token, string displayName, string? tags, CancellationToken ct)
    {
        return await this.RequestCredentialAsync(
            "runners/register",
            new { registrationToken = token, displayName, tags, contractVersion = RunnerContractVersion.Current },
            ct);
    }

    /// <summary>
    ///     Asks for a fresh credential, keeping this runner's identity and the scope the server stamped on
    ///     it. Presented with the credential being replaced, which is what proves it is the same host.
    /// </summary>
    /// <param name="ct">The cancellation token.</param>
    public async Task<CredentialResult> RenewCredentialAsync(CancellationToken ct)
    {
        return await this.RequestCredentialAsync(
            "runners/credential/renew",
            new { contractVersion = RunnerContractVersion.Current },
            ct);
    }

    private async Task<CredentialResult> RequestCredentialAsync(string path, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, payload, ct);
            if (response.IsSuccessStatusCode)
            {
                var issued = await response.Content.ReadFromJsonAsync<CredentialEnvelope>(ct);
                return issued?.Credential is { Length: > 0 } credential
                    ? new CredentialResult(credential, issued.ExpiresAt, null)
                    : new CredentialResult(null, null, "The control plane returned no credential.");
            }

            // A refusal here is terminal for this attempt and not for the process: an operator has to see
            // it, and a host that exited would hide it behind a restart loop.
            var detail = await ReadErrorAsync(response, ct) ?? $"the control plane answered {(int)response.StatusCode}";
            return new CredentialResult(null, null, detail);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new CredentialResult(null, null, ex.Message);
        }
    }

    /// <summary>
    ///     Renews one job's lease and reports what the control plane said about it.
    ///     <para>
    ///         A review runs for many times the lease duration, and a single model call can take minutes, so
    ///         renewal has to happen on its own schedule rather than at pipeline milestones. It is also the
    ///         only channel that reaches a job already in flight: a stop, a supersede, and an exhausted
    ///         budget all arrive as a refused renewal.
    ///     </para>
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseGeneration">The generation this runner holds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="servedBy">The granting replica's advertised base URL, when the manifest carries one.</param>
    public async Task<HeartbeatResult> HeartbeatAsync(Guid jobId, int leaseGeneration, CancellationToken ct, string? servedBy = null)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                Execution.RunnerReplicaAffinity.Resolve(servedBy, "runners/lease/heartbeat"),
                new { jobId, leaseGeneration, contractVersion = RunnerContractVersion.Current },
                ct);

            // A version refusal differs from a transient outage: this control plane can no longer serve this
            // runner's calls at all, so the accurate answer is a lost lease with the skew named, and the
            // review stops now instead of failing one refused execution call at a time.
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var skew = await ReadErrorAsync(response, ct);
                LogHeartbeatVersionRefused(logger, jobId, skew ?? "no detail");
                return new HeartbeatResult(false, false, null, "ContractRejected");
            }

            if (!response.IsSuccessStatusCode)
            {
                // Unreachable rather than lost. A control plane answering 500 is a transient fault, and
                // treating it as a lost lease would abandon a review that is otherwise healthy.
                return HeartbeatResult.NoAnswer;
            }

            var renewed = await response.Content.ReadFromJsonAsync<HeartbeatEnvelope>(ct);
            return renewed is null
                ? HeartbeatResult.NoAnswer
                : new HeartbeatResult(renewed.Accepted, false, renewed.ExpiresAt, renewed.StopReason);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogHeartbeatFailed(logger, jobId, ex.Message);
            return HeartbeatResult.NoAnswer;
        }
    }

    /// <summary>
    ///     Hands a lease back deliberately. Best effort on purpose: a release that fails costs the job a
    ///     lease timeout and nothing more, so it must never keep a draining host from exiting.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseGeneration">The generation this runner holds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="servedBy">
    ///     The granting replica's advertised base URL, when the manifest carries one. The release drops
    ///     per-lease state that only the granting replica holds: the budget scope, the tools and the
    ///     workspace registration. A release routed anywhere else succeeds in the database and leaks all of
    ///     it until the lease would have expired.
    /// </param>
    /// <param name="reason">
    ///     Why the lease is being released, from <see cref="RunnerLeaseReleaseReasons" />. A failure spends
    ///     one of the job's reclaim attempts and a drain spends none, so the two must not be reported alike.
    /// </param>
    public async Task<bool> ReleaseLeaseAsync(
        Guid jobId,
        int leaseGeneration,
        CancellationToken ct,
        string? servedBy = null,
        string reason = RunnerLeaseReleaseReasons.Drain)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(
                Execution.RunnerReplicaAffinity.Resolve(servedBy, "runners/lease/release"),
                new { jobId, leaseGeneration, reason },
                ct);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            LogReleaseFailed(logger, jobId, ex.Message);
            return false;
        }
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<RunnerContractError>(ct);
            return error?.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [LoggerMessage(EventId = 6104, Level = LogLevel.Warning, Message = "The lease for job {JobId} could not be renewed: {Reason}")]
    private static partial void LogHeartbeatFailed(ILogger logger, Guid jobId, string reason);

    [LoggerMessage(
        EventId = 6105,
        Level = LogLevel.Error,
        Message = "The control plane refused job {JobId}'s renewal for version skew; stopping the review: {Detail}")]
    private static partial void LogHeartbeatVersionRefused(ILogger logger, Guid jobId, string detail);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "The control plane could not be reached: {Reason}")]
    private static partial void LogControlPlaneUnreachable(ILogger logger, string reason);

    [LoggerMessage(
        EventId = 6102, Level = LogLevel.Warning, Message = "Releasing the lease on job {JobId} failed; it will be reclaimed on expiry instead: {Reason}")]
    private static partial void LogReleaseFailed(ILogger logger, Guid jobId, string reason);

    private sealed record HeartbeatEnvelope(bool Accepted, DateTimeOffset? ExpiresAt, string StopReason);

    private sealed record CredentialEnvelope(Guid RunnerId, string? Credential, DateTimeOffset? ExpiresAt);
}

/// <summary>
///     What one heartbeat learned.
/// </summary>
/// <param name="Held">Whether this runner still holds the lease.</param>
/// <param name="Unreachable">
///     Whether the answer is unknown rather than negative. A control plane that cannot be reached has not
///     said the lease is gone, and abandoning a running review on a transient fault would waste everything
///     it has already spent.
/// </param>
/// <param name="ExpiresAt">When the renewed lease expires, or null when it was not renewed.</param>
/// <param name="StopReason">Why the renewal was refused, as a stable token.</param>
public sealed record HeartbeatResult(bool Held, bool Unreachable, DateTimeOffset? ExpiresAt, string StopReason)
{
    /// <summary>The control plane could not be asked.</summary>
    public static HeartbeatResult NoAnswer { get; } = new(false, true, null, string.Empty);
}

/// <summary>
///     What asking for a credential produced.
/// </summary>
/// <param name="Credential">The issued credential, when one was.</param>
/// <param name="ExpiresAt">When it must be renewed by.</param>
/// <param name="Refusal">Why it was not issued, in words an operator can act on.</param>
public sealed record CredentialResult(string? Credential, DateTimeOffset? ExpiresAt, string? Refusal)
{
    /// <summary>Whether a credential was issued.</summary>
    public bool Succeeded => this.Credential is not null;
}
