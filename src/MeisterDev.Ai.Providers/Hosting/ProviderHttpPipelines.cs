// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     The named pipelines provider traffic leaves through, and what each one is configured with beyond its
///     handlers.
/// </summary>
/// <remarks>
///     Named in one place because three parties have to agree on them: the composition that registers them, the
///     drivers compiled into the host that ask for them by name, and the factory that hands a composed one to a
///     provider family. A name spelled differently in any of the three produces a client with no egress guard on
///     it, which nothing reports.
/// </remarks>
public static class ProviderHttpPipelines
{
    /// <summary>Checking whether a configured endpoint is reachable and the credential works.</summary>
    public const string Probe = "AiProbe";

    /// <summary>Configuration-time work: discovering models, exchanging or renewing a credential.</summary>
    public const string Admin = "AiProviderAdmin";

    /// <summary>Model calls a review makes.</summary>
    public const string Runtime = "AiProviderRuntime";

    /// <summary>
    ///     The client timeout the runtime pipeline runs with. Infinite, matching the shared transport a provider
    ///     SDK defaults to, so a long completion is not cut off part-written; each call is still bounded by the
    ///     cancellation token it was made with.
    /// </summary>
    public static TimeSpan RuntimeTimeout => Timeout.InfiniteTimeSpan;

    /// <summary>The pipeline that serves one purpose.</summary>
    /// <param name="purpose">What the calls are for.</param>
    /// <exception cref="ArgumentOutOfRangeException">The purpose is not one of the three.</exception>
    public static string NameFor(ProviderHttpPurpose purpose)
    {
        return purpose switch
        {
            ProviderHttpPurpose.Probe => Probe,
            ProviderHttpPurpose.Admin => Admin,
            ProviderHttpPurpose.Runtime => Runtime,
            _ => throw new ArgumentOutOfRangeException(
                nameof(purpose),
                purpose,
                "Provider traffic leaves through one of three pipelines, and this is none of them."),
        };
    }

    /// <summary>The client timeout that serves one purpose.</summary>
    /// <param name="purpose">What the calls are for.</param>
    public static TimeSpan? TimeoutFor(ProviderHttpPurpose purpose)
    {
        return purpose == ProviderHttpPurpose.Runtime ? RuntimeTimeout : null;
    }
}
