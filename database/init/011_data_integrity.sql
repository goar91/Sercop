UPDATE opportunities
SET priority = 'normal'
WHERE priority IS NULL
   OR btrim(priority) = ''
   OR lower(priority) NOT IN ('alta', 'normal', 'baja');

UPDATE crm_users
SET role = 'seller'
WHERE role IS NULL
   OR btrim(role) = ''
   OR lower(role) NOT IN ('admin', 'gerencia', 'coordinator', 'manager', 'seller', 'analyst');

DELETE FROM crm_zones
WHERE btrim(COALESCE(name, '')) = ''
   OR btrim(COALESCE(code, '')) = '';

DELETE FROM crm_users
WHERE btrim(COALESCE(full_name, '')) = ''
   OR btrim(COALESCE(email, '')) = '';

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_zones_name_not_blank'
  ) THEN
    ALTER TABLE crm_zones
      ADD CONSTRAINT crm_zones_name_not_blank
      CHECK (char_length(btrim(name)) > 0);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_zones_code_not_blank'
  ) THEN
    ALTER TABLE crm_zones
      ADD CONSTRAINT crm_zones_code_not_blank
      CHECK (char_length(btrim(code)) > 0);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_users_full_name_not_blank'
  ) THEN
    ALTER TABLE crm_users
      ADD CONSTRAINT crm_users_full_name_not_blank
      CHECK (char_length(btrim(full_name)) > 0);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'crm_users_email_not_blank'
  ) THEN
    ALTER TABLE crm_users
      ADD CONSTRAINT crm_users_email_not_blank
      CHECK (char_length(btrim(email)) > 0);
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
      CHECK (lower(role) IN ('admin', 'gerencia', 'coordinator', 'manager', 'seller', 'analyst'));
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'opportunities_priority_valid'
  ) THEN
    ALTER TABLE opportunities
      ADD CONSTRAINT opportunities_priority_valid
      CHECK (lower(priority) IN ('alta', 'normal', 'baja'));
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'keyword_rules_keyword_not_blank'
  ) THEN
    ALTER TABLE keyword_rules
      ADD CONSTRAINT keyword_rules_keyword_not_blank
      CHECK (char_length(btrim(keyword)) > 0);
  END IF;
END $$;
