INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
VALUES
  ('include', 'all', 'microrrestos', 'laboratorio', 1.40, 'analisis de microrrestos en contexto de laboratorio', TRUE),
  ('include', 'all', 'microrrestos vegetales', 'laboratorio', 1.50, 'analisis de microrrestos vegetales en investigacion/laboratorio', TRUE),
  ('include', 'all', 'analisis de leche', 'laboratorio', 1.40, 'analisis de leche y control de calidad (bromatologia/laboratorio)', TRUE),
  ('include', 'all', 'derivados de la leche', 'laboratorio', 1.30, 'analisis de productos lacteos y derivados', TRUE),
  ('include', 'all', 'hidrocarburos', 'laboratorio', 1.50, 'insumos/equipos para analisis y control en combustibles e hidrocarburos', TRUE),
  ('include', 'all', 'pasta detectora', 'laboratorio', 1.40, 'pasta detectora para control en combustibles/hidrocarburos', TRUE),
  ('include', 'all', 'pastas detectoras', 'laboratorio', 1.40, 'pastas detectoras para control en combustibles/hidrocarburos', TRUE)
ON CONFLICT (rule_type, scope, keyword_normalized)
DO UPDATE SET
  family = EXCLUDED.family,
  weight = EXCLUDED.weight,
  notes = EXCLUDED.notes,
  active = EXCLUDED.active,
  updated_at = NOW();
