// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;

namespace MeisterProPR.Api.Telemetry;

/// <summary>ActivitySource used for tracing review job operations.</summary>
public static class ReviewJobTelemetry
{
    /// <summary>Main activity source for review job spans.</summary>
    public static readonly ActivitySource Source = new("MeisterProPR.ReviewJobs", "1.0.0");
}
