CREATE TABLE IF NOT EXISTS crm_zones (
  id BIGSERIAL PRIMARY KEY,
  name TEXT NOT NULL UNIQUE,
  code TEXT NOT NULL UNIQUE,
  description TEXT,
  active BOOLEAN NOT NULL DEFAULT TRUE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS crm_users (
  id BIGSERIAL PRIMARY KEY,
  full_name TEXT NOT NULL,
  email TEXT NOT NULL UNIQUE,
  role TEXT NOT NULL DEFAULT 'seller',
  phone TEXT,
  active BOOLEAN NOT NULL DEFAULT TRUE,
  zone_id BIGINT REFERENCES crm_zones(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS crm_assignment_history (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT NOT NULL REFERENCES opportunities(id) ON DELETE CASCADE,
  assigned_user_id BIGINT REFERENCES crm_users(id) ON DELETE SET NULL,
  zone_id BIGINT REFERENCES crm_zones(id) ON DELETE SET NULL,
  previous_status TEXT,
  new_status TEXT,
  notes TEXT,
  changed_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

ALTER TABLE opportunities
  ADD COLUMN IF NOT EXISTS zone_id BIGINT,
  ADD COLUMN IF NOT EXISTS assigned_user_id BIGINT,
  ADD COLUMN IF NOT EXISTS priority TEXT NOT NULL DEFAULT 'normal',
  ADD COLUMN IF NOT EXISTS crm_notes TEXT,
  ADD COLUMN IF NOT EXISTS assignment_updated_at TIMESTAMPTZ;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'opportunities_zone_id_fkey'
  ) THEN
    ALTER TABLE opportunities
      ADD CONSTRAINT opportunities_zone_id_fkey
      FOREIGN KEY (zone_id) REFERENCES crm_zones(id) ON DELETE SET NULL;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'opportunities_assigned_user_id_fkey'
  ) THEN
    ALTER TABLE opportunities
      ADD CONSTRAINT opportunities_assigned_user_id_fkey
      FOREIGN KEY (assigned_user_id) REFERENCES crm_users(id) ON DELETE SET NULL;
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_opportunities_zone_id
  ON opportunities(zone_id);

CREATE INDEX IF NOT EXISTS idx_opportunities_assigned_user_id
  ON opportunities(assigned_user_id);

CREATE INDEX IF NOT EXISTS idx_opportunities_estado
  ON opportunities(estado);

CREATE INDEX IF NOT EXISTS idx_assignment_history_opportunity_id
  ON crm_assignment_history(opportunity_id);

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

INSERT INTO crm_zones (name, code, description)
VALUES
  ('Nacional', 'NAC', 'Cobertura nacional y cuentas estrategicas'),
  ('Costa', 'COS', 'Procesos comerciales de la region Costa'),
  ('Sierra', 'SIE', 'Procesos comerciales de la region Sierra'),
  ('Amazonia', 'AMA', 'Procesos comerciales de la region Amazonia')
ON CONFLICT (code) DO NOTHING;
