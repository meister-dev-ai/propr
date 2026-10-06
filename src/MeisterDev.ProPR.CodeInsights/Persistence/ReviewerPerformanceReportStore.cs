// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.CodeInsights.Http;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeisterDev.ProPR.CodeInsights.Persistence;

/// <summary>Stores complete server-captured responses and requires authorization for every copied client.</summary>
public sealed class ReviewerPerformanceReportStore(
    MeisterProPRDbContext dbContext,
    ReviewerPerformanceRangeReader reader,
    IOptionsMonitor<CodeInsightsOptions>? options = null,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
{
    private const int MaximumNameCharacters = 160;
    private const int MaximumPayloadBytes = 32 * 1024 * 1024;
    private const int MaximumReportBatch = 100;
    private const int MaximumWriteAttempts = 3;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReviewerPerformanceSavedReport> SaveAsync(
        ReviewerPerformanceSaveReportRequest request, IReadOnlyCollection<Guid> clients, CancellationToken ct = default)
    {
        for (var attempt = 0;; attempt++)
        {
            try
            {
                return await this.SaveAttemptAsync(request, clients, ct);
            }
            catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                      exception.InnerException is PostgresException { SqlState: "23505" or "40001" })
            {
                dbContext.ChangeTracker.Clear();
            }
            catch (PostgresException exception) when (attempt < MaximumWriteAttempts - 1 && exception.SqlState == "40001")
            {
                dbContext.ChangeTracker.Clear();
            }
        }
    }

    private async Task<ReviewerPerformanceSavedReport> SaveAttemptAsync(
        ReviewerPerformanceSaveReportRequest request, IReadOnlyCollection<Guid> clients, CancellationToken ct)
    {
        ValidateRequest(request, clients);
        var fingerprint = Fingerprint(request);
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(request.Id.ToByteArray(), 0)})", ct);
        }

        var existing = await db.ReviewerPerformanceReports.AsNoTracking().Include(row => row.Clients).FirstOrDefaultAsync(row => row.Id == request.Id, ct);
        if (existing is not null)
        {
            return OpenExisting(existing, fingerprint, clients);
        }

        var response = await reader.QueryInContextAsync(db, request.Query, clients, ct);
        var payload = JsonSerializer.Serialize(response, Json);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumPayloadBytes)
        {
            throw new ArgumentException("The saved response exceeds 32 MiB. Narrow the window or series.");
        }

        var record = Capture(request, response, payload, fingerprint, options?.CurrentValue.ReportRetentionDays ?? 365);
        db.ReviewerPerformanceReports.Add(record);
        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }

        return Open(record);
    }

    public async Task<IReadOnlyList<ReviewerPerformanceReportSummary>> ListAsync(IReadOnlyCollection<Guid> clients, CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        return await Authorized(db, clients)
            .OrderByDescending(row => row.CapturedAt).Take(MaximumReportBatch)
            .Select(row => new ReviewerPerformanceReportSummary(row.Id, row.Name, row.CalculationVersion, row.CapturedAt, row.ExpiresAt)).ToListAsync(ct);
    }

    public async Task<ReviewerPerformanceSavedReport?> GetAsync(Guid id, IReadOnlyCollection<Guid> clients, CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var row = await Authorized(db, clients).FirstOrDefaultAsync(row => row.Id == id, ct);
        return row is null ? null : Open(row);
    }

    public async Task<bool> DeleteAsync(Guid id, IReadOnlyCollection<Guid> clients, CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var ids = await Authorized(db, clients).Where(row => row.Id == id).Select(row => row.Id).ToListAsync(ct);
        if (ids.Count == 0)
        {
            return false;
        }

        await RemoveIdsAsync(db, ids, ct);
        return true;
    }

    public async Task<int> PurgeForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var ids = await db.ReviewerPerformanceReportClients.Where(member => member.ClientId == clientId).Select(member => member.ReportId).Distinct()
            .ToListAsync(ct);
        return await RemoveIdsAsync(db, ids, ct);
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
        var db = lease.Context;
        var now = DateTimeOffset.UtcNow;
        var ids = await db.ReviewerPerformanceReports.Where(row => row.ExpiresAt <= now).OrderBy(row => row.ExpiresAt).Select(row => row.Id)
            .Take(MaximumReportBatch)
            .ToListAsync(ct);
        return await RemoveIdsAsync(db, ids, ct);
    }

    internal static async Task<int> RemoveIdsAsync(MeisterProPRDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        if (db.Database.IsRelational())
        {
            // Memberships cascade from complete report deletion in the same database statement.
            return await db.ReviewerPerformanceReports.Where(row => ids.Contains(row.Id)).ExecuteDeleteAsync(ct);
        }

        db.ReviewerPerformanceReportClients.RemoveRange(
            await db.ReviewerPerformanceReportClients.Where(member => ids.Contains(member.ReportId)).ToListAsync(ct));
        foreach (var id in ids)
        {
            db.ReviewerPerformanceReports.Remove(
                db.ReviewerPerformanceReports.Local.FirstOrDefault(tracked => tracked.Id == id) ?? new ReviewerPerformanceReport { Id = id });
        }

        await db.SaveChangesAsync(ct);
        return ids.Count;
    }

    private static void ValidateRequest(ReviewerPerformanceSaveReportRequest request, IReadOnlyCollection<Guid> clients)
    {
        if (request.Id == Guid.Empty || string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > MaximumNameCharacters)
        {
            throw new ArgumentException("Provide a report identifier and a name of at most 160 characters.");
        }

        ReviewerPerformanceRangeReader.Validate(request.Query, clients);
        if (!request.Query.Views.SelectMany(view => view.ClientIds ?? clients.ToArray()).Any())
        {
            throw new ArgumentException("A saved report must include at least one authorized client.");
        }
    }

    private static string Fingerprint(ReviewerPerformanceSaveReportRequest request)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json))));
    }

    private static ReviewerPerformanceSavedReport OpenExisting(ReviewerPerformanceReport existing, string fingerprint, IReadOnlyCollection<Guid> clients)
    {
        if (existing.Clients.Count == 0 || existing.Clients.Any(member => !clients.Contains(member.ClientId)))
        {
            throw new UnauthorizedAccessException("The report has no authorized client membership or contains a client outside the authorized scope.");
        }

        if (existing.RequestFingerprint != fingerprint)
        {
            throw new InvalidOperationException("The identifier belongs to a different report request.");
        }

        if (existing.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The report has expired.");
        }

        return Open(existing);
    }

    private static ReviewerPerformanceReport Capture(
        ReviewerPerformanceSaveReportRequest request, ReviewerPerformanceRangeResponse response,
        string payload, string fingerprint, int retentionDays)
    {
        var memberships = response.Views.SelectMany(view => view.Scope.ClientIds ?? []).Distinct().ToArray();
        return new ReviewerPerformanceReport
        {
            Id = request.Id,
            Name = request.Name.Trim(),
            CalculationVersion = response.CalculationVersion,
            CapturedAt = response.CapturedAt,
            ExpiresAt = response.CapturedAt.AddDays(Math.Clamp(retentionDays, 1, 3650)),
            RequestFingerprint = fingerprint,
            Payload = payload,
            Clients = memberships.Select(client => new ReviewerPerformanceReportClient
            {
                ReportId = request.Id,
                ClientId = client
            }).ToList(),
        };
    }

    private static IQueryable<ReviewerPerformanceReport> Authorized(MeisterProPRDbContext db, IReadOnlyCollection<Guid> clients)
    {
        var now = DateTimeOffset.UtcNow;
        return db.ReviewerPerformanceReports.AsNoTracking().Where(row =>
            row.ExpiresAt > now && db.ReviewerPerformanceReportClients.Any(member => member.ReportId == row.Id) &&
            !db.ReviewerPerformanceReportClients.Any(member => member.ReportId == row.Id && !clients.Contains(member.ClientId)));
    }

    private static ReviewerPerformanceSavedReport Open(ReviewerPerformanceReport row) => new(
        new(row.Id, row.Name, row.CalculationVersion, row.CapturedAt, row.ExpiresAt),
        JsonSerializer.Deserialize<ReviewerPerformanceRangeResponse>(row.Payload, Json) ??
        throw new InvalidOperationException("The stored report response is invalid."), row.CalculationVersion == ReviewerPerformanceScoreCalculator.Version);
}
