CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE OR REPLACE FUNCTION crm_unaccent(input TEXT)
RETURNS TEXT
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
  SELECT public.unaccent('public.unaccent', COALESCE(input, ''));
$$;

CREATE OR REPLACE FUNCTION crm_normalize_text(input TEXT)
RETURNS TEXT
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
  SELECT btrim(regexp_replace(lower(crm_unaccent(COALESCE(input, ''))), '\s+', ' ', 'g'));
$$;

ALTER TABLE keyword_rules
  ADD COLUMN IF NOT EXISTS keyword_normalized TEXT GENERATED ALWAYS AS (crm_normalize_text(keyword)) STORED;

WITH ranked_duplicates AS (
  SELECT
    id,
    row_number() OVER (
      PARTITION BY rule_type, scope, keyword_normalized
      ORDER BY
        active DESC,
        (keyword ~ '[^[:ascii:]]') DESC,
        updated_at DESC NULLS LAST,
        id DESC
    ) AS duplicate_rank
  FROM keyword_rules
)
DELETE FROM keyword_rules rule
USING ranked_duplicates duplicate
WHERE rule.id = duplicate.id
  AND duplicate.duplicate_rank > 1;

DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'keyword_rules_unique'
      AND conrelid = 'keyword_rules'::regclass
  ) THEN
    ALTER TABLE keyword_rules DROP CONSTRAINT keyword_rules_unique;
  END IF;
END $$;

DROP INDEX IF EXISTS idx_keyword_rules_type_scope_keyword_lower;

CREATE UNIQUE INDEX IF NOT EXISTS idx_keyword_rules_type_scope_keyword_normalized
  ON keyword_rules(rule_type, scope, keyword_normalized);

ALTER TABLE opportunities
  ADD COLUMN IF NOT EXISTS search_document_normalized TEXT GENERATED ALWAYS AS (
    crm_normalize_text(
      COALESCE(titulo, '') || ' ' ||
      COALESCE(entidad, '') || ' ' ||
      COALESCE(tipo, '') || ' ' ||
      COALESCE(ocid_or_nic, '') || ' ' ||
      COALESCE(process_code, '')
    )
  ) STORED;

CREATE INDEX IF NOT EXISTS idx_opportunities_search_document_trgm
  ON opportunities USING GIN (search_document_normalized gin_trgm_ops);

CREATE TABLE IF NOT EXISTS crm_keyword_refresh_runs (
  id BIGSERIAL PRIMARY KEY,
  trigger_type TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending',
  keyword_rule_id BIGINT NULL REFERENCES keyword_rules(id) ON DELETE SET NULL,
  initiated_by_user_id BIGINT NULL REFERENCES crm_users(id) ON DELETE SET NULL,
  initiated_by_login_name TEXT NULL,
  requested_window_days INTEGER NOT NULL DEFAULT 14,
  started_at TIMESTAMPTZ NULL,
  finished_at TIMESTAMPTZ NULL,
  reevaluated_count INTEGER NOT NULL DEFAULT 0,
  captured_count INTEGER NOT NULL DEFAULT 0,
  error_count INTEGER NOT NULL DEFAULT 0,
  error_message TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT crm_keyword_refresh_runs_status_check CHECK (status IN ('pending', 'running', 'completed', 'error')),
  CONSTRAINT crm_keyword_refresh_runs_window_check CHECK (requested_window_days BETWEEN 1 AND 30)
);

DROP TRIGGER IF EXISTS trg_crm_keyword_refresh_runs_updated_at ON crm_keyword_refresh_runs;
CREATE TRIGGER trg_crm_keyword_refresh_runs_updated_at
BEFORE UPDATE ON crm_keyword_refresh_runs
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();

CREATE INDEX IF NOT EXISTS idx_crm_keyword_refresh_runs_status_created
  ON crm_keyword_refresh_runs(status, created_at DESC);
