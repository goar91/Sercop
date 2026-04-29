INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
VALUES
  ('include', 'all', 'muestras biologicas', 'laboratorio', 1.50, 'muestras biologicas y procesamiento de muestras de investigacion', TRUE),
  ('include', 'all', 'recoleccion de muestras', 'laboratorio', 1.40, 'insumos para toma y recoleccion de muestras', TRUE),
  ('include', 'all', 'muestras', 'laboratorio', 1.10, 'muestras de laboratorio e investigacion', TRUE),
  ('include', 'all', 'microbiologico', 'laboratorio', 1.50, 'microbiologia y laboratorio de alimentos', TRUE),
  ('include', 'all', 'microbiologicos', 'laboratorio', 1.50, 'microbiologia y laboratorio de alimentos', TRUE),
  ('include', 'all', 'microbiologica', 'laboratorio', 1.50, 'microbiologia y laboratorio de alimentos', TRUE),
  ('include', 'all', 'microbiologicas', 'laboratorio', 1.50, 'microbiologia y laboratorio de alimentos', TRUE),
  ('include', 'all', 'biomateriales', 'laboratorio', 1.30, 'investigacion de biomateriales y sustancias quimicas', TRUE),
  ('include', 'all', 'sustancias quimicas', 'laboratorio', 1.60, 'sustancias quimicas de laboratorio', TRUE),
  ('include', 'all', 'laboratorio de alimentos', 'laboratorio', 1.50, 'laboratorio de alimentos y microbiologia', TRUE),
  ('include', 'all', 'cromatografo', 'laboratorio', 1.60, 'equipos de cromatografia para analisis quimico', TRUE),
  ('include', 'all', 'cromatografos', 'laboratorio', 1.60, 'equipos de cromatografia para analisis quimico', TRUE),
  ('include', 'all', 'cromatografia', 'laboratorio', 1.60, 'metodos y equipos de cromatografia', TRUE),
  ('include', 'all', 'pureza de hidrogeno', 'laboratorio', 1.50, 'gases de pureza usados en analisis instrumental', TRUE)
ON CONFLICT (rule_type, scope, keyword_normalized)
DO UPDATE SET
  family = EXCLUDED.family,
  weight = EXCLUDED.weight,
  notes = EXCLUDED.notes,
  active = EXCLUDED.active,
  updated_at = NOW();
