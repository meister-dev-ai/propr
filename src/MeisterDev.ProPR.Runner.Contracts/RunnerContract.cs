// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeisterDev.ProPR.Runner.Contracts;

/// <summary>
///     The operations an out-of-process review executor and the control plane exchange. Named here so both
///     sides refer to one list rather than each inventing its own, and so a reader can see the whole surface
///     in one place.
/// </summary>
public static class RunnerContractOperations
{
    /// <summary>Enroll with an operator-issued registration token and receive a runner credential.</summary>
    public const string Register = "runner.register";

    /// <summary>Renew the runner credential before it expires, keeping the same identity and scope.</summary>
    public const string RenewCredential = "runner.credential.renew";

    /// <summary>Ask for a job. Answered with a manifest, or with nothing when none matches.</summary>
    public const string Lease = "runner.lease";

    /// <summary>Renew the lease and receive the control plane's directive in return.</summary>
    public const string Heartbeat = "runner.heartbeat";

    /// <summary>Hand a lease back deliberately, so a planned shutdown costs the job nothing.</summary>
    public const string ReleaseLease = "runner.lease.release";

    /// <summary>Fetch repository content from the control plane's mirror, authorized per lease.</summary>
    public const string FetchWorkspace = "runner.workspace.fetch";

    /// <summary>Call a review-context tool that needs a credential the executor does not hold.</summary>
    public const string ToolCall = "runner.tools.call";

    /// <summary>Reconsider one file's draft result against the thread-memory store.</summary>
    public const string MemoryReconsider = "runner.memory.reconsider";

    /// <summary>Relay a chat completion, where usage is captured and the hard cap is enforced.</summary>
    public const string ChatRelay = "runner.ai.chat";

    /// <summary>Ship a batch of trace events, per-file results, and spend.</summary>
    public const string Ingest = "runner.ingest";

    /// <summary>Submit findings for the control plane to deduplicate and publish.</summary>
    public const string SubmitFindings = "runner.findings.submit";
}

/// <summary>
///     Why a lease is being handed back. A drain and a failure are different operational events — one is
///     a planned shutdown that must cost the job nothing, the other spends one of the job's reclaim
///     attempts — and a release that did not say which let a failing host hand back and re-lease the same
///     job forever.
/// </summary>
public static class RunnerLeaseReleaseReasons
{
    /// <summary>A planned handback: shutdown, drain, or scale-in. Costs the job nothing.</summary>
    public const string Drain = "drain";

    /// <summary>The attempt failed. Counts against the job's reclaim budget like an expiry would.</summary>
    public const string Failure = "failure";
}

/// <summary>
///     The strict serializer the contract's tests round-trip with.
/// </summary>
public static class RunnerContractJson
{
    /// <summary>
    ///     Refuses unknown members, which is what makes a round-trip test detect a field the schema lost.
    ///     <para>
    ///         Deliberately NOT what production readers use. A reader that refused unknown fields would
    ///         reject the whole manifest the moment an older peer met a newer one's additive field — the
    ///         exact deploy the compatibility window exists to survive. Production readers tolerate what
    ///         they do not know; a version that actually changes shapes moves
    ///         <see cref="RunnerContractVersion.OldestManifestCompatible" /> instead, and the version gate
    ///         refuses below it.
    ///     </para>
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>
///     A refusal from the control plane, in a shape the executor can act on rather than parse out of prose.
/// </summary>
/// <param name="Code">A stable machine-readable code.</param>
/// <param name="Message">An operator-readable explanation.</param>
public sealed record RunnerContractError(string Code, string Message)
{
    /// <summary>The executor speaks a contract version this control plane cannot serve.</summary>
    public const string UnsupportedContractVersion = "unsupported_contract_version";

    /// <summary>The caller does not hold the lease it is presenting, or holds a superseded generation.</summary>
    public const string LeaseNotHeld = "lease_not_held";

    /// <summary>The runner's registration has been revoked.</summary>
    public const string RegistrationRevoked = "registration_revoked";

    /// <summary>The job's hard budget cap has been reached, so no further completions are served.</summary>
    public const string BudgetCapReached = "budget_cap_reached";

    /// <summary>The request exceeded a payload or batch ceiling.</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>No lease is available: the entitled concurrent-job count is already in use.</summary>
    public const string SlotLimitReached = "slot_limit_reached";

    /// <summary>Builds the refusal for an executor whose contract version cannot be served.</summary>
    public static RunnerContractError ForUnsupportedVersion(int reported)
    {
        return new RunnerContractError(
            UnsupportedContractVersion,
            RunnerContractVersion.DescribeMismatch(reported));
    }
}
