-- Draws the stratified sample for a Code Insights calibration round.
--
-- The protocol this implements, including what to do with the sample once it is drawn, is in
-- docs/reference/code-insight-calibration.md. Read that first: a sample nobody labels by the stated
-- procedure measures nothing.
--
-- Reproducible by construction. The draw order is a hash of the seed and the finding id, so the same
-- database, the same window and the same seed give the same sample to anybody who runs this, in any
-- order, on any machine. Nothing here writes, so it is safe to run against a production replica.
--
-- Usage (defaults in the \set lines below):
--   psql "$DB_CONNECTION_STRING" \
--     -v seed=calibration-2026-07 -v from_date=2026-01-01 -v to_date=2026-07-31 -v per_stratum=12 \
--     -f scripts/code-insight-calibration-sample.sql
--
-- The output is one row per sampled finding, with the columns a labeller needs and nothing more. It
-- deliberately does NOT include the recorded outcome or reason: a labeller who can see the machine's
-- answer is no longer an independent one, and the agreement measured against a primed labeller is
-- worthless. Join the answers back on finding_id after both labellers are done.

\if :{?seed} \else \set seed 'calibration-round-1' \endif
\if :{?from_date} \else \set from_date '2026-01-01' \endif
\if :{?to_date} \else \set to_date '2099-12-31' \endif
\if :{?per_stratum} \else \set per_stratum 12 \endif

WITH concern_class AS (
    -- The functional-versus-evolvability split, kept in step with CodeInsightCoreTaxonomy.ConcernClassOf
    -- by CodeInsightCalibrationSampleTests. Change one and that test fails until you change the other.
    SELECT * FROM (VALUES
        ('logic-error', 'functional'),
        ('data-validation', 'functional'),
        ('resource-handling', 'functional'),
        ('concurrency', 'functional'),
        ('security', 'functional'),
        ('performance', 'functional'),
        ('error-handling-observability', 'functional'),
        ('api-contract', 'evolvability'),
        ('design-structure', 'evolvability'),
        ('naming-clarity', 'evolvability'),
        ('documentation-tests', 'evolvability')
    ) AS mapping(core_slug, concern_class)
),
population AS (
    SELECT
        f.id AS finding_id,
        pr.client_id,
        pr.repository_id,
        pr.pull_request_id,
        f.file_path,
        f.line_number,
        f.severity,
        d.disposition,
        -- Functional wins where a finding carries types from both classes, which is the rule the
        -- application applies too: a logic error that is also hard to read is a logic error.
        COALESCE(
            MAX(CASE WHEN c.concern_class = 'functional' THEN 'functional' END),
            MAX(CASE WHEN c.concern_class = 'evolvability' THEN 'evolvability' END),
            'untyped') AS concern_class
    FROM code_insight_findings f
    JOIN code_insight_pull_requests pr ON pr.id = f.code_insight_pull_request_id
    JOIN code_insight_finding_dispositions d ON d.code_insight_finding_id = f.id
    LEFT JOIN code_insight_finding_tags t
        ON t.code_insight_finding_id = f.id AND t.is_core AND t.core_slug IS NOT NULL
    LEFT JOIN concern_class c ON c.core_slug = t.core_slug
    WHERE f.observed_at >= :'from_date'::date
      AND f.observed_at < (:'to_date'::date + 1)
    GROUP BY f.id, pr.client_id, pr.repository_id, pr.pull_request_id,
             f.file_path, f.line_number, f.severity, d.disposition
),
drawn AS (
    SELECT
        population.*,
        ROW_NUMBER() OVER (
            -- One stratum per outcome, concern class and severity. A rare outcome is represented rather
            -- than swamped, which is the whole reason to stratify instead of taking a flat random slice.
            PARTITION BY disposition, concern_class, severity
            ORDER BY md5(:'seed' || finding_id::text)
        ) AS pick,
        COUNT(*) OVER (PARTITION BY disposition, concern_class, severity) AS stratum_size
    FROM population
)
SELECT
    finding_id,
    client_id,
    repository_id,
    pull_request_id,
    file_path,
    line_number,
    -- Names rather than ordinals, so a labeller reads the sample without a lookup table.
    CASE severity
        WHEN 0 THEN 'info'
        WHEN 1 THEN 'warning'
        WHEN 2 THEN 'error'
        WHEN 3 THEN 'suggestion'
        ELSE 'unknown'
    END AS severity,
    concern_class,
    stratum_size,
    -- Which stratum this row came from, so a short stratum is visible in the sample itself rather than
    -- discovered when the counts do not add up.
    disposition AS stratum_outcome
FROM drawn
WHERE pick <= :per_stratum
ORDER BY stratum_outcome, concern_class, severity, pick;
