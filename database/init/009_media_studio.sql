CREATE TABLE IF NOT EXISTS crm_content_assets (
  id BIGSERIAL PRIMARY KEY,
  asset_type TEXT NOT NULL,
  asset_scope TEXT NOT NULL CHECK (asset_scope IN ('dashboard', 'opportunity', 'workflow')),
  opportunity_id BIGINT REFERENCES opportunities(id) ON DELETE CASCADE,
  workflow_id TEXT,
  title TEXT NOT NULL,
  format TEXT NOT NULL,
  audience TEXT,
  tone TEXT,
  model_name TEXT NOT NULL,
  content_text TEXT,
  payload_json JSONB NOT NULL DEFAULT '{}'::JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_crm_content_assets_scope_created
  ON crm_content_assets(asset_scope, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_crm_content_assets_opportunity
  ON crm_content_assets(opportunity_id, created_at DESC)
  WHERE opportunity_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_crm_content_assets_workflow
  ON crm_content_assets(workflow_id, created_at DESC)
  WHERE workflow_id IS NOT NULL;
