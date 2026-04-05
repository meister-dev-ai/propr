-- Backfill token_breakdown from protocol data for jobs that have empty breakdown
-- but DO have protocol records with tier info (ai_connection_category + model_id).
-- This fixes jobs processed before the ValueComparer was added to EF configuration.
UPDATE review_jobs j
SET token_breakdown = (
  SELECT COALESCE(jsonb_agg(t), '[]'::jsonb)
  FROM (
    SELECT
      p.ai_connection_category AS "connectionCategory",
      p.model_id               AS "modelId",
      SUM(p.total_input_tokens)  AS "totalInputTokens",
      SUM(p.total_output_tokens) AS "totalOutputTokens"
    FROM review_job_protocols p
    WHERE p.job_id = j.id
      AND p.ai_connection_category IS NOT NULL
      AND p.model_id IS NOT NULL
    GROUP BY p.ai_connection_category, p.model_id
  ) t
)
WHERE j.token_breakdown = '[]'::jsonb
  AND EXISTS (
    SELECT 1 FROM review_job_protocols p2
    WHERE p2.job_id = j.id
      AND p2.ai_connection_category IS NOT NULL
  );
