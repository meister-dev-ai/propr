// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Diagnostics;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Names the thing a call was made against, in the terms an operator configured it: which provider family,
///     which remote model, and the label of the connection profile it came from where the host has one.
/// </summary>
/// <remarks>
///     The profile label is host knowledge, not provider knowledge, which is why it is supplied here rather than
///     read off <see cref="ProviderEndpoint" />. Without it a failure message can only say "an OpenAI-compatible
///     endpoint failed", which does not tell an operator with several profiles which one to go and fix.
/// </remarks>
/// <param name="ProviderKind">Provider family the call was routed to.</param>
/// <param name="ModelId">Remote model id the call addressed.</param>
/// <param name="ProfileLabel">Operator-visible name of the connection profile, when the host has one.</param>
public sealed record ProviderCallTarget(
    string ProviderKind,
    string ModelId,
    string? ProfileLabel = null)
{
    /// <summary>Renders the target the way an operator would recognise it in a log line or failure message.</summary>
    /// <remarks>
    ///     Every part goes through <see cref="SecretSafeRendering.Identifier" />. A loaded family's key is
    ///     already held to the token rules, but this record takes three strings from whoever constructs it and
    ///     the result goes into a log line, where a line break in one ends the line and lets the rest be read as
    ///     a further entry.
    /// </remarks>
    public string Describe()
    {
        var family = SecretSafeRendering.Identifier(this.ProviderKind);
        var model = SecretSafeRendering.Identifier(this.ModelId);

        return this.ProfileLabel is { Length: > 0 } label
            ? $"profile '{SecretSafeRendering.Identifier(label)}' ({family}), model '{model}'"
            : $"provider {family}, model '{model}'";
    }
}
