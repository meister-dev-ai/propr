// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace MeisterProPR.Api.Extensions;

/// <summary>
///     One-time startup maintenance, run once a database is configured: apply pending migrations, seed the system
///     tenant and bootstrap admin, backfill secrets and logical-model mappings, and recover jobs a previous crash left
///     mid-flight. Kept out of <c>Program.cs</c> so the startup pipeline reads as a sequence of named steps.
/// </summary>
public static class StartupMaintenanceExtensions
{
    /// <summary>
    ///     Runs startup database maintenance when <paramref name="hasDatabaseConnectionString" /> is set; otherwise a
    ///     no-op (DB-less test hosts).
    /// </summary>
    public static async Task ApplyStartupMaintenanceAsync(this WebApplication app, bool hasDatabaseConnectionString)
    {
        if (!hasDatabaseConnectionString)
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

        // Apply any pending migrations automatically on startup.
        await db.Database.MigrateAsync();

        var systemTenantBootstrapService = scope.ServiceProvider.GetService<SystemTenantBootstrapService>();
        if (systemTenantBootstrapService is not null)
        {
            await systemTenantBootstrapService.SeedAsync();
        }

        var secretBackfillService = scope.ServiceProvider.GetRequiredService<SecretBackfillService>();
        await secretBackfillService.BackfillAsync();

        // Idempotent migration: move legacy configured-model review passes and unmapped AI purposes onto named logical
        // models. Best effort — a failure never blocks startup, and anything not yet migrated keeps resolving via its
        // legacy configured-model id / purpose binding until a later boot succeeds.
        var logicalModelBackfill = scope.ServiceProvider.GetService<ILogicalModelMigrationBackfill>();
        if (logicalModelBackfill is not null)
        {
            try
            {
                var migrated = await logicalModelBackfill.BackfillAllAsync(CancellationToken.None);
                if (migrated > 0)
                {
                    Log.Information(
                        "Logical-model migration: backfilled {Count} legacy review pass(es)/purpose mapping(s) onto logical models.",
                        migrated);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Logical-model migration backfill failed at startup; will retry on next boot.");
            }
        }

        // Startup recovery: transition stale Processing jobs (e.g., from a crash) back to Pending.
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var staleJobs = await jobRepo.GetProcessingJobsAsync();
        foreach (var job in staleJobs)
        {
            await jobRepo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending);
            Log.Warning(
                "Startup recovery: job {JobId} for PR#{PrId} was stale (Processing); reset to Pending",
                job.Id,
                job.PullRequestId);
        }

        // Seed the bootstrap admin user if none exists.
        var bootstrapService = scope.ServiceProvider.GetService<AdminBootstrapService>();
        if (bootstrapService is not null)
        {
            await bootstrapService.SeedAsync();
        }
    }
}
