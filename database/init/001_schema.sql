CREATE TABLE IF NOT EXISTS opportunities (
  id BIGSERIAL PRIMARY KEY,
  source TEXT NOT NULL,
  external_id TEXT NOT NULL,
  ocid_or_nic TEXT NOT NULL,
  process_code TEXT,
  titulo TEXT NOT NULL,
  entidad TEXT,
  tipo TEXT,
  fecha_publicacion TIMESTAMPTZ,
  fecha_limite TIMESTAMPTZ,
  monto_ref NUMERIC(18, 2),
  moneda TEXT DEFAULT 'USD',
  url TEXT NOT NULL DEFAULT '',
  invited_company_name TEXT,
  is_invited_match BOOLEAN NOT NULL DEFAULT FALSE,
  invitation_source TEXT,
  invitation_notes TEXT,
  invitation_evidence_url TEXT,
  invitation_verified_at TIMESTAMPTZ,
  keywords_hit TEXT[] DEFAULT ARRAY[]::TEXT[],
  match_score NUMERIC(5, 2) NOT NULL DEFAULT 0,
  ai_score NUMERIC(5, 2) NOT NULL DEFAULT 0,
  recomendacion TEXT,
  estado TEXT DEFAULT 'nuevo',
  vendedor TEXT,
  resultado TEXT,
  ai_resumen TEXT,
  ai_riesgos JSONB DEFAULT '[]'::JSONB,
  ai_checklist JSONB DEFAULT '[]'::JSONB,
  ai_estrategia_abastecimiento TEXT,
  ai_lista_cotizacion JSONB DEFAULT '[]'::JSONB,
  ai_preguntas_abiertas JSONB DEFAULT '[]'::JSONB,
  raw_payload JSONB NOT NULL DEFAULT '{}'::JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT opportunities_unique UNIQUE (source, external_id)
);

CREATE TABLE IF NOT EXISTS documents (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT NOT NULL REFERENCES opportunities(id) ON DELETE CASCADE,
  source_url TEXT NOT NULL,
  local_path TEXT,
  mime_type TEXT,
  sha256 TEXT,
  extracted_text TEXT,
  chunk_count INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS analysis_runs (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT REFERENCES opportunities(id) ON DELETE SET NULL,
  source TEXT NOT NULL DEFAULT 'workflow',
  model_name TEXT NOT NULL,
  analysis_payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS feedback_events (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT REFERENCES opportunities(id) ON DELETE SET NULL,
  source TEXT NOT NULL,
  external_id TEXT NOT NULL,
  outcome TEXT NOT NULL,
  notes TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS keyword_rules (
  id BIGSERIAL PRIMARY KEY,
  rule_type TEXT NOT NULL CHECK (rule_type IN ('include', 'exclude')),
  keyword TEXT NOT NULL,
  family TEXT,
  weight NUMERIC(5, 2) NOT NULL DEFAULT 1,
  active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT keyword_rules_unique UNIQUE (rule_type, keyword)
);

CREATE OR REPLACE FUNCTION set_updated_at()
RETURNS TRIGGER AS $$
BEGIN
  NEW.updated_at = NOW();
  RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_opportunities_updated_at ON opportunities;
CREATE TRIGGER trg_opportunities_updated_at
BEFORE UPDATE ON opportunities
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();

INSERT INTO keyword_rules (rule_type, keyword, family, weight)
VALUES
  ('include', 'quimico', 'general', 1.00),
  ('include', 'reactivo', 'laboratorio', 1.00),
  ('include', 'solvente', 'laboratorio', 1.00),
  ('include', 'desinfectante', 'sanitizacion', 1.00),
  ('include', 'acido', 'laboratorio', 1.00),
  ('include', 'base', 'laboratorio', 1.00),
  ('include', 'hipoclorito', 'sanitizacion', 1.00),
  ('exclude', 'vehiculo', 'ruido', 1.00),
  ('exclude', 'uniforme', 'ruido', 1.00),
  ('exclude', 'mantenimiento vial', 'ruido', 1.00)
ON CONFLICT DO NOTHING;
