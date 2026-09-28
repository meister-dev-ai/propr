// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Structured failure captured when a local review workspace cannot be prepared or used safely.
/// </summary>
/// <param name="Stage">The preparation step the failure occurred in.</param>
/// <param name="Code">A stable token for the kind of failure, for logs and protocol events.</param>
/// <param name="Message">What failed, in the words reported on the job.</param>
/// <param name="Retryable">Whether another attempt can succeed without an operator changing anything.</param>
/// <param name="FallbackApplied">Whether the caller continued without the workspace.</param>
/// <param name="RepositorySizeBreach">
///     The size and the bound when preparation was stopped because the repository passed the client's
///     repository-size limit, and null for every other failure. The caller turns it into an admission
///     refusal, which needs both numbers.
/// </param>
public sealed record ReviewWorkspaceFailure(
    string Stage,
    string Code,
    string Message,
    bool Retryable,
    bool FallbackApplied,
    ReviewRepositorySizeBreach? RepositorySizeBreach = null);

/// <summary>
///     How large a repository had grown when preparation stopped, and the bound it passed.
/// </summary>
/// <param name="MeasuredMegabytes">
///     The size measured in mebibytes (1,048,576 bytes) at the sample that passed the bound. The transfer was
///     stopped at that point, so the repository is at least this large.
/// </param>
/// <param name="LimitMegabytes">The client's repository-size bound, in the same unit.</param>
public sealed record ReviewRepositorySizeBreach(int MeasuredMegabytes, int LimitMegabytes);
