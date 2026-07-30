using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCodeInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "code_insight_finding_id",
                table: "thread_memory_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<List<string>>(
                name: "keywords",
                table: "thread_memory_records",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'");

            migrationBuilder.AddColumn<bool>(
                name: "code_insights_collection_enabled",
                table: "clients",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "code_insight_custom_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    definition = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    retired_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_custom_tags", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_custom_tags_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_daily_counts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bucket_date = table.Column<DateOnly>(type: "date", nullable: false),
                    dimension = table.Column<short>(type: "smallint", nullable: false),
                    dimension_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    count = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_daily_counts", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_daily_counts_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    event_type = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    direction = table.Column<int>(type: "integer", nullable: false),
                    observed_value = table.Column<double>(type: "double precision", nullable: false),
                    previous_value = table.Column<double>(type: "double precision", nullable: true),
                    magnitude = table.Column<double>(type: "double precision", nullable: false),
                    threshold_value = table.Column<double>(type: "double precision", nullable: false),
                    sample_size = table.Column<int>(type: "integer", nullable: false),
                    window_from = table.Column<DateOnly>(type: "date", nullable: false),
                    window_to = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_events_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_pull_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    repository_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    pull_request_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    latest_revision_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false, defaultValue: ""),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_pull_requests", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_pull_requests_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_insight_pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ordinal = table.Column<int>(type: "integer", nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    severity = table.Column<short>(type: "smallint", nullable: false),
                    encrypted_message = table.Column<string>(type: "text", nullable: false),
                    origin_pass_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    origin_pass_index = table.Column<int>(type: "integer", nullable: true),
                    origin_pass_lens = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    origin_pass_shadow = table.Column<bool>(type: "boolean", nullable: false),
                    origin_model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    origin_logical_model_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    origin_symbol_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    origin_symbol_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    scope_relation = table.Column<short>(type: "smallint", nullable: true),
                    source_read_grounding = table.Column<short>(type: "smallint", nullable: true),
                    provider_thread_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    provider_comment_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    finding_chain_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    level = table.Column<short>(type: "smallint", nullable: true),
                    qualifier = table.Column<short>(type: "smallint", nullable: true),
                    classified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    classification_attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    classification_confidence = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_findings", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_findings_code_insight_pull_requests_code_insig~",
                        column: x => x.code_insight_pull_request_id,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_misses",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_insight_pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_thread_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    file_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    line_number = table.Column<int>(type: "integer", nullable: true),
                    encrypted_discussion = table.Column<string>(type: "text", nullable: false),
                    is_substantive = table.Column<bool>(type: "boolean", nullable: false),
                    was_acted_on = table.Column<bool>(type: "boolean", nullable: false),
                    is_in_scope = table.Column<bool>(type: "boolean", nullable: false),
                    counts_as_miss = table.Column<bool>(type: "boolean", nullable: false),
                    classifier_confidence = table.Column<double>(type: "double precision", nullable: true),
                    classifier_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    harvested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_misses", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_misses_code_insight_pull_requests_code_insight~",
                        column: x => x.code_insight_pull_request_id,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_pull_request_metrics",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_insight_pull_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    pull_request_id = table.Column<long>(type: "bigint", nullable: false),
                    addressed_count = table.Column<int>(type: "integer", nullable: false),
                    acknowledged_count = table.Column<int>(type: "integer", nullable: false),
                    dismissed_count = table.Column<int>(type: "integer", nullable: false),
                    false_positive_count = table.Column<int>(type: "integer", nullable: false),
                    miss_count = table.Column<int>(type: "integer", nullable: false),
                    discussed_count = table.Column<int>(type: "integer", nullable: false),
                    resolved_count = table.Column<int>(type: "integer", nullable: false),
                    open_at_seal_count = table.Column<int>(type: "integer", nullable: false),
                    precision = table.Column<double>(type: "double precision", nullable: true),
                    recall = table.Column<double>(type: "double precision", nullable: true),
                    f1 = table.Column<double>(type: "double precision", nullable: true),
                    acceptance_rate = table.Column<double>(type: "double precision", nullable: true),
                    close_state = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sealed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sealed_on = table.Column<DateOnly>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_pull_request_metrics", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_pull_request_metrics_code_insight_pull_request~",
                        column: x => x.code_insight_pull_request_id,
                        principalTable: "code_insight_pull_requests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_finding_dispositions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_insight_finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    disposition = table.Column<short>(type: "smallint", nullable: false),
                    source_intent = table.Column<short>(type: "smallint", nullable: false),
                    source_code_change = table.Column<short>(type: "smallint", nullable: false),
                    classifier_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    classifier_confidence = table.Column<double>(type: "double precision", nullable: true),
                    rejection_reason = table.Column<short>(type: "smallint", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_finding_dispositions", x => x.id);
                    table.ForeignKey(
                        name: "FK_code_insight_finding_dispositions_code_insight_findings_cod~",
                        column: x => x.code_insight_finding_id,
                        principalTable: "code_insight_findings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "code_insight_finding_tags",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code_insight_finding_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_core = table.Column<bool>(type: "boolean", nullable: false),
                    core_slug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    custom_tag_id = table.Column<Guid>(type: "uuid", nullable: true),
                    taxonomy_version = table.Column<int>(type: "integer", nullable: false),
                    classifier_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_insight_finding_tags", x => x.id);
                    table.CheckConstraint("ck_code_insight_finding_tags_one_reference", "(is_core AND core_slug IS NOT NULL AND custom_tag_id IS NULL) OR (NOT is_core AND custom_tag_id IS NOT NULL AND core_slug IS NULL)");
                    table.ForeignKey(
                        name: "FK_code_insight_finding_tags_code_insight_custom_tags_custom_t~",
                        column: x => x.custom_tag_id,
                        principalTable: "code_insight_custom_tags",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_insight_finding_tags_code_insight_findings_code_insigh~",
                        column: x => x.code_insight_finding_id,
                        principalTable: "code_insight_findings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_code_insight_finding_id",
                table: "thread_memory_records",
                column: "code_insight_finding_id");

            migrationBuilder.CreateIndex(
                name: "ix_thread_memory_records_keywords_gin",
                table: "thread_memory_records",
                column: "keywords")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_custom_tags_slug",
                table: "code_insight_custom_tags",
                columns: new[] { "client_id", "slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_daily_counts_client_bucket",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "bucket_date", "dimension" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_daily_counts_dimension_bucket",
                table: "code_insight_daily_counts",
                columns: new[] { "dimension", "dimension_key", "bucket_date" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_daily_counts_repo_pr_bucket",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "bucket_date" });

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_daily_counts_key",
                table: "code_insight_daily_counts",
                columns: new[] { "client_id", "repository_id", "pull_request_id", "file_path", "job_id", "bucket_date", "dimension", "dimension_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_events_client_occurred",
                table: "code_insight_events",
                columns: new[] { "client_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_events_scope_condition",
                table: "code_insight_events",
                columns: new[] { "client_id", "repository_id", "file_path", "event_type", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_finding_dispositions_disposition",
                table: "code_insight_finding_dispositions",
                column: "disposition");

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_finding_dispositions_rejection_reason",
                table: "code_insight_finding_dispositions",
                columns: new[] { "disposition", "rejection_reason" });

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_finding_dispositions_finding",
                table: "code_insight_finding_dispositions",
                column: "code_insight_finding_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_finding_tags_core_slug",
                table: "code_insight_finding_tags",
                column: "core_slug");

            migrationBuilder.CreateIndex(
                name: "IX_code_insight_finding_tags_custom_tag_id",
                table: "code_insight_finding_tags",
                column: "custom_tag_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_finding_tags_finding_id",
                table: "code_insight_finding_tags",
                column: "code_insight_finding_id");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_finding_tags_core",
                table: "code_insight_finding_tags",
                columns: new[] { "code_insight_finding_id", "core_slug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_finding_tags_custom",
                table: "code_insight_finding_tags",
                columns: new[] { "code_insight_finding_id", "custom_tag_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_findings_chain",
                table: "code_insight_findings",
                columns: new[] { "code_insight_pull_request_id", "finding_chain_id" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_findings_classification_backlog",
                table: "code_insight_findings",
                columns: new[] { "classified_at", "classification_attempts" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_findings_job_id",
                table: "code_insight_findings",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_findings_provider_thread_id",
                table: "code_insight_findings",
                column: "provider_thread_id");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_findings_natural_key",
                table: "code_insight_findings",
                columns: new[] { "code_insight_pull_request_id", "revision_key", "ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_misses_counts_as_miss",
                table: "code_insight_misses",
                column: "counts_as_miss");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_misses_thread",
                table: "code_insight_misses",
                columns: new[] { "code_insight_pull_request_id", "provider_thread_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_pull_request_metrics_client_sealed",
                table: "code_insight_pull_request_metrics",
                columns: new[] { "client_id", "sealed_on" });

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_pull_request_metrics_repo_sealed",
                table: "code_insight_pull_request_metrics",
                columns: new[] { "client_id", "repository_id", "sealed_on" });

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_pull_request_metrics_aggregate",
                table: "code_insight_pull_request_metrics",
                column: "code_insight_pull_request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_code_insight_pull_requests_last_activity_at",
                table: "code_insight_pull_requests",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "uq_code_insight_pull_requests_identity",
                table: "code_insight_pull_requests",
                columns: new[] { "client_id", "repository_id", "pull_request_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "code_insight_daily_counts");

            migrationBuilder.DropTable(
                name: "code_insight_events");

            migrationBuilder.DropTable(
                name: "code_insight_finding_dispositions");

            migrationBuilder.DropTable(
                name: "code_insight_finding_tags");

            migrationBuilder.DropTable(
                name: "code_insight_misses");

            migrationBuilder.DropTable(
                name: "code_insight_pull_request_metrics");

            migrationBuilder.DropTable(
                name: "code_insight_custom_tags");

            migrationBuilder.DropTable(
                name: "code_insight_findings");

            migrationBuilder.DropTable(
                name: "code_insight_pull_requests");

            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_code_insight_finding_id",
                table: "thread_memory_records");

            migrationBuilder.DropIndex(
                name: "ix_thread_memory_records_keywords_gin",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "code_insight_finding_id",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "keywords",
                table: "thread_memory_records");

            migrationBuilder.DropColumn(
                name: "code_insights_collection_enabled",
                table: "clients");
        }
    }
}
