ALTER TABLE opportunities
  ADD COLUMN IF NOT EXISTS process_code TEXT,
  ADD COLUMN IF NOT EXISTS invitation_source TEXT,
  ADD COLUMN IF NOT EXISTS invitation_notes TEXT,
  ADD COLUMN IF NOT EXISTS invitation_evidence_url TEXT,
  ADD COLUMN IF NOT EXISTS invitation_verified_at TIMESTAMPTZ;

UPDATE opportunities
SET process_code = CASE
  WHEN source = 'ocds' AND ocid_or_nic ~ '^ocds-[^-]+-' THEN regexp_replace(ocid_or_nic, '^ocds-[^-]+-', '')
  ELSE ocid_or_nic
END
WHERE COALESCE(process_code, '') = '';

CREATE INDEX IF NOT EXISTS idx_opportunities_process_code
  ON opportunities(process_code);

CREATE INDEX IF NOT EXISTS idx_opportunities_invited_match_verified
  ON opportunities(is_invited_match, invitation_verified_at DESC);

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
