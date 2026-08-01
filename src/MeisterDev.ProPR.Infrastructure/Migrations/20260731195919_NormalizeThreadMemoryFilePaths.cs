using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeisterDev.ProPR.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NormalizeThreadMemoryFilePaths : Migration
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not reversible per row: the canonical form is the same string whether a path arrived with a
            // leading slash or without one, so the original cannot be told apart. Reverting the application
            // alone does not restore the previous behaviour either, it inverts which lookup matches, so a
            // rollback of this change means reverting the readers deliberately rather than by side effect.
        }
    }
}
