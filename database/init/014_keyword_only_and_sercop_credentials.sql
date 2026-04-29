CREATE TABLE IF NOT EXISTS sercop_credentials (
  id BIGSERIAL PRIMARY KEY,
  credential_key TEXT NOT NULL UNIQUE,
  username TEXT NOT NULL,
  password_encrypted TEXT NOT NULL,
  encryption_scope TEXT NOT NULL DEFAULT 'aspnet_data_protection',
  configured_by_user_id BIGINT NULL,
  configured_by_login_name TEXT NULL,
  configured_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  validation_status TEXT NULL,
  last_validated_at TIMESTAMPTZ NULL,
  validation_error TEXT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  CONSTRAINT sercop_credentials_key_check CHECK (credential_key IN ('portal'))
);

DROP TRIGGER IF EXISTS trg_sercop_credentials_updated_at ON sercop_credentials;
CREATE TRIGGER trg_sercop_credentials_updated_at
BEFORE UPDATE ON sercop_credentials
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();
