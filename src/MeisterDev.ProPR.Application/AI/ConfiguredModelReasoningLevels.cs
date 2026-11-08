// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.AI;

/// <summary>
///     Fills the reasoning levels of a configured model from the sources that know them: the provider, the model catalog
///     and the add-in of the provider family.
/// </summary>
/// <remarks>
///     <para>
///         The provider's own report applies first, because it describes the model as the provider serves it. Without
///         one, the catalog's levels apply, and the add-in can complete or correct them for its own models, for example
///         with the level <c>none</c> for a model whose thinking a request can leave off, or with the levels of a model
///         that is newer than the catalog snapshot. A model that no source knows gets no levels, and ProPR sends it no
///         level until an operator enters its levels.
///     </para>
///     <para>
///         A list that an operator entered is never replaced by a detected one. A detected list is replaced when a
///         source reports a different list, so a newer catalog snapshot or add-in reaches the models it describes.
///     </para>
/// </remarks>
public static class ConfiguredModelReasoningLevels
{
    /// <summary>The levels the catalog lists for a model of the family, or null when no catalog entry states them.</summary>
    /// <remarks>
    ///     A family with a catalog provider of its own reads that provider's entry. A family that serves the whole
    ///     catalog reads every entry for the model identifier and keeps the levels these entries share, because a
    ///     gateway can route the model to any of these providers.
    /// </remarks>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="remoteModelId">The model identifier as the provider knows it.</param>
    /// <param name="catalog">The effective catalog of the client or tenant.</param>
    public static IReadOnlyList<string>? FromCatalog(
        string? providerKind,
        string remoteModelId,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var candidates = ProviderCatalogScope.PreferOwnProvider(
            providerKind,
            ProviderCatalogScope.EntriesFor(providerKind, catalog)
                .Where(entry => entry.ReasoningLevels is { Count: > 0 }
                                && string.Equals(entry.RemoteModelId, remoteModelId, StringComparison.OrdinalIgnoreCase)));
        if (candidates.Count == 0)
        {
            return null;
        }

        IEnumerable<string> shared = candidates[0].ReasoningLevels!;
        foreach (var candidate in candidates.Skip(1))
        {
            shared = shared.Intersect(candidate.ReasoningLevels!, StringComparer.Ordinal);
        }

        return ProviderReasoningLevels.Normalize(shared) is { Count: > 0 } levels ? levels : null;
    }

    /// <summary>
    ///     The reason ProPR refuses the levels a request states for a model, or null when it accepts them. Each name is a
    ///     level name, and a model lists at most <see cref="ProviderReasoningLevels.MaxCount" /> levels.
    /// </summary>
    /// <param name="remoteModelId">The model identifier, for the message.</param>
    /// <param name="stated">The levels as the request states them, or null.</param>
    public static string? Refusal(string remoteModelId, IReadOnlyList<string>? stated)
    {
        if (stated is null)
        {
            return null;
        }

        if (stated.Count > ProviderReasoningLevels.MaxCount)
        {
            return $"Configured model '{remoteModelId}' lists {stated.Count} reasoning levels, and a model lists at most "
                   + $"{ProviderReasoningLevels.MaxCount}.";
        }

        foreach (var level in stated)
        {
            if (!ProviderReasoningLevels.IsValidName(level?.Trim().ToLowerInvariant()))
            {
                return $"Configured model '{remoteModelId}' lists '{level}' as a reasoning level. A level name has 1 to "
                       + $"{ProviderReasoningLevels.MaxNameLength} characters, lowercase letters, digits, hyphens and "
                       + "underscores, and starts with a letter.";
            }
        }

        return null;
    }

    /// <summary>The levels detected for a model and their source, or no levels when no source knows the model.</summary>
    /// <param name="driver">The driver of the provider family, or null when this build has none.</param>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="remoteModelId">The model identifier as the provider knows it.</param>
    /// <param name="catalog">The effective catalog of the client or tenant.</param>
    /// <param name="providerLevels">The levels the provider reported during discovery, or null.</param>
    public static (IReadOnlyList<string>? Levels, AiReasoningLevelSource? Source) Detect(
        IAiProviderDriver? driver,
        string? providerKind,
        string remoteModelId,
        IReadOnlyList<AiModelCatalogEntryDto> catalog,
        IReadOnlyList<string>? providerLevels = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (ProviderReasoningLevels.Normalize(providerLevels) is { Count: > 0 } reported)
        {
            return (reported, AiReasoningLevelSource.Provider);
        }

        var fromCatalog = FromCatalog(providerKind, remoteModelId, catalog);
        var described = ProviderReasoningLevels.Normalize(Describe(driver, remoteModelId, fromCatalog));
        if (described is not { Count: > 0 })
        {
            return (null, null);
        }

        return fromCatalog is not null && described.SequenceEqual(fromCatalog, StringComparer.Ordinal)
            ? (described, AiReasoningLevelSource.Catalog)
            : (described, AiReasoningLevelSource.AddIn);
    }

