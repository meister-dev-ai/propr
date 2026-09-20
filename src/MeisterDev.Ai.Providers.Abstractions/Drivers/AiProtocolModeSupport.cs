// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Words the refusal for a protocol mode a family does not declare, and narrows a model to the shapes it can be
///     called on.
/// </summary>
/// <remarks>
///     <para>
///         Which shapes a family speaks is the family's own declaration, in <c>ProtocolModes</c>. This class
///         decides nothing about that; it compares a requested shape against the declared list.
///     </para>
///     <para>
///         A binding can name a protocol mode the family does not serve — a row written before the family declared
///         its shapes, or one the family has withdrawn — which is safe only while asking for one produces a
///         refusal. Without it a driver falls through to whatever shape it does speak and puts a request on the
///         wire in the wrong format, answered by the provider with a rejection that names nothing useful.
///     </para>
/// </remarks>
public static class AiProtocolModeSupport
{
    /// <summary>
    ///     Returns a user-facing reason when <paramref name="requested" /> is not one of
    ///     <paramref name="supported" />, or <see langword="null" /> when it is.
    /// </summary>
    /// <param name="providerKind">The provider family being asked, named in the reason.</param>
    /// <param name="supported">The shapes that driver can serve.</param>
    /// <param name="requested">The shape being asked for.</param>
    public static string? GetRefusalReason(
        string providerKind,
        IReadOnlyList<string> supported,
        string requested)
    {
        ArgumentNullException.ThrowIfNull(supported);

        return ProviderVocabulary.Names(supported, requested)
            ? null
            : $"the '{providerKind}' provider does not speak the '{requested}' protocol "
              + $"(it speaks: {string.Join(", ", supported)})";
    }

    /// <summary>
    ///     Throws when a driver is asked for a shape it cannot speak. The last line of defence: configuration
    ///     refuses this long before a call is built, so reaching here means a profile was stored before the rule
    ///     existed or edited around it.
    /// </summary>
    /// <param name="providerKind">The provider family being asked.</param>
    /// <param name="supported">The shapes that driver can serve.</param>
    /// <param name="requested">The shape being asked for.</param>
    public static void Require(
        string providerKind,
        IReadOnlyList<string> supported,
        string requested)
    {
        if (GetRefusalReason(providerKind, supported, requested) is { } refusal)
        {
            throw new InvalidOperationException($"This model cannot be called: {refusal}.");
        }
    }

    /// <summary>
    ///     Narrows a model's declared shapes to those the driver can serve, so
    ///     <see cref="ProviderDeclaredProtocolModes.Auto" /> cannot resolve to one it cannot. A model advertising
    ///     the Responses API on an endpoint that has no Responses API is the case this exists for.
    /// </summary>
    /// <param name="model">The model descriptor to narrow.</param>
    /// <param name="supported">The shapes the driver can serve.</param>
    public static ProviderModelDescriptor NarrowToSupported(
        ProviderModelDescriptor model,
        IReadOnlyList<string> supported)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(supported);

        var narrowed = model.SupportedProtocolModes
            .Where(mode => ProviderVocabulary.Names(supported, mode))
            .ToList();

        return narrowed.Count == model.SupportedProtocolModes.Count
            ? model
            : model with { SupportedProtocolModes = narrowed };
    }
}
