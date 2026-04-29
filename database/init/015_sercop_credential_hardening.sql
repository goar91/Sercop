ALTER TABLE IF EXISTS sercop_credentials
  ALTER COLUMN username DROP NOT NULL;

ALTER TABLE IF EXISTS sercop_credentials
  ADD COLUMN IF NOT EXISTS username_encrypted TEXT NULL;

ALTER TABLE IF EXISTS sercop_credentials
  ADD COLUMN IF NOT EXISTS masked_username TEXT NULL;

ALTER TABLE IF EXISTS sercop_credentials
  ADD COLUMN IF NOT EXISTS ruc_encrypted TEXT NULL;

ALTER TABLE IF EXISTS sercop_credentials
  ADD COLUMN IF NOT EXISTS masked_ruc TEXT NULL;

UPDATE sercop_credentials
SET masked_username = CASE
    WHEN username IS NULL OR btrim(username) = '' THEN NULL
    WHEN length(btrim(username)) <= 4 THEN repeat('*', length(btrim(username)))
    WHEN position('@' in btrim(username)) > 0 THEN
      CASE
        WHEN position('@' in btrim(username)) <= 2
          THEN substr(btrim(username), 1, 1) || '***' || substr(btrim(username), position('@' in btrim(username)))
        ELSE substr(btrim(username), 1, 2) || '***' || substr(btrim(username), position('@' in btrim(username)))
      END
    ELSE substr(btrim(username), 1, 2) || '***' || right(btrim(username), 2)
  END
WHERE masked_username IS NULL;
