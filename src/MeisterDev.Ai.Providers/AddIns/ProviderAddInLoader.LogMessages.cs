// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Logging;

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>
///     Log messages for the add-in discovery pass.
///     <para>
///         A skip produces exactly one event, carrying the category, the path and the reason, so the condition
///         is readable in the log before anyone opens the inventory. Skips are warnings because a family an
///         operator expected is missing; the directory being absent is not, because a stock deployment has none.
///     </para>
/// </summary>
public static partial class ProviderAddInLoader
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "No {Origin} provider add-in directory at '{Directory}'; none were loaded from it.")]
    private static partial void LogAddInDirectoryAbsent(ILogger logger, string directory, string origin);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The {Origin} provider add-in directory '{Directory}' could not be read; none were loaded from it.")]
    private static partial void LogAddInDirectoryUnreadable(
        ILogger logger,
        string directory,
        string origin,
        Exception exception);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Loaded the {Origin} provider add-in '{Label}' ({Key} {Version}) from '{Path}'.")]
    private static partial void LogAddInLoaded(
        ILogger logger,
        string key,
        string label,
        string version,
        string path,
        string origin);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipped the {Origin} provider add-in '{Path}' as {Category}: {Reason}")]
    private static partial void LogAddInSkipped(
        ILogger logger,
        string category,
        string path,
        string reason,
        string origin);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Found the provider add-in '{Name}' at '{Path}' and did not load it: no administrator has activated these bytes")]
    private static partial void LogAddInAwaitingActivation(ILogger logger, string path, string name);
}
