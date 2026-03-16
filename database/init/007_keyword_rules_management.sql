ALTER TABLE keyword_rules
  ADD COLUMN IF NOT EXISTS scope TEXT NOT NULL DEFAULT 'all',
  ADD COLUMN IF NOT EXISTS notes TEXT,
  ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW();

UPDATE keyword_rules
SET
  scope = COALESCE(NULLIF(scope, ''), 'all'),
  updated_at = COALESCE(updated_at, created_at, NOW());

DO $$
BEGIN
  IF EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'keyword_rules_unique'
      AND conrelid = 'keyword_rules'::regclass
  ) THEN
    ALTER TABLE keyword_rules DROP CONSTRAINT keyword_rules_unique;
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'keyword_rules_unique'
      AND conrelid = 'keyword_rules'::regclass
  ) THEN
    ALTER TABLE keyword_rules
      ADD CONSTRAINT keyword_rules_unique UNIQUE (rule_type, scope, keyword);
  END IF;
END $$;

DO $$
BEGIN
  IF NOT EXISTS (
    SELECT 1
    FROM pg_constraint
    WHERE conname = 'keyword_rules_scope_check'
      AND conrelid = 'keyword_rules'::regclass
  ) THEN
    ALTER TABLE keyword_rules
      ADD CONSTRAINT keyword_rules_scope_check
      CHECK (scope IN ('all', 'ocds', 'nco'));
  END IF;
END $$;

DROP TRIGGER IF EXISTS trg_keyword_rules_updated_at ON keyword_rules;
CREATE TRIGGER trg_keyword_rules_updated_at
BEFORE UPDATE ON keyword_rules
FOR EACH ROW
EXECUTE FUNCTION set_updated_at();

CREATE INDEX IF NOT EXISTS idx_keyword_rules_active_scope_type
  ON keyword_rules(active, scope, rule_type);

CREATE UNIQUE INDEX IF NOT EXISTS idx_keyword_rules_type_scope_keyword_lower
  ON keyword_rules(rule_type, scope, lower(keyword));

INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
VALUES
  ('include', 'all', 'quimico', 'general', 1.00, 'termino amplio', TRUE),
  ('include', 'all', 'reactivo', 'laboratorio', 1.00, 'reactivos y kits', TRUE),
  ('include', 'all', 'solvente', 'laboratorio', 1.00, 'insumos de laboratorio', TRUE),
  ('include', 'all', 'desinfectante', 'sanitizacion', 1.00, 'limpieza y sanitizacion', TRUE),
  ('include', 'all', 'hipoclorito', 'sanitizacion', 1.00, 'cloro e insumos afines', TRUE),
  ('include', 'all', 'detergente', 'sanitizacion', 0.80, 'limpieza industrial', TRUE),
  ('include', 'all', 'acido', 'laboratorio', 1.00, 'acidos tecnicos', TRUE),
  ('include', 'all', 'hidroxido', 'laboratorio', 1.00, 'bases y neutralizantes', TRUE),
  ('include', 'all', 'etanol', 'solventes', 1.00, 'alcoholes', TRUE),
  ('include', 'all', 'isopropanol', 'solventes', 1.00, 'solventes y limpieza', TRUE),
  ('exclude', 'all', 'vehiculo', 'ruido', 1.00, 'no relacionado con quimica', TRUE),
  ('exclude', 'all', 'uniforme', 'ruido', 1.00, 'no relacionado con quimica', TRUE),
  ('exclude', 'all', 'mantenimiento vial', 'ruido', 1.00, 'obra vial', TRUE),
  ('exclude', 'all', 'construccion', 'ruido', 0.80, 'obra civil', TRUE),
  ('exclude', 'all', 'impresora', 'ruido', 0.80, 'TI y oficina', TRUE)
ON CONFLICT (rule_type, scope, keyword)
DO UPDATE SET
  family = EXCLUDED.family,
  weight = EXCLUDED.weight,
  notes = EXCLUDED.notes,
  active = EXCLUDED.active,
  updated_at = NOW();
