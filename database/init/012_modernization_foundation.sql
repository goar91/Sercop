ALTER TABLE crm_users
  ADD COLUMN IF NOT EXISTS login_name TEXT,
  ADD COLUMN IF NOT EXISTS password_hash TEXT,
  ADD COLUMN IF NOT EXISTS must_change_password BOOLEAN NOT NULL DEFAULT TRUE,
  ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMPTZ;

UPDATE crm_users
SET login_name = CONCAT('user_', id::text)
WHERE login_name IS NULL
   OR btrim(login_name) = '';

DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_users_role_valid'
  ) THEN
    ALTER TABLE crm_users DROP CONSTRAINT crm_users_role_valid;
  END IF;
END $$;

UPDATE crm_users
SET role = CASE
  WHEN lower(role) = 'manager' THEN 'gerencia'
  WHEN lower(role) IN ('seller', 'analyst', 'admin', 'gerencia', 'coordinator') THEN lower(role)
  ELSE 'seller'
END;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_users_login_name_not_blank'
  ) THEN
    ALTER TABLE crm_users
      ADD CONSTRAINT crm_users_login_name_not_blank
      CHECK (char_length(btrim(login_name)) > 0);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_users_role_valid'
  ) THEN
    ALTER TABLE crm_users
      ADD CONSTRAINT crm_users_role_valid
      CHECK (lower(role) IN ('admin', 'gerencia', 'coordinator', 'seller', 'analyst'));
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS idx_crm_users_login_name
  ON crm_users (lower(login_name));

CREATE TABLE IF NOT EXISTS crm_opportunity_activities (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT NOT NULL REFERENCES opportunities(id) ON DELETE CASCADE,
  activity_type TEXT NOT NULL,
  body TEXT,
  metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_by_user_id BIGINT REFERENCES crm_users(id) ON DELETE SET NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_opportunity_activities_type_valid'
  ) THEN
    ALTER TABLE crm_opportunity_activities
      ADD CONSTRAINT crm_opportunity_activities_type_valid
      CHECK (lower(activity_type) IN ('note', 'assignment', 'status_change', 'invitation_confirmation', 'reminder', 'system'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_crm_opportunity_activities_opportunity_id
  ON crm_opportunity_activities (opportunity_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_crm_opportunity_activities_created_by
  ON crm_opportunity_activities (created_by_user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS crm_reminders (
  id BIGSERIAL PRIMARY KEY,
  opportunity_id BIGINT NOT NULL REFERENCES opportunities(id) ON DELETE CASCADE,
  remind_at TIMESTAMPTZ NOT NULL,
  notes TEXT,
  created_by_user_id BIGINT REFERENCES crm_users(id) ON DELETE SET NULL,
  completed_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_crm_reminders_opportunity_active
  ON crm_reminders (opportunity_id, completed_at, remind_at DESC);

CREATE INDEX IF NOT EXISTS idx_crm_reminders_due
  ON crm_reminders (completed_at, remind_at);

CREATE TABLE IF NOT EXISTS crm_saved_views (
  id BIGSERIAL PRIMARY KEY,
  user_id BIGINT NOT NULL REFERENCES crm_users(id) ON DELETE CASCADE,
  view_type TEXT NOT NULL,
  name TEXT NOT NULL,
  filters_json JSONB NOT NULL DEFAULT '{}'::jsonb,
  shared BOOLEAN NOT NULL DEFAULT FALSE,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_saved_views_type_valid'
  ) THEN
    ALTER TABLE crm_saved_views
      ADD CONSTRAINT crm_saved_views_type_valid
      CHECK (lower(view_type) IN ('commercial'));
  END IF;
END $$;

CREATE INDEX IF NOT EXISTS idx_crm_saved_views_user_type
  ON crm_saved_views (user_id, view_type, updated_at DESC);

CREATE TABLE IF NOT EXISTS crm_audit_logs (
  id BIGSERIAL PRIMARY KEY,
  actor_user_id BIGINT REFERENCES crm_users(id) ON DELETE SET NULL,
  actor_login_name TEXT,
  action_type TEXT NOT NULL,
  entity_type TEXT NOT NULL,
  entity_id TEXT,
  ip_address TEXT,
  user_agent TEXT,
  details_json JSONB NOT NULL DEFAULT '{}'::jsonb,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_crm_audit_logs_actor
  ON crm_audit_logs (actor_user_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_crm_audit_logs_entity
  ON crm_audit_logs (entity_type, entity_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_opportunities_assignment_updated_at
  ON opportunities (assignment_updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_opportunities_fecha_publicacion
  ON opportunities (fecha_publicacion DESC);

CREATE INDEX IF NOT EXISTS idx_opportunities_fecha_limite
  ON opportunities (fecha_limite DESC);

CREATE INDEX IF NOT EXISTS idx_opportunities_resultado
  ON opportunities (resultado);

CREATE INDEX IF NOT EXISTS idx_opportunities_process_code
  ON opportunities (process_code);
