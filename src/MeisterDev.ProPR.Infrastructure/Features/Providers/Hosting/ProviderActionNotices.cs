// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     What the host tells an operator about a declared action: what has to be true before it is started, and
///     what a run that is still open is waiting for.
/// </summary>
/// <remarks>
///     <para>
///         Composed by the host from the family's declaration and the connection's configured values, so a
///         console renders one string and carries no knowledge of any family. The same text serves both
///         directions in time: shown before the action starts so an operator whose deployment cannot satisfy it
///         knows that first, and recorded on the invocation so a run that expires says what it was waiting for
///         rather than only that it ran out.
///     </para>
///     <para>
///         The port is read from the family's declared integer configuration fields, which is where a family that
///         binds a listener states it. The host has no other way to know which value is a port, and it needs no
///         other: a family that binds one declares it as configuration so the operator can change it.
///     </para>
/// </remarks>
public static class ProviderActionNotices
{
    /// <summary>What a run of <paramref name="action" /> is waiting for while it is still open.</summary>
    /// <param name="declaration">The family the action belongs to.</param>
    /// <param name="action">The action that was started.</param>
    /// <param name="providerSettings">The connection's declared configuration values, by declared field name.</param>
    public static string WaitingFor(
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        IReadOnlyDictionary<string, string>? providerSettings)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(action);

        var waiting = $"Waiting for the '{action.Label}' action of the '{declaration.Label}' provider family "
                      + "to report that it finished.";

        if (!RequiresCoLocation(declaration, action))
        {
            return Capped(waiting);
        }

        var ports = DescribePorts(declaration, providerSettings);
        var arrival = declaration.OpensListener && ports is not null
            ? $" It finishes when the provider sends your browser back to {ports} on the machine running this "
              + "host, so that machine and the browser have to be the same one."
            : " It finishes in your browser on the machine running this host, so that machine and the browser "
              + "have to be the same one.";

        return Capped(waiting + arrival);
    }

    /// <summary>
    ///     What an operator has to know before starting <paramref name="action" />, or <see langword="null" />
    ///     when the family states no requirement its deployment has to meet.
    /// </summary>
    /// <param name="declaration">The family the action belongs to.</param>
    /// <param name="action">The action about to be started.</param>
    /// <param name="providerSettings">The connection's declared configuration values, by declared field name.</param>
    public static string? CoLocationNotice(
        ProviderDeclaration declaration,
        ProviderDeclaredAction action,
        IReadOnlyDictionary<string, string>? providerSettings,
        IEnumerable<string>? secrets = null)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(action);

        if (!RequiresCoLocation(declaration, action))
        {
            return null;
        }

        var ports = DescribePorts(declaration, providerSettings);
        var requirement = ports is null
            ? $"The '{declaration.Label}' provider family completes this action in a browser on the machine "
              + "running this host, so start it from that machine."
            : $"The '{declaration.Label}' provider family completes this action by sending your browser back to "
              + $"{ports} on the machine running this host, so start it from that machine.";

        // The paste path is the fallback every flow keeps, and it is the answer for an operator administering a
        // host that runs somewhere else. Stated only where the action declares an input a pasted address fits:
        // an action whose inputs are a checkbox and a number cannot take one, and telling an operator to paste
        // into it sends them to a form that has nowhere to put it.
        var alternative = AnyTakesAPastedAddress(action)
            ? " If your browser is somewhere else, complete it by pasting the address the provider redirects to."
            : string.Empty;

        return Capped(requirement + alternative, secrets);
    }

    // Whether this action finishes in a browser on the machine running the host. The action's own statement
    // where it makes one, and the family's otherwise: a family with a credential flow has actions of both kinds,
    // and a disconnect that clears a stored credential reaches no browser.
    private static bool RequiresCoLocation(ProviderDeclaration declaration, ProviderDeclaredAction action)
    {
        return action.RequiresBrowserCoLocation ?? declaration.RequiresBrowserCoLocation;
    }

    // The inputs a redirect address can be pasted into. The family states them where it knows: a redirect is
    // collected as free text, because the family parses the code out of it, so the shape a host could infer it
    // from is the shape of every other free-text input. A family that states none leaves the host reading any
    // free-text or address input as one, which it did before an input could say so.
    private static bool AnyTakesAPastedAddress(ProviderDeclaredAction action)
    {
        var stated = action.Inputs.Where(input => input.AcceptsRedirectAddress).ToList();

        return stated.Count > 0
            ? stated.Exists(input => !input.IsComputed)
            : action.Inputs.Any(TakesAPastedAddress);
    }

    private static bool TakesAPastedAddress(ProviderDeclaredField input)
    {
        return input.Kind is ProviderFieldKind.Url or ProviderFieldKind.String && !input.IsComputed;
    }

    /// <summary>
    ///     The ports this family's declared configuration names, with their configured values, or
    ///     <see langword="null" /> when the family opens no listener or declares none.
    /// </summary>
    /// <param name="declaration">The family whose configuration is read.</param>
    /// <param name="providerSettings">The connection's declared configuration values.</param>
    private static string? DescribePorts(
        ProviderDeclaration declaration,
        IReadOnlyDictionary<string, string>? providerSettings)
    {
        // Read only from a family that declared it opens a socket. Without that, a whole-number setting of any
        // other kind — a timeout in seconds, a page size — is announced to the operator as the port their browser
        // will be sent back to.
        if (!declaration.OpensListener)
        {
            return null;
        }

        // The family names its port fields where it knows them; a family that names none leaves every
        // whole-number field read as one, which is the same reading and the same risk inside the family. A name
        // matching no whole-number field is passed over, so the notice states one port fewer and never a wrong
        // one.
        var named = declaration.ListenerPortFieldNames;
        var ports = declaration.ConnectionFields
            .Where(field => field.Kind == ProviderFieldKind.Int && !field.IsComputed)
            .Where(field => named.Count == 0 || named.Contains(field.Name, StringComparer.Ordinal))
            .Select(field => new { field.Label, Value = Configured(providerSettings, field) })
            .Where(port => port.Value is not null)
            .Select(port => $"port {port.Value} ({port.Label})")
            .ToList();

        return ports.Count == 0 ? null : string.Join(" or ", ports);
    }

    private static string? Configured(
        IReadOnlyDictionary<string, string>? providerSettings,
        ProviderDeclaredField field)
    {
        var stored = providerSettings?.GetValueOrDefault(field.Name);

        return string.IsNullOrWhiteSpace(stored) ? field.DefaultValue : stored;
    }

    // Every part of this is composed from strings the family wrote, so it goes out through the same cap and the
    // column it is stored in is the same width.
    // A notice is composed from the add-in's own labels and the connection's non-secret settings, so nothing
    // that goes into one can hold the credential. It still leaves through the guard, and the caller hands over
    // the values it is already holding where it has them, so a notice that later quotes a field of another kind
    // is covered. The dispatcher has none in hand when it opens a run, and reading the credential there would
    // add a database call and a failure path to a string that cannot carry one.
    private static string Capped(string notice, IEnumerable<string>? secrets = null)
    {
        return ProviderMessageGuard.Sanitize(notice, secrets);
    }
}
