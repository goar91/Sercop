UPDATE keyword_rules
SET
  active = FALSE,
  notes = CONCAT(
    COALESCE(notes, ''),
    CASE WHEN COALESCE(notes, '') = '' THEN '' ELSE ' | ' END,
    'desactivado para excluir procesos medicos y clinicos'
  ),
  updated_at = NOW()
WHERE rule_type = 'include'
  AND scope = 'all'
  AND keyword IN ('bioquimica', 'pruebas rapidas');

INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
VALUES
  ('exclude', 'all', 'hospital', 'medico', 1.80, 'excluir entidades hospitalarias y procesos hospitalarios', TRUE),
  ('exclude', 'all', 'salud', 'medico', 1.80, 'excluir procesos del sistema de salud', TRUE),
  ('exclude', 'all', 'clinico', 'medico', 1.80, 'excluir laboratorio clinico y procesos clinicos', TRUE),
  ('exclude', 'all', 'clinica', 'medico', 1.80, 'excluir procesos clinicos', TRUE),
  ('exclude', 'all', 'medico', 'medico', 1.90, 'excluir area medica', TRUE),
  ('exclude', 'all', 'medica', 'medico', 1.90, 'excluir area medica', TRUE),
  ('exclude', 'all', 'dispositivos medicos', 'medico', 2.00, 'excluir dispositivos medicos', TRUE),
  ('exclude', 'all', 'diagnostico', 'medico', 1.70, 'excluir pruebas y procesos de diagnostico medico', TRUE),
  ('exclude', 'all', 'examenes de laboratorio', 'medico', 1.70, 'excluir examenes clinicos y medicos', TRUE),
  ('exclude', 'all', 'serologia', 'medico', 1.70, 'excluir serologia clinica', TRUE),
  ('exclude', 'all', 'hormonas', 'medico', 1.70, 'excluir quimica sanguinea y hormonas clinicas', TRUE),
  ('exclude', 'all', 'sanguinea', 'medico', 1.70, 'excluir quimica sanguinea y procesos hematologicos', TRUE),
  ('exclude', 'all', 'hematologia', 'medico', 1.80, 'excluir hematologia clinica', TRUE),
  ('exclude', 'all', 'hematologico', 'medico', 1.80, 'excluir procesos hematologicos y clinicos', TRUE),
  ('exclude', 'all', 'bioquimica', 'medico', 1.70, 'excluir bioquimica clinica', TRUE),
  ('exclude', 'all', 'pcr', 'medico', 1.70, 'excluir biologia molecular clinica', TRUE),
  ('exclude', 'all', 'vih', 'medico', 2.00, 'excluir diagnostico clinico', TRUE),
  ('exclude', 'all', 'hiv', 'medico', 2.00, 'excluir diagnostico clinico', TRUE),
  ('exclude', 'all', 'bcr/abl', 'medico', 2.00, 'excluir marcadores clinicos', TRUE),
  ('exclude', 'all', 'jak2', 'medico', 2.00, 'excluir marcadores clinicos', TRUE),
  ('exclude', 'all', 'veterinario', 'medico', 1.70, 'excluir procesos de diagnostico veterinario', TRUE),
  ('exclude', 'all', 'veterinaria', 'medico', 1.70, 'excluir procesos de diagnostico veterinario', TRUE),
  ('exclude', 'all', 'apoyo tecnologico', 'medico', 1.50, 'excluir reactivos clinicos con apoyo tecnologico', TRUE),
  ('exclude', 'all', 'convenio de uso', 'medico', 1.50, 'excluir reactivos clinicos con equipo en convenio', TRUE),
  ('exclude', 'all', 'pruebas rapidas', 'medico', 1.80, 'excluir pruebas rapidas medicas', TRUE),
  ('exclude', 'all', 'farmacotecnia', 'medico', 1.70, 'excluir area farmaceutica hospitalaria', TRUE)
ON CONFLICT (rule_type, scope, keyword)
DO UPDATE SET
  family = EXCLUDED.family,
  weight = EXCLUDED.weight,
  notes = EXCLUDED.notes,
  active = EXCLUDED.active,
  updated_at = NOW();
