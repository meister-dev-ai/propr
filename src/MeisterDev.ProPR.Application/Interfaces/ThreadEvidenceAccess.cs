// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     How one thread evaluation retrieves code it was not supplied with up front.
/// </summary>
/// <remarks>
///     <para>
///         A comment is anchored to the location where a problem was observed, which is often not the
///         location that has to change. A finding raised on an interface declaration, stating that the
///         implementation does not validate its arguments, refers to two files, and an evaluation supplied
///         only with the first cannot observe the change that resolves it.
///     </para>
///     <para>
///         The evaluation may request additional files once. It cannot examine what is returned and request
///         again, so no thread can traverse the pull request through repeated requests, however large the
///         pull request is. Requests are limited to files this pull request changed, which also confines a
///         comment crafted to direct the reviewer elsewhere to code the pull request already exposes to
///         anyone reading it.
///     </para>
/// </remarks>
/// <param name="FetchFileDiffAsync">
///     Retrieves one file's diff by repository-relative path, returning <see langword="null" /> when the
///     provider has no content for that path.
/// </param>
/// <param name="MaxContextTokens">
///     The context window of the model performing the evaluation, used to determine how much retrieved code
///     fits. <see langword="null" /> falls back to the built-in default window.
/// </param>
/// <param name="TokenizerName">
///     The model's tokenizer, so the fit is measured rather than estimated. <see langword="null" /> falls
///     back to the codebase's character heuristic.
/// </param>
/// <param name="OnRequestRejected">
///     Invoked with each path that was requested and refused because this pull request never changed it. A
///     refusal indicates that something in the conversation is directing the reviewer at code outside the
///     change, so it is recorded even though the thread is still answered.
/// </param>
public sealed record ThreadEvidenceAccess(
    Func<string, CancellationToken, Task<ChangedFile?>> FetchFileDiffAsync,
    int? MaxContextTokens = null,
    string? TokenizerName = null,
    Action<string>? OnRequestRejected = null);
