// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Immutable;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     What an action answers a dispatch with. Four results and no others.
/// </summary>
/// <remarks>
///     <para>
///         The host honours each without knowing why it was returned, and that keeps a credential flow's
///         semantics with the family that owns them. A fifth result meaning "still going" is not needed and not
///         offered: a family whose work outlives the dispatch writes its terminal state through
///         <see cref="IProviderInvocationReporter" /> instead.
///     </para>
///     <para>
///         Closed by construction — the base type cannot be derived from outside this assembly — so a family
///         cannot return a result the host has no handling for.
///     </para>
/// </remarks>
public abstract record ProviderActionResult
{
    private protected ProviderActionResult()
    {
    }

    /// <summary>The action finished, with something to tell the operator.</summary>
    /// <param name="message">What to show the operator.</param>
    public static ProviderActionResult Completed(string message)
    {
        // Refused here, where the family named it, and not left to the host. A completed action with nothing to
        // show reaches an operator as a blank panel, and the family that produced it is the part worth naming.
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return new ProviderActionCompleted(message);
    }

    /// <summary>The operator has to visit an address for the action to continue.</summary>
    /// <remarks>
    ///     The host checks the address before it reaches a browser: https, with a host one of the family's
    ///     declared patterns covers, or http to a loopback host where the family declared co-location. A URL that
    ///     fails either check becomes a failure naming the scheme or the undeclared host.
    /// </remarks>
    /// <param name="url">Where to send the operator.</param>
    /// <param name="awaitCompletion">
    ///     Whether the invocation stays open after the operator has been sent, because the family will report its
    ///     own terminal state later.
    /// </param>
    public static ProviderActionResult OpenUrl(string url, bool awaitCompletion)
    {
        // The host checks the address it is given; a blank one never reaches that check as an address, so it is
        // refused where the family stated it.
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        return new ProviderActionOpenUrl(url, awaitCompletion);
    }

    /// <summary>The action needs values from the operator before it can continue.</summary>
    /// <remarks>
    ///     The submitted values continue this invocation rather than opening a second one, and that keeps
    ///     the initiating administrator, the window and any stored handshake attached to one record.
    /// </remarks>
    /// <param name="fields">The values to ask for, declared the way configuration fields are.</param>
    public static ProviderActionResult ShowForm(IReadOnlyList<ProviderDeclaredField> fields)
    {
        return new ProviderActionShowForm(fields);
    }

    /// <summary>The action cannot be completed.</summary>
    /// <param name="message">What went wrong, in terms an operator can act on.</param>
    public static ProviderActionResult Failed(string message)
    {
        return new ProviderActionFailed(message);
    }
}

/// <summary>The action finished.</summary>
/// <param name="Message">What to show the operator.</param>
public sealed record ProviderActionCompleted(string Message) : ProviderActionResult;

/// <summary>The operator has to visit an address for the action to continue.</summary>
/// <param name="Url">Where to send the operator.</param>
/// <param name="AwaitCompletion">Whether the invocation stays open after the operator has been sent.</param>
public sealed record ProviderActionOpenUrl(string Url, bool AwaitCompletion) : ProviderActionResult;

/// <summary>The action needs values from the operator before it can continue.</summary>
public sealed record ProviderActionShowForm : ProviderActionResult
{
    /// <summary>Initializes a new instance of the <see cref="ProviderActionShowForm" /> class.</summary>
    /// <param name="fields">The values to ask for.</param>
    public ProviderActionShowForm(IReadOnlyList<ProviderDeclaredField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Copied into an immutable array, because the host renders this after the family has returned. A
        // collection expression targeting IReadOnlyList produces an array, which a family can cast back to and
        // write through, so copying alone would not stop it changing a form the operator is already looking at.
        this.Fields = fields.ToImmutableArray();
    }

    /// <summary>The values to ask for.</summary>
    public IReadOnlyList<ProviderDeclaredField> Fields { get; }
}

/// <summary>The action cannot be completed.</summary>
/// <param name="Message">What went wrong.</param>
public sealed record ProviderActionFailed(string Message) : ProviderActionResult;
