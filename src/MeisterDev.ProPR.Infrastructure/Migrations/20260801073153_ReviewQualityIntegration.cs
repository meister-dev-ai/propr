using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReviewQualityIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Bring stored paths to the canonical form the store now reads and writes: repository-relative,
            // forward slashes, no leading slash. Rows written from an Azure DevOps thread context carry a
            // leading slash, which no lookup could ever match because every lookup uses the
            // repository-relative form the review pipeline works in.
            //
            // The expression mirrors the canonicalization the application applies, in the same order, down to
            // a path that trims away to nothing becoming null rather than empty, so a migrated row and a
            // freshly written one are the same value. The unique constraint covers client, repository and
            // thread and not the path, so collapsing two forms cannot collide.
            //
            // Stored embedding vectors are left alone: they encode the path as it read when the vector was
            // generated, and re-embedding the corpus is a cost decision, not a data-repair one.
            migrationBuilder.Sql(
                """
                UPDATE thread_memory_records
                SET file_path = nullif(ltrim(btrim(replace(file_path, '\', '/'), E' \t\r\n\f\v'), '/'), '')
                WHERE file_path IS NOT NULL
                  AND file_path IS DISTINCT FROM
                      nullif(ltrim(btrim(replace(file_path, '\', '/'), E' \t\r\n\f\v'), '/'), '');
                """);

            migrationBuilder.AddColumn<short>(
                name: "resolution_clarity",
                table: "thread_memory_records",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<short>(
                name: "resolution_intent",
                table: "thread_memory_records",
                type: "smallint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "output_language",
                table: "clients",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "en");

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
                    auto_resolved_by_propr = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
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
            // The schema changes below revert cleanly. The thread-memory path normalization in Up does not:
            // the canonical form is the same string whether a path arrived with a leading slash or without
            // one, so the original cannot be told apart per row. Reverting the application alone does not
            // restore the previous behaviour either, it inverts which lookup matches, so rolling this back
            // means reverting the readers deliberately rather than by side effect.
            migrationBuilder.DropTable(
                name: "posted_finding_records");

            migrationBuilder.DropColumn(
                name: "resolution_clarity",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "resolution_intent",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "output_language",
                table: "clients");
        }
    }
}
