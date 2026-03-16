CREATE TABLE IF NOT EXISTS personal_ai_sessions (
  id BIGSERIAL PRIMARY KEY,
  title TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS personal_ai_messages (
  id BIGSERIAL PRIMARY KEY,
  session_id BIGINT NOT NULL REFERENCES personal_ai_sessions(id) ON DELETE CASCADE,
  role TEXT NOT NULL CHECK (role IN ('user', 'assistant', 'system')),
  content TEXT NOT NULL,
  model TEXT,
  context_json JSONB NOT NULL DEFAULT '{}'::JSONB,
  sources_json JSONB NOT NULL DEFAULT '[]'::JSONB,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS personal_ai_memory (
  id BIGSERIAL PRIMARY KEY,
  memory_kind TEXT NOT NULL CHECK (memory_kind IN ('answer_note', 'web_learning', 'manual_note')),
  title TEXT NOT NULL,
  content TEXT NOT NULL,
  source_kind TEXT NOT NULL DEFAULT 'assistant',
  source_url TEXT,
  sources_json JSONB NOT NULL DEFAULT '[]'::JSONB,
  confidence NUMERIC(5, 2) NOT NULL DEFAULT 0.50,
  learned_from_query TEXT,
  last_used_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_personal_ai_messages_session_created
  ON personal_ai_messages (session_id, created_at DESC);

CREATE INDEX IF NOT EXISTS idx_personal_ai_sessions_updated
  ON personal_ai_sessions (updated_at DESC);

CREATE INDEX IF NOT EXISTS idx_personal_ai_memory_search
  ON personal_ai_memory
  USING GIN (to_tsvector('simple', COALESCE(title, '') || ' ' || COALESCE(content, '')));

CREATE INDEX IF NOT EXISTS idx_personal_ai_memory_updated
  ON personal_ai_memory (updated_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS idx_personal_ai_memory_web_source_url
  ON personal_ai_memory (source_url)
  WHERE source_kind = 'web' AND source_url IS NOT NULL;

DROP TRIGGER IF EXISTS trg_personal_ai_sessions_updated_at ON personal_ai_sessions;
CREATE TRIGGER trg_personal_ai_sessions_updated_at
BEFORE UPDATE ON personal_ai_sessions
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();

DROP TRIGGER IF EXISTS trg_personal_ai_memory_updated_at ON personal_ai_memory;
CREATE TRIGGER trg_personal_ai_memory_updated_at
BEFORE UPDATE ON personal_ai_memory
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();
