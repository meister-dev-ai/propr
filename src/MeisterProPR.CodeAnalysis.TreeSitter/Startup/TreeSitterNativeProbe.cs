// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;
using Microsoft.Extensions.Logging;
using TS = TreeSitter;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Startup;

/// <summary>
///     Startup fail-soft probe for the Tree-sitter native libraries.
/// </summary>
/// <remarks>
///     <para>
///         At construction the probe loads each kept language once via
///         <see cref="LanguageRegistry.TryGetLanguage" />. Failures are <b>not</b> fatal - the
///         worker stays up and the analyzer reports <see cref="IStructuralAnalyzerProbe.IsAvailable" />
///         = false so callers fall back to the heuristic. Every load attempt emits a structured
///         log entry and bumps an OpenTelemetry counter so the degradation is visible to
///         operators.
///     </para>
///     <para>
///         The probe is cheap to construct (one <c>NativeLibrary.Load</c> per language) and
///         idempotent: <see cref="LanguageRegistry" /> caches handles for the lifetime of the
///         process, so subsequent analyzer calls do not re-probe.
///     </para>
/// </remarks>
internal sealed class TreeSitterNativeProbe : IStructuralAnalyzerProbe
{
    internal const string MeterName = "MeisterProPR.CodeAnalysis.TreeSitter";
    internal const string ProbeOutcomeCounterName = "tree_sitter.native_probe.outcomes";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private readonly ConcurrentDictionary<SupportedLanguage, Exception?> _loadFailures = new();

    private readonly ILogger<TreeSitterNativeProbe> _logger;

    public TreeSitterNativeProbe(ILogger<TreeSitterNativeProbe> logger)
    {
        this._logger = logger;
        this.IsAvailable = this.Probe();
    }

    /// <summary>
    ///     Languages whose native library failed to load, with the captured exception for
    ///     diagnostics. Empty when <see cref="IsAvailable" /> is true.
    /// </summary>
    public IReadOnlyDictionary<SupportedLanguage, Exception?> LoadFailures => this._loadFailures;

    /// <summary>
    ///     True when every kept language's native library loaded successfully. When false, the
    ///     analyzer returns empty for all calls and the prefetch falls back to the heuristic.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    ///     Probes each kept language. Returns true when all loaded.
    /// </summary>
    private bool Probe()
    {
        var allLoaded = true;
        var outcomeCounter = Meter.CreateCounter<int>(
            ProbeOutcomeCounterName, "{outcome}",
            "Tree-sitter native library probe outcomes per language (loaded|failed)");

        foreach (var language in LanguageRegistry.SupportedLanguages)
        {
            TS.Language? loaded;
            try
            {
                loaded = LanguageRegistry.TryGetLanguage(language);
            }
            catch (Exception ex)
            {
                loaded = null;
                this._loadFailures[language] = ex;
            }

            if (loaded is null)
            {
                allLoaded = false;
                outcomeCounter.Add(
                    1, new KeyValuePair<string, object?>("language", language.ToString()),
                    new KeyValuePair<string, object?>("outcome", "failed"));

                var failure = this._loadFailures.GetValueOrDefault(language);
                this._logger.LogError(
                    failure,
                    "Tree-sitter native probe failed for language {Language}. Analyzer will fall back to heuristic for this language.",
                    language);
            }
            else
            {
                outcomeCounter.Add(
                    1, new KeyValuePair<string, object?>("language", language.ToString()),
                    new KeyValuePair<string, object?>("outcome", "loaded"));
                this._logger.LogInformation(
                    "Tree-sitter native probe loaded language {Language} ({AbiVersion}).",
                    language, loaded.AbiVersion);
            }
        }

        if (allLoaded)
        {
            this._logger.LogInformation(
                "Tree-sitter native probe completed: all {Count} kept languages available. Structural boundary resolution is enabled.",
                LanguageRegistry.SupportedLanguages.Count);
        }
        else
        {
            this._logger.LogWarning(
                "Tree-sitter native probe completed with {FailedCount} of {TotalCount} languages unavailable. " +
                "Structural boundary resolution will fall back to the heuristic for unavailable languages.", this._loadFailures.Count,
                LanguageRegistry.SupportedLanguages.Count);
        }

        Debug.Assert(Meter.Name == MeterName, "meter name is stable for OTel instrumentation discovery");
        return allLoaded;
    }
}
