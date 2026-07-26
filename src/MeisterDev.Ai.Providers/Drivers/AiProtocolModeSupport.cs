// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Decides whether a driver can speak a requested protocol shape, and says so in one voice.
/// </summary>
/// <remarks>
///     The protocol-mode enum names shapes no current driver implements, which is safe only while asking for one
///     produces a refusal. Without this a driver silently falls through to whatever shape it does speak — a
///     request in the wrong wire format, answered by the provider with a rejection that names nothing useful.
/// </remarks>
public static class AiProtocolModeSupport
{
    /// <summary>The shapes an OpenAI-shaped endpoint that implements the Responses API can serve.</summary>
    public static IReadOnlyList<AiProtocolMode> OpenAiFamily { get; } =
        [AiProtocolMode.Auto, AiProtocolMode.Responses, AiProtocolMode.ChatCompletions, AiProtocolMode.Embeddings];

    /// <summary>
    ///     The shapes an arbitrary OpenAI-compatible server can be assumed to serve. The Responses API is
    ///     deliberately absent: it is an OpenAI-specific surface, and assuming it of a compatible endpoint turns
    ///     into a 404 on the first call.
    /// </summary>
    public static IReadOnlyList<AiProtocolMode> OpenAiCompatibleFamily { get; } =
        [AiProtocolMode.Auto, AiProtocolMode.ChatCompletions, AiProtocolMode.Embeddings];

    /// <summary>
    ///     Returns a user-facing reason when <paramref name="requested" /> is not one of
    ///     <paramref name="supported" />, or <see langword="null" /> when it is.
    /// </summary>
    /// <param name="providerKind">The provider family being asked, named in the reason.</param>
    /// <param name="supported">The shapes that driver can serve.</param>
    /// <param name="requested">The shape being asked for.</param>
    public static string? DescribeRefusal(
        AiProviderKind providerKind,
        IReadOnlyList<AiProtocolMode> supported,
        AiProtocolMode requested)
    {
        ArgumentNullException.ThrowIfNull(supported);

        return supported.Contains(requested)
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
        AiProviderKind providerKind,
        IReadOnlyList<AiProtocolMode> supported,
        AiProtocolMode requested)
    {
        if (DescribeRefusal(providerKind, supported, requested) is { } refusal)
        {
            throw new InvalidOperationException($"This model cannot be called: {refusal}.");
        }
    }

    /// <summary>
    ///     Narrows a model's declared shapes to those the driver can serve, so <see cref="AiProtocolMode.Auto" />
    ///     cannot resolve to one it cannot. A model advertising the Responses API on an endpoint that has no
    ///     Responses API is the case this exists for.
    /// </summary>
    /// <param name="model">The model descriptor to narrow.</param>
    /// <param name="supported">The shapes the driver can serve.</param>
    public static ProviderModelDescriptor NarrowToSupported(
        ProviderModelDescriptor model,
        IReadOnlyList<AiProtocolMode> supported)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(supported);

        var narrowed = model.SupportedProtocolModes.Where(supported.Contains).ToList();
        return narrowed.Count == model.SupportedProtocolModes.Count
            ? model
            : model with { SupportedProtocolModes = narrowed };
    }
}
