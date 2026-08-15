using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MentionConfigurationsAndOwnedMentions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_mention_pr_scans_crawl_configurations_crawl_configuration_id",
                table: "mention_pr_scans");

            migrationBuilder.DropForeignKey(
                name: "FK_mention_project_scans_crawl_configurations_crawl_configurat~",
                table: "mention_project_scans");

            migrationBuilder.DropIndex(
                name: "uq_mention_reply_jobs_mention",
                table: "mention_reply_jobs");

            migrationBuilder.RenameColumn(
                name: "crawl_configuration_id",
                table: "mention_project_scans",
                newName: "mention_configuration_id");

            migrationBuilder.RenameColumn(
                name: "crawl_configuration_id",
                table: "mention_pr_scans",
                newName: "mention_configuration_id");

            migrationBuilder.AddColumn<string>(
                name: "mentioned_reviewer_key",
                table: "mention_reply_jobs",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                defaultValue: "");

            // Rows written before this column existed never recorded which reviewer account was addressed,
            // and it cannot be recovered. Left at the empty default they would all collide under the new
            // uniqueness rule and the index would fail to build on exactly the installations this change
            // exists for: the ones where two clients each hold a row for one comment. Each historical row
            // therefore gets a key unique to itself, which keeps every row and its recorded spend.
            //
            // These rows can no longer suppress a second answer to the same comment, because a live scan
            // computes a real key and finds no match. Nothing re-reads them: a mention configuration is new
            // on every installation, and the repository claim it carries stops the first scan from looking
            // at anything published before the operator claimed the repository.
            migrationBuilder.Sql(
                """
                UPDATE mention_reply_jobs
                SET mentioned_reviewer_key = 'legacy:' || id::text
                WHERE mentioned_reviewer_key = '';
                """);

            migrationBuilder.CreateTable(
                name: "mention_configurations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    organization_url = table.Column<string>(type: "text", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: false),
                    scan_interval_seconds = table.Column<int>(type: "integer", nullable: false, defaultValue: 60),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mention_configurations", x => x.id);
                    table.ForeignKey(
                        name: "FK_mention_configurations_clients_client_id",
                        column: x => x.client_id,
                        principalTable: "clients",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "mention_repo_filters",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    mention_configuration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    repository_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    source_provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    canonical_source_ref = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    claimed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mention_repo_filters", x => x.id);
                    table.ForeignKey(
                        name: "FK_mention_repo_filters_mention_configurations_mention_configu~",
                        column: x => x.mention_configuration_id,
                        principalTable: "mention_configurations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_mention_reply_jobs_mention",
                table: "mention_reply_jobs",
                columns: new[] { "repository_id", "pull_request_id", "thread_id", "comment_id", "mentioned_reviewer_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_mention_configurations_active",
                table: "mention_configurations",
                column: "is_active",
                filter: "is_active = true");

            migrationBuilder.CreateIndex(
                name: "ix_mention_configurations_client_id",
                table: "mention_configurations",
                column: "client_id");

            migrationBuilder.CreateIndex(
                name: "ix_mention_repo_filters_configuration_id",
                table: "mention_repo_filters",
                column: "mention_configuration_id");

            // The uniqueness the application relies on is case-insensitive: the controller compares a scope
            // path and project key that way, the scan matches repository ids that way, and an edit groups
            // them that way. A plain unique index over the raw columns would admit two rows differing only
            // in casing, which is two configurations scanning one project, and a repository pair the next
            // edit throws on. Expression indexes are not expressible through the model builder, so they are
            // created here alone: the plain column equivalents would be maintained on every write while
            // enforcing nothing, and each of these leads with the column its table is looked up by, so they
            // already serve those reads.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX uq_mention_configurations_client_project
                ON mention_configurations (client_id, provider, lower(organization_url), lower(project_id));
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX uq_mention_repo_filters_config_repository
                ON mention_repo_filters (mention_configuration_id, lower(repository_id));
                """);

            // The renamed column still holds crawl-configuration ids, which now reference nothing: the scan
            // is driven by mention configurations, and no mention configuration exists yet on any
            // installation. Left in place they would fail the foreign key added immediately below. Removing
            // them costs nothing, because a watermark says how far a configuration has scanned and every
            // configuration is about to be created from scratch.
            migrationBuilder.Sql("DELETE FROM mention_pr_scans;");
            migrationBuilder.Sql("DELETE FROM mention_project_scans;");

            migrationBuilder.AddForeignKey(
                name: "FK_mention_pr_scans_mention_configurations_mention_configurati~",
                table: "mention_pr_scans",
                column: "mention_configuration_id",
                principalTable: "mention_configurations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_mention_project_scans_mention_configurations_mention_config~",
                table: "mention_project_scans",
                column: "mention_configuration_id",
                principalTable: "mention_configurations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_mention_pr_scans_mention_configurations_mention_configurati~",
                table: "mention_pr_scans");

            migrationBuilder.DropForeignKey(
                name: "FK_mention_project_scans_mention_configurations_mention_config~",
                table: "mention_project_scans");

            // Watermarks reference mention configurations, which are about to be dropped, and the crawl
            // configurations the restored foreign key expects are not the same rows. Scanning resumes from
            // one look-back window after a rollback, which is the cost of going back.
            migrationBuilder.Sql("DELETE FROM mention_pr_scans;");
            migrationBuilder.Sql("DELETE FROM mention_project_scans;");

            migrationBuilder.DropTable(
                name: "mention_repo_filters");

            migrationBuilder.DropTable(
                name: "mention_configurations");

            migrationBuilder.DropIndex(
                name: "uq_mention_reply_jobs_mention",
                table: "mention_reply_jobs");

            migrationBuilder.DropColumn(
                name: "mentioned_reviewer_key",
                table: "mention_reply_jobs");

            migrationBuilder.RenameColumn(
                name: "mention_configuration_id",
                table: "mention_project_scans",
                newName: "crawl_configuration_id");

            migrationBuilder.RenameColumn(
                name: "mention_configuration_id",
                table: "mention_pr_scans",
                newName: "crawl_configuration_id");

            // The restored rule drops repository_id and adds client_id, so it is not merely narrower: rows
            // this migration legitimately allowed can collide under it. One client answering in two
            // repositories whose pull-request numbering coincides, which is every provider that numbers per
            // repository, holds two rows that differ only by a column the old rule does not look at. The
            // newest of each colliding group goes, keeping the answer that was posted first.
            //
            // Ranked rather than compared pairwise. Two rows written in the same transaction can share a
            // timestamp to the microsecond, and a strict "later than" comparison deletes neither of them,
            // so the index below would still find the duplicate and the rollback would stop half applied.
            // Ordering by the id as well leaves exactly one row standing in every group.
            migrationBuilder.Sql(
                """
                DELETE FROM mention_reply_jobs
                WHERE id IN (
                    SELECT id FROM (
                        SELECT id, row_number() OVER (
                            PARTITION BY client_id, pull_request_id, thread_id, comment_id
                            ORDER BY created_at, id
                        ) AS rank
                        FROM mention_reply_jobs
                    ) ranked
                    WHERE ranked.rank > 1
                );
                """);

            migrationBuilder.CreateIndex(
                name: "uq_mention_reply_jobs_mention",
                table: "mention_reply_jobs",
                columns: new[] { "client_id", "pull_request_id", "thread_id", "comment_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_mention_pr_scans_crawl_configurations_crawl_configuration_id",
                table: "mention_pr_scans",
                column: "crawl_configuration_id",
                principalTable: "crawl_configurations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_mention_project_scans_crawl_configurations_crawl_configurat~",
                table: "mention_project_scans",
                column: "crawl_configuration_id",
                principalTable: "crawl_configurations",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
