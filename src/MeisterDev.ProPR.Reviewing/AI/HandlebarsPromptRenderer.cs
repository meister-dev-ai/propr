// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using HandlebarsDotNet;

namespace MeisterDev.ProPR.Infrastructure.AI;

internal sealed class HandlebarsPromptRenderer
{
    /// <summary>
    ///     The environment last built, with the partial set it was built from. A render reuses it when the partials
    ///     it was given match, and rebuilds otherwise.
    ///     <para>
    ///         An environment per render is not viable. <c>Handlebars.CreateSharedEnvironment</c> subscribes an
    ///         observer to each of the shared <see cref="HandlebarsConfiguration" />'s nine observable collections
    ///         and never unsubscribes, and every later <c>RegisterTemplate</c> notifies all of them, so the cost of
    ///         one render grew with the number of renders the process had already performed. On a long-lived replica
    ///         that reached a multiple of the original cost and was cleared only by a restart.
    ///     </para>
    ///     <para>
    ///         Holding one environment rather than a set of them keeps that from returning by another route: at most
    ///         one is retained, whatever a caller passes, so no cap or eviction is needed. Callers render against the
    ///         shipped <c>shared/partials</c> tree, which is fixed for a given build, so the rebuild path is taken
    ///         once.
    ///     </para>
    ///     <para>
    ///         One environment is held because one partial set is expected. A caller alternating between two sets
    ///         would rebuild on every render and get no reuse at all, so a second stable set is a reason to key this
    ///         by partial set again. What such a caller would lose is the reuse: the cost of a render returns to
    ///         building an environment, which is what it cost before this cache existed, and it does not resume
    ///         growing, because each environment carries its own configuration and a discarded one leaves nothing
    ///         behind.
    ///     </para>
    ///     <para>
    ///         Each environment is built with <c>Handlebars.Create</c> and its own configuration. A shared
    ///         configuration holds one template registry for every environment built from it, which makes
    ///         registration last-writer-wins across callers and across threads, so a render could compile against
    ///         another render's partials. An environment with its own configuration cannot, which is what lets one be
    ///         reused, and what keeps a rebuild from accumulating anything.
    ///     </para>
    /// </summary>
    private CachedEnvironment? _cached;

    internal string Render(string template, object? model, IReadOnlyDictionary<string, string>? partials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        try
        {
            var compiled = this.ResolveEnvironment(partials).Compile(template);
            return compiled(model);
        }
        catch (Exception ex) when (ex is HandlebarsCompilerException or HandlebarsRuntimeException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Failed to render Handlebars prompt template: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     The held environment when it was built from <paramref name="partials" />, and <see langword="null" />
    ///     otherwise. Exposed so a test can pin that a render resolves through the cache and that repeated renders of
    ///     one partial set keep the environment the first one built.
    /// </summary>
    internal IHandlebars? GetCachedEnvironment(IReadOnlyDictionary<string, string>? partials)
    {
        var cached = Volatile.Read(ref this._cached);
        return cached is not null && SamePartials(cached.Partials, partials) ? cached.Environment : null;
    }

    private static IHandlebars CreateEnvironment(IReadOnlyDictionary<string, string>? partials)
    {
        var handlebars = Handlebars.Create(new HandlebarsConfiguration { NoEscape = true });

        if (partials is not null)
        {
            foreach (var partial in partials)
            {
                handlebars.RegisterTemplate(partial.Key, partial.Value);
            }
        }

        return handlebars;
    }

    /// <summary>
    ///     Whether two partial sets hold the same names bound to the same text. Compared entry by entry, so two sets
    ///     are distinguished by their content and never by a digest of it: a digest over names and values joined
    ///     together reports two different sets as one whenever a value contains the join character, and the second
    ///     set would then render with the first set's partials.
    /// </summary>
    private static bool SamePartials(IReadOnlyDictionary<string, string> held, IReadOnlyDictionary<string, string>? candidate)
    {
        var count = candidate?.Count ?? 0;
        if (held.Count != count)
        {
            return false;
        }

        if (candidate is null)
        {
            return true;
        }

        foreach (var partial in candidate)
        {
            if (!held.TryGetValue(partial.Key, out var value) || !string.Equals(value, partial.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     A copy of the partial set, so a caller reusing or mutating its dictionary afterwards cannot change what the
    ///     held environment is compared against.
    /// </summary>
    private static Dictionary<string, string> Snapshot(IReadOnlyDictionary<string, string>? partials)
    {
        var snapshot = new Dictionary<string, string>(partials?.Count ?? 0, StringComparer.Ordinal);
        if (partials is null)
        {
            return snapshot;
        }

        foreach (var partial in partials)
        {
            snapshot[partial.Key] = partial.Value;
        }

        return snapshot;
    }

    private IHandlebars ResolveEnvironment(IReadOnlyDictionary<string, string>? partials)
    {
        var cached = Volatile.Read(ref this._cached);
        if (cached is not null && SamePartials(cached.Partials, partials))
        {
            return cached.Environment;
        }

        // The environment is returned to this caller whether or not the publish below wins, so concurrent renders of
        // different partial sets each use one built from their own partials. Two of them can build at once and the
        // later publish stands; that costs a construction and holds no more than the one environment.
        var environment = CreateEnvironment(partials);
        Volatile.Write(ref this._cached, new CachedEnvironment(Snapshot(partials), environment));
        return environment;
    }

    private sealed record CachedEnvironment(IReadOnlyDictionary<string, string> Partials, IHandlebars Environment);
}
