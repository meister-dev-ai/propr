// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace MeisterDev.ProPR.Infrastructure.Data.Configurations;

internal sealed class ReviewerPerformanceReportConfiguration : IEntityTypeConfiguration<ReviewerPerformanceReport>
{
    public void Configure(EntityTypeBuilder<ReviewerPerformanceReport> builder)
    {
        builder.ToTable("reviewer_performance_reports");
        builder.HasKey(row => row.Id);
        builder.Property(row => row.Id).ValueGeneratedNever();
        builder.Property(row => row.Name).HasMaxLength(160);
        builder.Property(row => row.CalculationVersion).HasMaxLength(64);
        builder.Property(row => row.RequestFingerprint).HasMaxLength(64);
        builder.Property(row => row.Payload).HasColumnType("text");
        builder.HasIndex(row => row.ExpiresAt);
        builder.HasMany(row => row.Clients).WithOne().HasForeignKey(row => row.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ReviewerPerformanceReportClientConfiguration : IEntityTypeConfiguration<ReviewerPerformanceReportClient>
{
    public void Configure(EntityTypeBuilder<ReviewerPerformanceReportClient> builder)
    {
        builder.ToTable("reviewer_performance_report_clients");
        builder.HasKey(row => new { row.ReportId, row.ClientId });
        builder.HasIndex(row => row.ClientId);
        builder.HasOne<ClientRecord>().WithMany().HasForeignKey(row => row.ClientId).OnDelete(DeleteBehavior.Cascade);
    }
}
