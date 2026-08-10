using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RunnerIngestReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "runner_ingest_receipts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_runner_ingest_receipts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_runner_ingest_receipts_job_key",
                table: "runner_ingest_receipts",
                columns: new[] { "job_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_runner_ingest_receipts_job_sequence",
                table: "runner_ingest_receipts",
                columns: new[] { "job_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "runner_ingest_receipts");
        }
    }
}
