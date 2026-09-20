// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using System.Text.Json.Serialization;
using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Hosting;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     Everything a driver needs to reach a provider: where it is, how to authenticate, and any transport
///     defaults. The same shape serves configuration-time probing and runtime calls, because both answer the
///     same question. A host projects its own stored connection onto this.
/// </summary>
/// <remarks>
///     The three members that can carry credential material are marked <c>[JsonIgnore]</c>, so a driver that
///     writes an endpoint into its own keyed store, or a host that returns one from an API, emits the address
///     and the mode and nothing else. The generated <c>ToString</c> is overridden for the same reason, which no
///     serialization attribute covers.
/// </remarks>
/// <param name="ProviderKind">Provider family that selects the driver.</param>
/// <param name="BaseUrl">Exact configured provider base URL.</param>
/// <param name="AuthMode">Authentication mode to use.</param>
/// <param name="Secret">Unprotected secret material for the chosen auth mode; never logged or serialized.</param>
/// <param name="DefaultHeaders">
///     Optional headers appended to every request. Never serialized: an operator-supplied header is one of the
///     places a provider takes its key.
/// </param>
/// <param name="DefaultQueryParams">
///     Optional query parameters appended to every request. Never serialized, for the same reason as the
///     headers.
/// </param>
public sealed record ProviderEndpoint(
    string ProviderKind,
    string BaseUrl,
    string AuthMode,
    [property: JsonIgnore] string? Secret = null,
    IReadOnlyDictionary<string, string>? DefaultHeaders = null,
    IReadOnlyDictionary<string, string>? DefaultQueryParams = null)
{
    // Held in fields so the copy is made wherever a map is set: a positional argument, an object initializer and
    // a `with` expression all reach the property, and a property with an accessor body cannot carry a field
    // initializer of its own. An endpoint is handed to a provider family and held for the life of the client it
    // builds — a review builds one client and calls it for as long as the review runs — so a caller keeping its
    // own dictionary could otherwise rewrite the headers, the query parameters or the declared values of a
    // request already in flight. Sending somewhere else means building another endpoint.
    private readonly IReadOnlyDictionary<string, string>? _defaultHeaders = Held(DefaultHeaders);
    private readonly IReadOnlyDictionary<string, string>? _defaultQueryParams = Held(DefaultQueryParams);
    private readonly IReadOnlyDictionary<string, string> _declaredValues = FrozenDictionary<string, string>.Empty;

    /// <summary>Optional headers applied to every request, copied so the endpoint keeps what it was given.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? DefaultHeaders
    {
        get => this._defaultHeaders;
        init => this._defaultHeaders = Held(value);
    }

    /// <summary>Optional query parameters appended to every request, copied for the same reason.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string>? DefaultQueryParams
    {
        get => this._defaultQueryParams;
        init => this._defaultQueryParams = Held(value);
    }

    /// <summary>
    ///     What the host allows this family to do against the connection this endpoint was projected from, or
    ///     null where the host built an endpoint from values that belong to no stored connection, which a
    ///     configuration-time probe does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is how a family reaches the host primitives. It rides on the endpoint because the primitives
    ///         are bound to one connection and the endpoint is the connection as a driver sees it: a driver is
    ///         constructed once for the family and serves every connection of it, so a handle taken at
    ///         construction would have to be told which connection each call is for, and a family naming its own
    ///         connection is the thing the whole primitive set is shaped to prevent.
    ///     </para>
    ///     <para>
    ///         A driver that needs a credential at call time captures this when the host asks it to build a
    ///         client and holds it for the client's life, which is longer than the work that built it: a review
    ///         builds one client and calls it for as long as the review runs.
    ///     </para>
    ///     <para>
    ///         Never serialized, for the same reason the credential is not: it is a live handle into the host and
    ///         means nothing outside the process that made it.
    ///     </para>
    /// </remarks>
    /// <remarks>
    ///     Part of this record's equality, like every other member, so two endpoints describing one connection
    ///     compare equal only when they carry the same handle. Nothing keys anything on an endpoint today; a
    ///     caller that starts to would find a cache keyed on one misses once per runtime, because a handle is
    ///     built per runtime.
    /// </remarks>
    [JsonIgnore]
    public IProviderConnectionContext? HostContext { get; init; }

    /// <summary>
    ///     The values of the fields this family declared, by declared field name, as configured on the connection
    ///     this endpoint was projected from.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One set rather than two: a family declares one set of fields and reads back one set of values. Where
    ///         each value is kept is the host's decision and the family never sees it — a secret-marked value is
    ///         held in the protected credential store and a plain one in the connection's settings document, so a
    ///         family supplies named plain values and receives named plain values and never handles an envelope or
    ///         an encryption type.
    ///     </para>
    ///     <para>
    ///         A field the operator left empty is absent unless the family declared a default, which stands in for
    ///         it. A read-only computed field is not here: it is derived rather than configured, and it is
    ///         recomputed wherever it is shown.
    ///     </para>
    ///     <para>
    ///         Never serialized, and rendered as its key names alone, because a family decides what goes in it and
    ///         the host has no basis for deciding which of those values is safe to print.
    ///     </para>
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyDictionary<string, string> DeclaredValues
    {
        get => this._declaredValues;
        init => this._declaredValues = Held(value) ?? FrozenDictionary<string, string>.Empty;
    }

    /// <summary>
    ///     Renders the endpoint without its credential. Overridden because the generated version prints every
    ///     property, which would put the secret into the first log line or exception message that mentions the
    ///     endpoint — and header and query values are elided for the same reason, since either can carry a key.
    /// </summary>
    public override string ToString()
    {
        return
            $"{nameof(ProviderEndpoint)} {{ ProviderKind = {SecretSafeRendering.Identifier(this.ProviderKind)}, BaseUrl = {SecretSafeRendering.Address(this.BaseUrl)}, "
            + $"AuthMode = {SecretSafeRendering.Identifier(this.AuthMode)}, Secret = {SecretSafeRendering.Elide(this.Secret)}, "
            + $"DefaultHeaders = [{SecretSafeRendering.KeyNames(this.DefaultHeaders)}], "
            + $"DefaultQueryParams = [{SecretSafeRendering.KeyNames(this.DefaultQueryParams)}], "
            + $"DeclaredValues = [{SecretSafeRendering.KeyNames(this.DeclaredValues)}] }}";
    }

    private static IReadOnlyDictionary<string, string>? Held(IReadOnlyDictionary<string, string>? value)
    {
        return value is null ? null : value.ToFrozenDictionary(StringComparer.Ordinal);
    }
}
