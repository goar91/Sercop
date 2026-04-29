ALTER TABLE opportunities
  ADD COLUMN IF NOT EXISTS process_category TEXT,
  ADD COLUMN IF NOT EXISTS capture_scope TEXT,
  ADD COLUMN IF NOT EXISTS is_chemistry_candidate BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS classification_payload JSONB NOT NULL DEFAULT '{}'::jsonb;

UPDATE opportunities
SET process_category = CASE
  WHEN COALESCE(process_code, ocid_or_nic) ILIKE 'NIC-%' THEN 'infimas'
  WHEN COALESCE(process_code, ocid_or_nic) ILIKE 'NC-%' THEN 'nco'
  WHEN COALESCE(process_code, ocid_or_nic) ILIKE 'SIE-%' THEN 'sie'
  WHEN COALESCE(process_code, ocid_or_nic) ILIKE 'RE-%' THEN 're'
  WHEN source = 'nco' AND COALESCE(tipo, '') ILIKE '%infima%' THEN 'infimas'
  WHEN source = 'nco' THEN 'nco'
  ELSE 'other_public'
END
WHERE COALESCE(process_category, '') = '';

UPDATE opportunities
SET capture_scope = CASE
  WHEN COALESCE(process_category, '') IN ('infimas', 'nco', 'sie', 're') THEN process_category
  ELSE 'all_public'
END
WHERE COALESCE(capture_scope, '') = '';

UPDATE opportunities
SET is_chemistry_candidate = CASE
  WHEN match_score >= 60 THEN TRUE
  ELSE FALSE
END
WHERE classification_payload = '{}'::jsonb;

UPDATE opportunities
SET classification_payload = jsonb_build_object(
  'processCategory', process_category,
  'captureScope', capture_scope,
  'isChemistryCandidate', is_chemistry_candidate,
  'reasons', CASE
    WHEN is_chemistry_candidate THEN jsonb_build_array('Clasificación inicial migrada desde match_score histórico.')
    ELSE jsonb_build_array('Clasificación inicial migrada; pendiente de reevaluación por reglas vigentes.')
  END
)
WHERE classification_payload = '{}'::jsonb;

CREATE INDEX IF NOT EXISTS idx_opportunities_process_category
  ON opportunities(process_category);

CREATE INDEX IF NOT EXISTS idx_opportunities_capture_scope
  ON opportunities(capture_scope);

CREATE INDEX IF NOT EXISTS idx_opportunities_chemistry_candidate
  ON opportunities(is_chemistry_candidate, fecha_publicacion DESC);

DROP VIEW IF EXISTS crm_opportunity_overview;

CREATE VIEW crm_opportunity_overview AS
SELECT
  o.id,
  o.source,
  o.external_id,
  o.ocid_or_nic,
  o.process_code,
  o.titulo,
  o.entidad,
  o.tipo,
  o.process_category,
  o.capture_scope,
  o.is_chemistry_candidate,
  o.classification_payload,
  o.fecha_publicacion,
  o.fecha_limite,
  o.monto_ref,
  o.url,
  o.invited_company_name,
  o.is_invited_match,
  o.invitation_source,
  o.invitation_notes,
  o.invitation_evidence_url,
  o.invitation_verified_at,
  o.match_score,
  o.ai_score,
  o.recomendacion,
  o.estado,
  o.resultado,
  o.priority,
  o.crm_notes,
  o.assignment_updated_at,
  z.id AS zone_id,
  z.name AS zone_name,
  z.code AS zone_code,
  u.id AS assigned_user_id,
  u.full_name AS assigned_user_name,
  u.email AS assigned_user_email
FROM opportunities o
LEFT JOIN crm_zones z ON z.id = o.zone_id
LEFT JOIN crm_users u ON u.id = o.assigned_user_id;