    /// <summary>
    ///     Completes the reasoning levels of the configured models of a save. A chat model that states its levels keeps
    ///     them. A model that states none keeps the levels stored for it, unless the request asks to detect them again.
    ///     Any other chat model gets the detected levels.
    /// </summary>
    /// <param name="models">The configured models of the save.</param>
    /// <param name="stored">The configured models stored before the save, or null for a new connection.</param>
    /// <param name="detectAgain">The model identifiers whose levels the request asks to detect again.</param>
    /// <param name="driver">The driver of the provider family, or null when this build has none.</param>
    /// <param name="providerKind">The declared key of the provider family.</param>
    /// <param name="catalog">The effective catalog of the client or tenant.</param>
    public static IReadOnlyList<AiConfiguredModelDto> Complete(
        IReadOnlyList<AiConfiguredModelDto> models,
        IReadOnlyList<AiConfiguredModelDto>? stored,
        IReadOnlySet<string> detectAgain,
        IAiProviderDriver? driver,
        string? providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(detectAgain);
        ArgumentNullException.ThrowIfNull(catalog);
        return models
            .Select(model => Complete(model, stored, detectAgain, driver, providerKind, catalog))
            .ToList()
            .AsReadOnly();
    }

    private static AiConfiguredModelDto Complete(
        AiConfiguredModelDto model,
        IReadOnlyList<AiConfiguredModelDto>? stored,
        IReadOnlySet<string> detectAgain,
        IAiProviderDriver? driver,
        string? providerKind,
        IReadOnlyList<AiModelCatalogEntryDto> catalog)
    {
        // An embedding model reasons about nothing, so it keeps what the request states.
        if (!model.OperationKinds.Contains(AiOperationKind.Chat))
        {
            return model;
        }

        var again = detectAgain.Contains(model.RemoteModelId);
        if (!again && model.ReasoningLevels is not null)
        {
            return model;
        }

        if (!again && Stored(model, stored) is { ReasoningLevels: not null } previous)
        {
            return model with { ReasoningLevels = previous.ReasoningLevels, ReasoningLevelsSource = previous.ReasoningLevelsSource };
        }

        var (levels, source) = WithDefaultLevel(Detect(driver, providerKind, model.RemoteModelId, catalog), model.DefaultReasoningLevel);
        return model with { ReasoningLevels = levels, ReasoningLevelsSource = source };
    }

    /// <summary>
    ///     Detected levels completed with the default level of the model when they lack it. An operator who set the
    ///     default level stated that the model accepts it, which a catalog or add-in that omits it does not overrule.
    /// </summary>
    /// <remarks>
    ///     Levels that no source knows stay unknown. A list made of the default level alone would leave the model only
    ///     that level, and every other configured level would reach the model as the nearest lower level of that list or
    ///     as no level. A model whose levels no source knows receives every level as configured.
    /// </remarks>
    /// <param name="detected">The detected levels and their source.</param>
    /// <param name="defaultLevel">The default level of the model, or null.</param>
    public static (IReadOnlyList<string>? Levels, AiReasoningLevelSource? Source) WithDefaultLevel(
        (IReadOnlyList<string>? Levels, AiReasoningLevelSource? Source) detected,
        string? defaultLevel)
    {
        if (defaultLevel is null || detected.Levels is null || detected.Levels.Contains(defaultLevel, StringComparer.Ordinal))
        {
            return detected;
        }

        return (ProviderReasoningLevels.Normalize([.. detected.Levels, defaultLevel]), detected.Source);
    }

    private static AiConfiguredModelDto? Stored(AiConfiguredModelDto model, IReadOnlyList<AiConfiguredModelDto>? stored)
    {
        if (stored is null)
        {
            return null;
        }

        return (model.Id != Guid.Empty ? stored.FirstOrDefault(candidate => candidate.Id == model.Id) : null)
               ?? stored.FirstOrDefault(candidate =>
                   string.Equals(candidate.RemoteModelId, model.RemoteModelId, StringComparison.OrdinalIgnoreCase));
    }

    // The description runs code of the provider family's add-in. When it throws, the catalog's levels apply.
    private static IReadOnlyList<string>? Describe(
        IAiProviderDriver? driver,
        string remoteModelId,
        IReadOnlyList<string>? catalogLevels)
    {
        if (driver is null)
        {
            return catalogLevels;
        }

        try
        {
            return driver.DescribeReasoningLevels(remoteModelId, catalogLevels);
        }
        catch (Exception)
        {
            return catalogLevels;
        }
    }
}
