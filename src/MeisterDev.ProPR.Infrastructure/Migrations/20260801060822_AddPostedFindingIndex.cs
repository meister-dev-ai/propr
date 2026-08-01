using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostedFindingIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "posted_finding_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    pull_request_id = table.Column<int>(type: "integer", nullable: false),
                    provider_thread_id = table.Column<long>(type: "bigint", nullable: false),
                    review_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    iteration_id = table.Column<int>(type: "integer", nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    severity = table.Column<short>(type: "smallint", nullable: false),
                    finding_message = table.Column<string>(type: "text", nullable: false),
                    embedding_vector = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_posted_finding_records", x => x.id);
                    table.ForeignKey(
                        name: "FK_posted_finding_records_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_posted_finding_records_embedding_hnsw",
                table: "posted_finding_records",
                column: "embedding_vector")
                .Annotation("Npgsql:IndexMethod", "hnsw")
                .Annotation("Npgsql:IndexOperators", new[] { "vector_cosine_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_posted_finding_records_pull_request",
                table: "posted_finding_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id" });

            migrationBuilder.CreateIndex(
                name: "uq_posted_finding_records_thread",
                table: "posted_finding_records",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "provider_thread_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "posted_finding_records");
        }
    }
}
