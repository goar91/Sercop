UPDATE keyword_rules
SET
  active = FALSE,
  notes = CONCAT(COALESCE(notes, ''), CASE WHEN COALESCE(notes, '') = '' THEN '' ELSE ' | ' END, 'desactivado para enfocar insumos y materiales de laboratorio'),
  updated_at = NOW()
WHERE rule_type = 'include'
  AND scope = 'all'
  AND keyword IN ('desinfectante', 'detergente', 'analisis de laboratorio');

INSERT INTO keyword_rules (rule_type, scope, keyword, family, weight, notes, active)
VALUES
  ('include', 'all', 'kit de reactivos', 'laboratorio', 1.60, 'kits y consumibles de laboratorio', TRUE),
  ('include', 'all', 'kits de reactivos', 'laboratorio', 1.60, 'kits y consumibles de laboratorio', TRUE),
  ('include', 'all', 'pruebas rapidas', 'laboratorio', 1.40, 'pruebas y tiras de laboratorio', TRUE),
  ('include', 'all', 'material de referencia', 'laboratorio', 1.50, 'material de referencia certificado', TRUE),
  ('include', 'all', 'materiales de referencia', 'laboratorio', 1.50, 'material de referencia certificado', TRUE),
  ('include', 'all', 'estandares', 'laboratorio', 1.40, 'estandares analiticos', TRUE),
  ('include', 'all', 'medio de cultivo', 'laboratorio', 1.40, 'microbiologia y control de calidad', TRUE),
  ('include', 'all', 'medios de cultivo', 'laboratorio', 1.40, 'microbiologia y control de calidad', TRUE),
  ('include', 'all', 'calibrador', 'laboratorio', 1.30, 'calibradores para reactivos y equipos analiticos', TRUE),
  ('include', 'all', 'control', 'laboratorio', 1.10, 'controles para laboratorio', TRUE),
  ('exclude', 'all', 'servicio de', 'ruido', 1.30, 'excluir servicios', TRUE),
  ('exclude', 'all', 'mantenimiento', 'ruido', 1.30, 'excluir mantenimiento', TRUE),
  ('exclude', 'all', 'calibracion', 'ruido', 1.10, 'excluir calibracion de servicios y equipos', TRUE),
  ('exclude', 'all', 'desaduanizacion', 'ruido', 1.30, 'excluir servicios logisticos', TRUE),
  ('exclude', 'all', 'equipos de laboratorio', 'ruido', 1.40, 'excluir equipos mayores', TRUE),
  ('exclude', 'all', 'adquisicion de equipos', 'ruido', 1.40, 'excluir adquisicion de equipos', TRUE),
  ('exclude', 'all', 'instrumentos de medicion', 'ruido', 1.40, 'excluir instrumentos de medicion', TRUE),
  ('exclude', 'all', 'vitrina termica', 'ruido', 1.20, 'excluir mobiliario/equipos', TRUE),
  ('exclude', 'all', 'incubadora', 'ruido', 1.20, 'excluir equipos', TRUE),
  ('exclude', 'all', 'centrifuga', 'ruido', 1.20, 'excluir equipos', TRUE),
  ('exclude', 'all', 'balanza', 'ruido', 1.10, 'excluir equipos', TRUE),
  ('exclude', 'all', 'electrodomesticos', 'ruido', 1.10, 'excluir equipos no consumibles', TRUE),
  ('exclude', 'all', 'accesorios electronicos', 'ruido', 1.20, 'excluir electronica', TRUE),
  ('exclude', 'all', 'aire acondicionado', 'ruido', 1.20, 'excluir HVAC', TRUE),
  ('exclude', 'all', 'invernadero', 'ruido', 1.20, 'excluir infraestructura agricola', TRUE),
  ('exclude', 'all', 'malla antipajaro', 'ruido', 1.10, 'excluir mallas agricolas', TRUE),
  ('exclude', 'all', 'saran', 'ruido', 1.00, 'excluir mallas agricolas', TRUE),
  ('exclude', 'all', 'marmitas', 'ruido', 1.10, 'excluir equipos de alimentos', TRUE),
  ('exclude', 'all', 'agitacion', 'ruido', 1.00, 'excluir sistemas mecánicos cuando no son insumos', TRUE),
  ('exclude', 'all', 'fertilizacion', 'ruido', 1.20, 'excluir insumos para areas verdes y agricultura', TRUE),
  ('exclude', 'all', 'malezas', 'ruido', 1.20, 'excluir control agricola', TRUE),
  ('exclude', 'all', 'plagas', 'ruido', 1.20, 'excluir control agricola', TRUE),
  ('exclude', 'all', 'areas verdes', 'ruido', 1.20, 'excluir mantenimiento de areas verdes', TRUE),
  ('exclude', 'all', 'insumos generales', 'ruido', 1.10, 'evitar grupos demasiado amplios', TRUE)
ON CONFLICT (rule_type, scope, keyword)
DO UPDATE SET
  family = EXCLUDED.family,
  weight = EXCLUDED.weight,
  notes = EXCLUDED.notes,
  active = EXCLUDED.active,
  updated_at = NOW();
