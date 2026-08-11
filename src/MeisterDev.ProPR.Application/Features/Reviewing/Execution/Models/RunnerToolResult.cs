// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     The answer to a proxied tool call: either the value the in-process tool would have returned, or a
///     refusal saying why the call was not served.
///     <para>
///         A refusal is not an error the pipeline handles. It means the caller should not be calling at all,
///         because it no longer holds the job, and it must stop rather than carry on with a degraded answer.
///         That is why this is a distinct outcome rather than an empty result: an executor that cannot tell
///         "there are no linked items" from "you are not allowed to ask" will review the wrong thing.
///     </para>
/// </summary>
/// <typeparam name="T">The value the tool returns.</typeparam>
public sealed record RunnerToolResult<T>
{
    private RunnerToolResult(T? value, RunnerCallRefusal refusal, bool unavailable, string? fault)
    {
        this.Value = value;
        this.Refusal = refusal;
        this.Unavailable = unavailable;
        this.Fault = fault;
    }

    /// <summary>The tool's result when the call was served.</summary>
    public T? Value { get; }

    /// <summary>Why the call was refused, or <see cref="RunnerCallRefusal.None" /> when it was served.</summary>
    public RunnerCallRefusal Refusal { get; }

    /// <summary>
    ///     True when the tool is not part of this review's surface at all: the code-knowledge service is
    ///     switched off, so the in-process path does not offer these tools either. Distinct from a refusal,
    ///     which is about the caller, and from an empty answer, which is about the repository.
    /// </summary>
    public bool Unavailable { get; }

    /// <summary>
    ///     What went wrong in transit, when the call received no answer at all: a dropped connection, a 5xx,
    ///     an unreadable body. A distinct state on purpose, because a transient failure during a rolling
    ///     restart read as "not offered" reported to the reviewer that the pull request changed no files, and
    ///     the reviewer acted on that.
    /// </summary>
    public string? Fault { get; }

    /// <summary>Whether the call was served.</summary>
    public bool IsServed => this.Refusal == RunnerCallRefusal.None && !this.Unavailable && this.Fault is null;

    /// <summary>A served call.</summary>
    public static RunnerToolResult<T> Served(T value)
    {
        return new RunnerToolResult<T>(value, RunnerCallRefusal.None, false, null);
    }

    /// <summary>A call the caller was not entitled to make.</summary>
    public static RunnerToolResult<T> Refused(RunnerCallRefusal refusal)
    {
        return new RunnerToolResult<T>(default, refusal, false, null);
    }

    /// <summary>A tool this installation does not offer, because the service behind it is switched off.</summary>
    public static RunnerToolResult<T> NotOffered()
    {
        return new RunnerToolResult<T>(default, RunnerCallRefusal.None, true, null);
    }

    /// <summary>A call that never got an answer: a transport failure, a server error, an unreadable body.</summary>
    public static RunnerToolResult<T> Faulted(string reason)
    {
        return new RunnerToolResult<T>(default, RunnerCallRefusal.None, false, reason);
    }
}
