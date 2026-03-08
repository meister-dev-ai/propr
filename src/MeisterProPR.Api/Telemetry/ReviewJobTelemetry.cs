using System.Diagnostics;

namespace MeisterProPR.Api.Telemetry;

/// <summary>ActivitySource used for tracing review job operations.</summary>
public static class ReviewJobTelemetry
{
    /// <summary>Main activity source for review job spans.</summary>
    public static readonly ActivitySource Source = new("MeisterProPR.ReviewJobs", "1.0.0");
}