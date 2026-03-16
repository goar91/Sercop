CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE INDEX IF NOT EXISTS idx_opportunities_invited_match
  ON opportunities(is_invited_match);

CREATE INDEX IF NOT EXISTS idx_opportunities_fecha_limite_desc
  ON opportunities(fecha_limite DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_opportunities_fecha_publicacion_desc
  ON opportunities(fecha_publicacion DESC NULLS LAST);

CREATE INDEX IF NOT EXISTS idx_documents_opportunity_created_at
  ON documents(opportunity_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_assignment_history_changed_at
  ON crm_assignment_history(opportunity_id, changed_at DESC);

CREATE INDEX IF NOT EXISTS idx_opportunities_titulo_trgm
  ON opportunities USING GIN (titulo gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_opportunities_entidad_trgm
  ON opportunities USING GIN (entidad gin_trgm_ops);

CREATE INDEX IF NOT EXISTS idx_opportunities_ocid_or_nic_trgm
  ON opportunities USING GIN (ocid_or_nic gin_trgm_ops);
