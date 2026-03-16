import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = dirname(__dirname);
const workflowsDir = join(root, 'workflows');

mkdirSync(workflowsDir, { recursive: true });

const postgresCredential = {
  postgres: {
    id: 'QX2Kr6LtP0sGmA1b',
    name: 'Local Postgres CRM',
  },
};

const smtpCredential = {
  smtp: {
    id: 'Sm7Ny4BcT2rVhP8q',
    name: 'Local SMTP Mailpit',
  },
};

const baseSettings = {
  executionOrder: 'v1',
};

const baseMeta = {
  templateCredsSetupCompleted: true,
};

const upsertOpportunityQuery = `INSERT INTO opportunities (
  source,
  external_id,
  ocid_or_nic,
  process_code,
  titulo,
  entidad,
  tipo,
  fecha_publicacion,
  fecha_limite,
  monto_ref,
  moneda,
  url,
  invited_company_name,
  is_invited_match,
  keywords_hit,
  match_score,
  ai_score,
  recomendacion,
  estado,
  vendedor,
  resultado,
  raw_payload
) VALUES (
  '{{$json.source_sql}}',
  '{{$json.external_id_sql}}',
  '{{$json.ocid_or_nic_sql}}',
  '{{$json.process_code_sql}}',
  '{{$json.titulo_sql}}',
  '{{$json.entidad_sql}}',
  '{{$json.tipo_sql}}',
  CASE WHEN '{{$json.fecha_publicacion_sql}}' = '' THEN NULL ELSE '{{$json.fecha_publicacion_sql}}'::timestamptz END,
  CASE WHEN '{{$json.fecha_limite_sql}}' = '' THEN NULL ELSE '{{$json.fecha_limite_sql}}'::timestamptz END,
  {{$json.monto_ref_sql}},
  '{{$json.moneda_sql}}',
  '{{$json.url_sql}}',
  CASE WHEN {{$json.is_invited_match_sql}} THEN '{{$json.invited_company_name_sql}}' ELSE NULL END,
  {{$json.is_invited_match_sql}},
  CASE WHEN '{{$json.keywords_hit_csv}}' = '' THEN ARRAY[]::text[] ELSE string_to_array('{{$json.keywords_hit_csv}}', ',') END,
  {{$json.match_score}},
  {{$json.ai_score}},
  '{{$json.recomendacion_sql}}',
  '{{$json.estado}}',
  '{{$json.vendedor}}',
  '{{$json.resultado}}',
  '{{$json.raw_payload_string}}'::jsonb
)
ON CONFLICT (source, external_id)
DO UPDATE SET
  process_code = COALESCE(NULLIF(EXCLUDED.process_code, ''), opportunities.process_code),
  titulo = EXCLUDED.titulo,
  entidad = EXCLUDED.entidad,
  tipo = EXCLUDED.tipo,
  fecha_publicacion = EXCLUDED.fecha_publicacion,
  fecha_limite = EXCLUDED.fecha_limite,
  monto_ref = COALESCE(EXCLUDED.monto_ref, opportunities.monto_ref),
  moneda = COALESCE(NULLIF(EXCLUDED.moneda, ''), opportunities.moneda),
  url = EXCLUDED.url,
  invited_company_name = CASE
    WHEN EXCLUDED.is_invited_match THEN EXCLUDED.invited_company_name
    ELSE opportunities.invited_company_name
  END,
  is_invited_match = opportunities.is_invited_match OR EXCLUDED.is_invited_match,
  keywords_hit = EXCLUDED.keywords_hit,
  match_score = EXCLUDED.match_score,
  recomendacion = EXCLUDED.recomendacion,
  raw_payload = EXCLUDED.raw_payload,
  updated_at = NOW()
RETURNING
  id,
  source,
  external_id,
  titulo,
  entidad,
  tipo,
  url,
  match_score,
  fecha_limite,
  invited_company_name,
  is_invited_match,
  keywords_hit,
  recomendacion,
  estado,
  (xmax = 0) AS was_inserted;`;

const filterEmailCandidatesCode = `return $input
  .all()
  .filter((item) => Boolean(item.json.was_inserted) && Boolean(item.json.is_invited_match));`;

const ocdsSeedSearchTermsCode = `const rules = $items('Load Keyword Rules').map((item) => item.json);
const ignoredKeywords = new Set(['acido', 'base', 'quimico']);
const familyBundles = {
  sanitizacion: ['hipoclorito desinfectante'],
  solventes: ['etanol solvente'],
};
const keywordPriority = {
  reactivo: 100,
  'equipos de laboratorio': 99,
  'insumos de laboratorio': 98,
  'material de laboratorio': 97,
  hipoclorito: 95,
  desinfectante: 90,
  laboratorio: 85,
  etanol: 80,
  solvente: 75,
  hidroxido: 70,
  detergente: 65,
  isopropanol: 60,
};
const maxSearchTerms = Math.max(6, Math.min(Number($env.OCDS_MAX_SEARCH_TERMS || 12), 16));
const lookbackDays = Math.max(0, Math.min(Number($env.PROCESS_PUBLICATION_LOOKBACK_DAYS ?? $env.OCDS_PUBLIC_LOOKBACK_DAYS ?? 0), 180));

function formatLocalDate(value) {
  const year = value.getFullYear();
  const month = String(value.getMonth() + 1).padStart(2, '0');
  const day = String(value.getDate()).padStart(2, '0');
  return year + '-' + month + '-' + day;
}

const today = new Date();
const todayStart = new Date(today.getFullYear(), today.getMonth(), today.getDate());
const dateTo = formatLocalDate(todayStart);
const dateFrom = formatLocalDate(new Date(todayStart.getTime() - lookbackDays * 24 * 60 * 60 * 1000));
const includeRules = rules
  .filter((rule) => String(rule.rule_type || '') === 'include')
  .map((rule) => ({
    keyword: String(rule.keyword || '').trim().toLowerCase(),
    family: String(rule.family || '').trim().toLowerCase(),
    weight: Number(rule.weight || 1) || 1,
  }))
  .filter((rule) => rule.keyword && !ignoredKeywords.has(rule.keyword))
  .sort((left, right) =>
    (keywordPriority[right.keyword] || 0) - (keywordPriority[left.keyword] || 0) ||
    right.weight - left.weight ||
    left.keyword.localeCompare(right.keyword));
const groupedByFamily = new Map();
const seen = new Set();
const out = [];

function pushQuery(searchQuery, family, seedKeywords, weight) {
  const normalized = String(searchQuery || '').trim().toLowerCase().replace(/\\s+/g, ' ');
  if (!normalized || seen.has(normalized)) {
    return;
  }

  seen.add(normalized);
  out.push({
    json: {
      searchKeyword: Array.isArray(seedKeywords) && seedKeywords.length > 0 ? seedKeywords[0] : normalized,
      searchQuery: normalized,
      family: family || '',
      seedKeywords: Array.isArray(seedKeywords) && seedKeywords.length > 0 ? seedKeywords : [normalized],
      weight: Number(weight || 1) || 1,
      dateFrom,
      dateTo,
    },
  });
}

for (const rule of includeRules) {
  if (!rule.family) {
    continue;
  }

  const familyRules = groupedByFamily.get(rule.family) || [];
  familyRules.push(rule);
  groupedByFamily.set(rule.family, familyRules);
}

for (const [family, familyRules] of groupedByFamily.entries()) {
  const prioritizedRules = familyRules
    .slice()
    .sort((left, right) =>
      (keywordPriority[right.keyword] || 0) - (keywordPriority[left.keyword] || 0) ||
      right.keyword.split(/\\s+/).length - left.keyword.split(/\\s+/).length ||
      right.weight - left.weight ||
      left.keyword.localeCompare(right.keyword));
  const topKeywords = prioritizedRules
    .map((rule) => rule.keyword)
    .filter((keyword, index, keywords) => keywords.indexOf(keyword) === index)
    .slice(0, 4);

  if (topKeywords.length === 0) {
    continue;
  }

  const exactPhrases = prioritizedRules
    .filter((rule) => rule.keyword.includes(' '))
    .slice(0, 2);
  for (const phraseRule of exactPhrases) {
    pushQuery(phraseRule.keyword, family, [phraseRule.keyword], phraseRule.weight);
  }

  const bundledQueries = familyBundles[family] || [];
  if (bundledQueries.length > 0) {
    for (const bundledQuery of bundledQueries) {
      pushQuery(bundledQuery, family, topKeywords.slice(0, 2), prioritizedRules[0]?.weight || 1);
    }
  } else if (topKeywords.length > 1) {
    pushQuery(topKeywords.slice(0, 2).join(' '), family, topKeywords.slice(0, 2), prioritizedRules[0]?.weight || 1);
  }
}

pushQuery('laboratorio', 'laboratorio', ['reactivo'], 1);

for (const rule of includeRules) {
  pushQuery(rule.keyword, rule.family, [rule.keyword], rule.weight);
}

return out.slice(0, maxSearchTerms);`;

const ocdsPreparePublicSearchPagesCode = `const maxPages = Math.max(1, Math.min(Number($env.OCDS_PUBLIC_MAX_PAGES || 2), 4));
const out = [];

for (const item of $input.all()) {
  for (let page = 0; page < maxPages; page++) {
    out.push({
      json: {
        ...item.json,
        page,
        offset: page * 20,
      },
    });
  }
}

return out;`;

const ocdsNormalizeSearchResultsCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');
const candidates = new Map();
const maxCandidates = Math.max(10, Math.min(Number($env.OCDS_PUBLIC_MAX_CANDIDATES || 30), 60));
const includeClosed = String($env.OCDS_INCLUDE_CLOSED || 'false').toLowerCase() === 'true';
const publicationLookbackDays = Math.max(0, Math.min(Number($env.PROCESS_PUBLICATION_LOOKBACK_DAYS ?? $env.OCDS_PUBLIC_LOOKBACK_DAYS ?? 0), 180));
const typeLabels = {
  '386': 'Subasta Inversa Electrónica',
  '387': 'Licitación',
  '451': 'Publicación',
  '4205': 'Capacidad nacional',
  '4207': 'Producción Nacional',
  '4209': 'Precalificación',
  '4213': 'Contratación directa',
  '4216': 'Lista corta',
  '4217': 'Concurso público',
  '4245': 'Cotización',
  '4246': 'Menor Cuantía',
  '4504': 'Subasta Inversa Corporativa',
  '4505': 'Convenio Marco',
  '4506': 'Ferias Inclusivas',
  '4600': 'Lista Corta por Contratación Directa Desierta',
  '4601': 'Concurso Público por Contratación Directa Desierta',
  '4602': 'Concurso Público por Lista Corta Desierta',
  '4603': 'Contratación Directa por Terminación Unilateral',
};

function parseXJson(rawHeader) {
  const raw = String(rawHeader || '').trim();
  if (!raw || raw === '([])' || raw === '({})') {
    return [];
  }

  const payload = raw.startsWith('(') && raw.endsWith(')') ? raw.slice(1, -1) : raw;
  if (!payload) {
    return [];
  }

  try {
    const parsed = JSON.parse(payload);
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

function normalizeText(value) {
  return String(value ?? '')
    .toLowerCase()
    .replace(/[áàäâ]/g, 'a')
    .replace(/[éèëê]/g, 'e')
    .replace(/[íìïî]/g, 'i')
    .replace(/[óòöô]/g, 'o')
    .replace(/[úùüû]/g, 'u')
    .replace(/ñ/g, 'n');
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = String(hit.keyword || '').trim().toLowerCase();
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword,
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(deduped.values());
}

function asNumber(value) {
  const normalized = String(value ?? '').replace(/,/g, '');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function typeScore(processType) {
  const normalized = String(processType || '').trim();
  if (/convenio marco|cat[aá]logo/i.test(normalized)) {
    return 0;
  }

  if (/subasta inversa/i.test(normalized)) {
    return 25;
  }

  if (/cotizaci[oó]n|menor cuant[ií]a|licitaci[oó]n|concurso/i.test(normalized)) {
    return 18;
  }

  return normalized ? 12 : 8;
}

function stateScore(state) {
  const normalized = String(state || '').trim();
  if (/entrega de propuesta|recepci[oó]n|convalid|preguntas|aclaraciones|por adjudicar|reprogramaci[oó]n|puja|calificaci[oó]n|audiencia/i.test(normalized)) {
    return 12;
  }

  if (/suspendido|en curso/i.test(normalized)) {
    return 4;
  }

  return 0;
}

function isClosedState(state) {
  return /adjudicad|finalizad|registro de contratos|ejecuci[oó]n de contrato|desiert|cancelad|borrador/i.test(String(state || ''));
}

function buildDetailUrl(internalId, version) {
  const encodedId = encodeURIComponent(String(internalId || '').trim());
  const normalizedVersion = Number(version || 0);

  if (!encodedId) {
    return '';
  }

  if (normalizedVersion === -1 || normalizedVersion === -2) {
    return 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/SC/sci.cpe?idSoliCompra=' + encodedId;
  }

  if (normalizedVersion === 3) {
    return 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/CR/mostrarferiainicial.cpe?idSoliCompra=' + encodedId;
  }

  if (normalizedVersion === 2) {
    return 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/informacionProcesoContratacion2.cpe?idSoliCompra=' + encodedId;
  }

  return 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/informacionProcesoContratacion.cpe?idSoliCompra=' + encodedId;
}

function parseDateScore(value) {
  const timestamp = Date.parse(String(value || ''));
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function localDayStart(value) {
  const parsed = new Date(String(value || ''));
  if (Number.isNaN(parsed.getTime())) {
    return 0;
  }

  return new Date(parsed.getFullYear(), parsed.getMonth(), parsed.getDate()).getTime();
}

const publicationCutoff = new Date();
publicationCutoff.setHours(0, 0, 0, 0);
publicationCutoff.setDate(publicationCutoff.getDate() - publicationLookbackDays);
const publicationCutoffTimestamp = publicationCutoff.getTime();

function isOnOrAfterPublicationCutoff(value) {
  const timestamp = localDayStart(value);
  return timestamp !== 0 && timestamp >= publicationCutoffTimestamp;
}

const inputs = $input.all();
for (let index = 0; index < inputs.length; index++) {
  const payload = inputs[index].json;
  const seed = $item(index).$node['Prepare Public Search Pages'].json;
  const searchQuery = String(seed.searchQuery || seed.searchKeyword || '').trim().toLowerCase();
  const seedKeywords = Array.isArray(seed.seedKeywords) && seed.seedKeywords.length > 0
    ? seed.seedKeywords.map((keyword) => String(keyword || '').trim().toLowerCase()).filter(Boolean)
    : (searchQuery ? [searchQuery] : []);
  const rawHeader = payload?.headers?.['x-json'] || payload?.headers?.['X-JSON'] || '';
  const rows = parseXJson(rawHeader);

  for (const row of rows) {
    const processCode = String(row.c || '').trim();
    const internalId = String(row.i || '').trim();
    if (!processCode || !internalId) {
      continue;
    }

    const portalState = String(row.g || '').trim();
    if (!includeClosed && isClosedState(portalState)) {
      continue;
    }

    const title = String(row.d || processCode).trim();
    const entity = String(row.r || 'Sin entidad').trim();
    const processType = typeLabels[String(row.t || '').trim()] || ('Proceso ' + String(row.t || '').trim());
    const publishedAt = String(row.f || '').trim();
    if (!isOnOrAfterPublicationCutoff(publishedAt)) {
      continue;
    }

    const amount = asNumber(row.p);
    const searchHaystack = normalizeText([processCode, title, entity, processType, portalState, searchQuery].join(' '));
    const seededHits = seedKeywords.map((keyword) => ({ keyword, weight: Number(seed.weight || 1) || 1 }));
    const includeHits = uniqueHits(
      includeRules.filter((rule) => searchHaystack.includes(normalizeText(rule.keyword))).concat(seededHits),
    );
    const excludeHits = uniqueHits(
      excludeRules.filter((rule) => searchHaystack.includes(normalizeText(rule.keyword))),
    );
    const includeWeight = includeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
    const excludeWeight = excludeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
    const taxonomyScore = Math.max(0, Math.min(60, Math.round(includeWeight * 20) - Math.round(excludeWeight * 25)));
    const heuristicScore = includeHits.length > 0 ? 15 : 0;
    const previewScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + stateScore(portalState) + heuristicScore));

    if (includeHits.length === 0 || previewScore < 30) {
      continue;
    }

    const current = candidates.get(processCode) || {
      source: 'ocds',
      external_id: processCode,
      ocid_or_nic: processCode,
      process_code: processCode,
      titulo: title,
      entidad: entity,
      tipo: processType,
      fecha_publicacion: publishedAt,
      fecha_limite: '',
      monto_ref: amount,
      moneda: 'USD',
      url: buildDetailUrl(internalId, row.v),
      detail_internal_id: internalId,
      detail_version: Number(row.v || 0) || 0,
      portal_state: portalState,
      initial_keywords: [],
      raw_rows: [],
      preview_score: previewScore,
    };

    for (const keyword of seedKeywords) {
      if (!current.initial_keywords.includes(keyword)) {
        current.initial_keywords.push(keyword);
      }
    }

    current.preview_score = Math.max(current.preview_score || 0, previewScore);

    if (!current.fecha_publicacion || parseDateScore(publishedAt) > parseDateScore(current.fecha_publicacion)) {
      current.fecha_publicacion = publishedAt;
    }

    if (current.monto_ref === null && amount !== null) {
      current.monto_ref = amount;
    }

    if (stateScore(portalState) >= stateScore(current.portal_state)) {
      current.portal_state = portalState;
    }

    current.raw_rows.push({
      searchQuery,
      page: Number(seed.page || 0) || 0,
      offset: Number(seed.offset || 0) || 0,
      row,
    });

    candidates.set(processCode, current);
  }
}

return Array.from(candidates.values())
  .sort((left, right) => {
    if ((right.preview_score || 0) !== (left.preview_score || 0)) {
      return (right.preview_score || 0) - (left.preview_score || 0);
    }

    return parseDateScore(right.fecha_publicacion) - parseDateScore(left.fecha_publicacion);
  })
  .slice(0, maxCandidates)
  .map((candidate) => ({ json: candidate }));`;

const ocdsEnrichDatesCode = `function stripTags(value) {
  return String(value ?? '').replace(/<[^>]+>/g, ' ').replace(/\\s+/g, ' ').trim();
}

function decodeEntities(value) {
  return String(value ?? '')
    .replace(/&nbsp;/gi, ' ')
    .replace(/&amp;/gi, '&')
    .replace(/&quot;/gi, '"')
    .replace(/&#39;/gi, "'")
    .replace(/&ntilde;/gi, 'ñ')
    .replace(/&Ntilde;/gi, 'Ñ')
    .replace(/&aacute;/gi, 'á')
    .replace(/&eacute;/gi, 'é')
    .replace(/&iacute;/gi, 'í')
    .replace(/&oacute;/gi, 'ó')
    .replace(/&uacute;/gi, 'ú')
    .replace(/&Aacute;/gi, 'Á')
    .replace(/&Eacute;/gi, 'É')
    .replace(/&Iacute;/gi, 'Í')
    .replace(/&Oacute;/gi, 'Ó')
    .replace(/&Uacute;/gi, 'Ú');
}

function normalizeLabel(value) {
  return decodeEntities(stripTags(value))
    .toLowerCase()
    .replace(/[áàäâ]/g, 'a')
    .replace(/[éèëê]/g, 'e')
    .replace(/[íìïî]/g, 'i')
    .replace(/[óòöô]/g, 'o')
    .replace(/[úùüû]/g, 'u')
    .replace(/ñ/g, 'n')
    .replace(/\\s+/g, ' ')
    .trim();
}

function parseRows(html) {
  const rows = new Map();
  const regex = /<tr>\\s*<th[^>]*>([\\s\\S]*?)<\\/th>\\s*<td[^>]*>([\\s\\S]*?)<\\/td>/gi;
  let match = regex.exec(String(html || ''));
  while (match) {
    const label = normalizeLabel(match[1]);
    const value = decodeEntities(stripTags(match[2]));
    if (label && value) {
      rows.set(label, value);
    }
    match = regex.exec(String(html || ''));
  }
  return rows;
}

function pickDate(rows, labels) {
  for (const label of labels) {
    const value = rows.get(label);
    if (value && /^\\d{4}-\\d{2}-\\d{2}/.test(value)) {
      return value.replace(/\\s+/g, ' ').trim();
    }
  }
  return '';
}

const out = [];
for (let index = 0; index < $input.all().length; index++) {
  const candidate = { ...$item(index).$node['Normalize Search Results'].json };
  const html = String($input.all()[index].json.data || '');
  const rows = parseRows(html);
  const publishedAt = pickDate(rows, ['fecha de publicacion']);
  const deadlineAt = pickDate(rows, [
    'fecha limite entrega ofertas',
    'fecha limite de entrega ofertas',
    'fecha limite de entrega de ofertas',
    'fecha final de puja',
    'fecha estimada de adjudicacion',
  ]);

  if (publishedAt) {
    candidate.fecha_publicacion = publishedAt;
  }

  if (deadlineAt) {
    candidate.fecha_limite = deadlineAt;
  }

  out.push({ json: candidate });
}

return out;`;

const ocdsScoreCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');
const includeClosed = String($env.OCDS_INCLUDE_CLOSED || 'false').toLowerCase() === 'true';
const typeWeights = {
  'Subasta Inversa Electrónica': 25,
  'Subasta Inversa Electronica': 25,
  'Ínfimas Cuantías': 20,
  'Infima Cuantia': 20,
  'Necesidades de Contratación y Recepción de Proformas': 20,
  'Necesidades de Contratacion y Recepcion de Proformas': 20,
  'Bienes y Servicios únicos': 15,
  'Bienes y Servicios unicos': 15,
};

function sqlText(value) {
  return String(value ?? '').replace(/'/g, "''");
}

function sqlNumber(value) {
  if (value === null || value === undefined || value === '') {
    return 'NULL';
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? String(parsed) : 'NULL';
}

function normalizeText(value) {
  return String(value ?? '')
    .toLowerCase()
    .replace(/[áàäâ]/g, 'a')
    .replace(/[éèëê]/g, 'e')
    .replace(/[íìïî]/g, 'i')
    .replace(/[óòöô]/g, 'o')
    .replace(/[úùüû]/g, 'u')
    .replace(/ñ/g, 'n');
}

function normalizeProcessCode(value) {
  const raw = String(value || '').trim();
  return /-\\d{3,}$/.test(raw) ? raw.replace(/-\\d{3,}$/, '') : raw;
}

function typeScore(processType) {
  const normalized = String(processType || '').trim();
  return typeWeights[normalized] ?? (normalized ? 15 : 10);
}

function stateScore(state) {
  const normalized = String(state || '').trim();
  if (/entrega de propuesta|recepci[oó]n|convalid|preguntas|aclaraciones|por adjudicar|reprogramaci[oó]n|puja|calificaci[oó]n|audiencia/i.test(normalized)) {
    return 12;
  }

  if (/suspendido|en curso/i.test(normalized)) {
    return 4;
  }

  return 0;
}

function isClosedState(state) {
  return /adjudicad|finalizad|registro de contratos|ejecuci[oó]n de contrato|desiert|cancelad|borrador/i.test(String(state || ''));
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = String(hit.keyword || '').trim().toLowerCase();
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword,
      family: hit.family || null,
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(deduped.values());
}

const out = [];
for (const input of $input.all()) {
  const candidate = input.json;
  const title = String(candidate.titulo || candidate.ocid_or_nic || 'Sin titulo').trim();
  const entity = String(candidate.entidad || 'Sin entidad').trim();
  const processType = String(candidate.tipo || 'Sin tipo').trim();
  const portalState = String(candidate.portal_state || '').trim();

  if (!includeClosed && isClosedState(portalState)) {
    continue;
  }

  const publishedAt = String(candidate.fecha_publicacion || '').trim();
  const deadlineAt = String(candidate.fecha_limite || '').trim();
  const amount = candidate.monto_ref ?? null;
  const processCode = normalizeProcessCode(candidate.process_code || candidate.ocid_or_nic);
  const currency = String(candidate.moneda || 'USD').trim() || 'USD';
  const haystack = normalizeText([
    title,
    entity,
    processType,
    portalState,
    ...(Array.isArray(candidate.initial_keywords) ? candidate.initial_keywords : []),
    JSON.stringify(candidate.raw_rows || {}),
  ].join(' '));

  const seededHits = Array.isArray(candidate.initial_keywords)
    ? candidate.initial_keywords.map((keyword) => ({ keyword, weight: 1 }))
    : [];
  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())).concat(seededHits),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())),
  );
  const includeWeight = includeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const excludeWeight = excludeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const taxonomyScore = Math.max(0, Math.min(60, Math.round(includeWeight * 20) - Math.round(excludeWeight * 25)));
  const heuristicScore = includeHits.length > 0 ? 15 : 0;
  const baseScore = Number(candidate.preview_score || 0) || 0;
  const matchScore = Math.max(0, Math.min(100, Math.max(baseScore, taxonomyScore + typeScore(processType) + stateScore(portalState) + heuristicScore)));

  if (matchScore < threshold) {
    continue;
  }

  const recommendation = /entrega de propuesta|recepci[oó]n|preguntas|aclaraciones|convalid/i.test(portalState) ? 'revisar' : 'observacion';
  const rawPayload = {
    source: 'ocds_public_search',
    detail_internal_id: candidate.detail_internal_id || null,
    detail_version: candidate.detail_version || null,
    portal_state: portalState,
    search_matches: candidate.raw_rows || [],
    keywords: candidate.initial_keywords || [],
  };

  out.push({
    json: {
      source: 'ocds',
      external_id: candidate.external_id,
      ocid_or_nic: candidate.ocid_or_nic,
      process_code: processCode,
      titulo: title,
      entidad: entity,
      tipo: processType,
      fecha_publicacion: publishedAt,
      fecha_limite: deadlineAt,
      monto_ref: amount,
      moneda: currency,
      url: candidate.url,
      invited_company_name: null,
      is_invited_match: false,
      keywords_hit: includeHits.map((hit) => hit.keyword),
      keywords_hit_csv: includeHits.map((hit) => hit.keyword).join(','),
      match_score: matchScore,
      ai_score: 0,
      recomendacion: recommendation,
      estado: 'nuevo',
      vendedor: '',
      resultado: '',
      raw_payload_string: sqlText(JSON.stringify(rawPayload)),
      source_sql: sqlText('ocds'),
      external_id_sql: sqlText(candidate.external_id),
      ocid_or_nic_sql: sqlText(candidate.ocid_or_nic),
      process_code_sql: sqlText(processCode),
      titulo_sql: sqlText(title),
      entidad_sql: sqlText(entity),
      tipo_sql: sqlText(processType),
      fecha_publicacion_sql: sqlText(publishedAt),
      fecha_limite_sql: sqlText(deadlineAt),
      monto_ref_sql: sqlNumber(amount),
      moneda_sql: sqlText(currency || 'USD'),
      url_sql: sqlText(candidate.url),
      invited_company_name_sql: '',
      recomendacion_sql: sqlText(recommendation),
      is_invited_match_sql: 'FALSE',
    },
  });
}

return out;`;

const ncoNormalizeListCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const fallbackEnabled = String($env.NCO_FALLBACK_ENABLED || 'true').toLowerCase() !== 'false';
const fallbackLimit = Number($env.NCO_FALLBACK_LIMIT || 40);
const fallbackRecentHours = Number($env.NCO_FALLBACK_RECENT_HOURS || 168);
const publicationLookbackDays = Math.max(0, Math.min(Number($env.PROCESS_PUBLICATION_LOOKBACK_DAYS ?? 0), 30));
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');

function normalizeText(value) {
  return String(value ?? '').toLowerCase();
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = String(hit.keyword || '').trim().toLowerCase();
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword,
      family: hit.family || null,
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(deduped.values());
}

function parseDetailUrl(anchorHtml) {
  const match = String(anchorHtml || '').match(/href=([^\\s>]+)/i);
  if (!match?.[1]) {
    return '';
  }

  const relativeUrl = match[1].replace(/^['"]|['"]$/g, '');
  if (/^https?:\\/\\//i.test(relativeUrl)) {
    return relativeUrl;
  }

  return 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/' +
    relativeUrl
      .replace(/^\\.\\.\\/NCO\\//i, '')
      .replace(/^\\.\\.\\//, '');
}

function parseDateScore(value) {
  const timestamp = Date.parse(String(value || ''));
  return Number.isFinite(timestamp) ? timestamp : 0;
}

function localDayStart(value) {
  const parsed = new Date(String(value || ''));
  if (Number.isNaN(parsed.getTime())) {
    return 0;
  }

  return new Date(parsed.getFullYear(), parsed.getMonth(), parsed.getDate()).getTime();
}

const publicationCutoff = new Date();
publicationCutoff.setHours(0, 0, 0, 0);
publicationCutoff.setDate(publicationCutoff.getDate() - publicationLookbackDays);
const publicationCutoffTimestamp = publicationCutoff.getTime();

function isOnOrAfterPublicationCutoff(value) {
  const timestamp = localDayStart(value);
  return timestamp !== 0 && timestamp >= publicationCutoffTimestamp;
}

function isRecentEnough(value) {
  const timestamp = parseDateScore(value);
  if (!timestamp) {
    return false;
  }

  return timestamp >= Date.now() - fallbackRecentHours * 60 * 60 * 1000;
}

function typeScore(processType) {
  return /infima|infimas|necesidad/i.test(String(processType || '')) ? 20 : 15;
}

let parsedPayload = $json;

if (typeof $json?.data === 'string') {
  try {
    parsedPayload = JSON.parse($json.data);
  } catch {
    parsedPayload = { data: [] };
  }
}

if (typeof parsedPayload === 'string') {
  try {
    parsedPayload = JSON.parse(parsedPayload);
  } catch {
    parsedPayload = { data: [] };
  }
}

const rows = Array.isArray(parsedPayload?.data) ? parsedPayload.data : [];
const matchedCandidates = [];
const fallbackCandidates = [];

for (const row of rows) {
  const detailUrl = parseDetailUrl(row.url);
  if (!detailUrl) {
    continue;
  }

  if (!isOnOrAfterPublicationCutoff(row.fecha_publicacion)) {
    continue;
  }

  const title = String(row.objeto_contratacion || 'Sin titulo').trim();
  const entity = String(row.razon_social || 'Sin entidad').trim();
  const processType = String(row.tipo_necesidad || 'Necesidad').trim();
  const haystack = normalizeText(
    [
      title,
      entity,
      processType,
      row.provincia,
      row.canton,
      row.codigo_contratacion,
      row.contacto,
    ].join(' '),
  );

  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())),
  );
  const includeWeight = includeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const excludeWeight = excludeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const taxonomyScore = Math.max(0, Math.min(60, Math.round(includeWeight * 25) - Math.round(excludeWeight * 25)));
  const heuristicScore = includeHits.length > 0 ? 15 : 0;
  const matchScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + heuristicScore));
  const baseCandidate = {
    json: {
      source: 'nco',
      external_id: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      ocid_or_nic: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      process_code: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      titulo: title,
      entidad: entity,
      tipo: processType,
      fecha_publicacion: String(row.fecha_publicacion || '').trim(),
      fecha_limite: String(row.fecha_limite_propuesta || '').trim(),
      monto_ref: null,
      moneda: 'USD',
      url: detailUrl,
      initial_keywords: includeHits.map((hit) => hit.keyword),
      candidate_payload: row,
      preview_score: matchScore,
      is_fallback: false,
      fallback_reason: '',
    },
  };

  if (matchScore >= threshold) {
    matchedCandidates.push(baseCandidate);
    continue;
  }

  if (!fallbackEnabled || excludeHits.length > 0 || !isRecentEnough(row.fecha_publicacion)) {
    continue;
  }

  fallbackCandidates.push({
    json: {
      ...baseCandidate.json,
      initial_keywords: [],
      preview_score: Math.max(typeScore(processType), 35),
      is_fallback: true,
      fallback_reason: 'recent_nco',
    },
  });
}

const selectedCandidates = matchedCandidates.length > 0
  ? matchedCandidates
  : fallbackCandidates.slice(0, fallbackLimit);

return selectedCandidates
  .sort((left, right) => parseDateScore(right.json.fecha_publicacion) - parseDateScore(left.json.fecha_publicacion))
  .slice(0, matchedCandidates.length > 0 ? 180 : fallbackLimit);`;

const ncoScoreCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const fallbackScore = Number($env.NCO_FALLBACK_SCORE || 35);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');

function sqlText(value) {
  return String(value ?? '').replace(/'/g, "''");
}

function sqlNumber(value) {
  if (value === null || value === undefined || value === '') {
    return 'NULL';
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? String(parsed) : 'NULL';
}

function normalizeText(value) {
  return String(value ?? '').toLowerCase();
}

function stripHtml(value) {
  return String(value ?? '')
    .replace(/<script[\\s\\S]*?<\\/script>/gi, ' ')
    .replace(/<style[\\s\\S]*?<\\/style>/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/&nbsp;/gi, ' ')
    .replace(/\\s+/g, ' ')
    .trim();
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = String(hit.keyword || '').trim().toLowerCase();
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword,
      family: hit.family || null,
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(deduped.values());
}

function typeScore(processType) {
  return /infima|infimas|necesidad/i.test(String(processType || '')) ? 20 : 15;
}

const out = [];
const inputs = $input.all();
for (let index = 0; index < inputs.length; index++) {
  const html = String(inputs[index].json?.body || inputs[index].json || '');
  const candidate = $item(index).$node['Normalize NCO List'].json;
  const text = stripHtml(html);
  const textLower = text.toLowerCase();
  const haystack = normalizeText(
    [
      candidate.titulo,
      candidate.entidad,
      candidate.tipo,
      candidate.ocid_or_nic,
      text,
    ].join(' '),
  );

  const seededHits = Array.isArray(candidate.initial_keywords)
    ? candidate.initial_keywords.map((keyword) => ({ keyword, weight: 1 }))
    : [];
  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())).concat(seededHits),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(String(rule.keyword || '').toLowerCase())),
  );
  const includeWeight = includeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const excludeWeight = excludeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  const taxonomyScore = Math.max(0, Math.min(60, Math.round(includeWeight * 25) - Math.round(excludeWeight * 25)));
  const heuristicScore = includeHits.length > 0 ? 15 : 0;
  const matchScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(candidate.tipo) + heuristicScore));
  const keepFallback = Boolean(candidate.is_fallback) && excludeHits.length === 0;
  const finalScore = keepFallback
    ? Math.max(Number(candidate.preview_score || 0), matchScore, fallbackScore)
    : matchScore;

  if (!keepFallback && matchScore < threshold) {
    continue;
  }

  const recommendation = keepFallback && includeHits.length === 0 ? 'observacion' : 'revisar';
  const rawPayload = {
    source: 'nco',
    candidate: candidate.candidate_payload,
    detail_url: candidate.url,
    detail_text_excerpt: text.slice(0, 5000),
    fallback_used: keepFallback,
    fallback_reason: candidate.fallback_reason || null,
  };

  out.push({
    json: {
      source: 'nco',
      external_id: candidate.external_id,
      ocid_or_nic: candidate.ocid_or_nic,
      process_code: candidate.process_code || candidate.ocid_or_nic,
      titulo: candidate.titulo,
      entidad: candidate.entidad,
      tipo: candidate.tipo,
      fecha_publicacion: candidate.fecha_publicacion,
      fecha_limite: candidate.fecha_limite,
      monto_ref: candidate.monto_ref,
      moneda: candidate.moneda || 'USD',
      url: candidate.url,
      invited_company_name: null,
      is_invited_match: false,
      keywords_hit: includeHits.map((hit) => hit.keyword),
      keywords_hit_csv: includeHits.map((hit) => hit.keyword).join(','),
      match_score: finalScore,
      ai_score: 0,
      recomendacion: recommendation,
      estado: 'nuevo',
      vendedor: '',
      resultado: '',
      raw_payload_string: sqlText(JSON.stringify(rawPayload)),
      source_sql: sqlText('nco'),
      external_id_sql: sqlText(candidate.external_id),
      ocid_or_nic_sql: sqlText(candidate.ocid_or_nic),
      process_code_sql: sqlText(candidate.process_code || candidate.ocid_or_nic),
      titulo_sql: sqlText(candidate.titulo),
      entidad_sql: sqlText(candidate.entidad),
      tipo_sql: sqlText(candidate.tipo),
      fecha_publicacion_sql: sqlText(candidate.fecha_publicacion),
      fecha_limite_sql: sqlText(candidate.fecha_limite),
      monto_ref_sql: sqlNumber(candidate.monto_ref),
      moneda_sql: sqlText(candidate.moneda || 'USD'),
      url_sql: sqlText(candidate.url),
      invited_company_name_sql: '',
      recomendacion_sql: sqlText(recommendation),
      is_invited_match_sql: 'FALSE',
    },
  });
}

return out;`;

function workflowNode({
  id,
  name,
  type,
  typeVersion,
  position,
  parameters,
  credentials,
  onError,
}) {
  return {
    parameters,
    id,
    name,
    type,
    typeVersion,
    position,
    ...(onError ? { onError } : {}),
    ...(credentials ? { credentials } : {}),
  };
}

const ocdsWorkflow = {
  name: '01 SERCOP OCDS Poller',
  nodes: [
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d00',
      name: 'Manual Trigger',
      type: 'n8n-nodes-base.manualTrigger',
      typeVersion: 1,
      position: [260, 160],
      parameters: {},
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d01',
      name: 'Schedule Trigger',
      type: 'n8n-nodes-base.scheduleTrigger',
      typeVersion: 1.2,
      position: [260, 320],
      parameters: {
        rule: {
          interval: [
            {
              field: 'minutes',
              minutesInterval: 30,
            },
          ],
        },
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d02',
      name: 'Load Keyword Rules',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [520, 320],
      parameters: {
        operation: 'executeQuery',
        query:
          "SELECT rule_type, scope, keyword, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'ocds') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d03',
      name: 'Seed Search Terms',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [780, 320],
      parameters: {
        jsCode: ocdsSeedSearchTermsCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d04',
      name: 'Prepare Public Search Pages',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1040, 320],
      parameters: {
        jsCode: ocdsPreparePublicSearchPagesCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d05',
      name: 'Fetch OCDS Public Search',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1300, 320],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php',
        sendHeaders: true,
        specifyHeaders: 'keypair',
        headerParameters: {
          parameters: [
            {
              name: 'Accept',
              value: 'text/html',
            },
          ],
        },
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: '__class', value: 'SolicitudCompra' },
            { name: '__action', value: 'buscarProcesoxEntidad' },
            { name: 'txtPalabrasClaves', value: '={{$json.searchQuery}}' },
            { name: 'txtEntidadContratante', value: '' },
            { name: 'cmbEntidad', value: '' },
            { name: 'txtCodigoTipoCompra', value: '' },
            { name: 'txtCodigoProceso', value: '' },
            { name: 'f_inicio', value: '={{$json.dateFrom}}' },
            { name: 'f_fin', value: '={{$json.dateTo}}' },
            { name: 'image', value: '' },
            { name: 'captccc2', value: '2' },
            { name: 'paginaActual', value: '={{String($json.offset || 0)}}' },
            { name: 'estado', value: '' },
            { name: 'trx', value: '' },
          ],
        },
        options: {
          timeout: 20000,
          response: {
            response: {
              fullResponse: true,
              responseFormat: 'text',
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d06',
      name: 'Normalize Search Results',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1560, 320],
      parameters: {
        jsCode: ocdsNormalizeSearchResultsCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d07',
      name: 'Fetch Process Dates',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1820, 320],
      parameters: {
        url: "={{ 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/ProcesoContratacion/tab.php?tab=2&id=' + encodeURIComponent($json.detail_internal_id) }}",
        options: {
          timeout: 20000,
          response: {
            response: {
              responseFormat: 'text',
              outputPropertyName: 'data',
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d11',
      name: 'Merge Process Dates',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2080, 320],
      parameters: {
        jsCode: ocdsEnrichDatesCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d12',
      name: 'Score Filter Prepare',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2340, 320],
      parameters: {
        jsCode: ocdsScoreCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d08',
      name: 'Upsert Opportunity',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [2600, 320],
      parameters: {
        operation: 'executeQuery',
        query: upsertOpportunityQuery,
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d09',
      name: 'Filter Email Candidates',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2860, 320],
      parameters: {
        jsCode: filterEmailCandidatesCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d10',
      name: 'Email Responsible',
      type: 'n8n-nodes-base.emailSend',
      typeVersion: 2.1,
      position: [3120, 320],
      parameters: {
        resource: 'email',
        operation: 'send',
        fromEmail: '={{$env.SMTP_FROM}}',
        toEmail: '={{$env.RESPONSIBLE_EMAIL}}',
        subject: '=SERCOP/OCDS {{ $json.tipo }} :: {{ $json.titulo }}',
        emailFormat: 'html',
        html: '= <p><strong>Nuevo proceso OCDS confirmado para HDM</strong></p><p><strong>Titulo:</strong> {{$json.titulo}}</p><p><strong>Entidad:</strong> {{$json.entidad}}</p><p><strong>Tipo:</strong> {{$json.tipo}}</p><p><strong>Score:</strong> {{$json.match_score}}</p><p><strong>Fecha limite:</strong> {{$json.fecha_limite || "No visible"}}</p><p><strong>URL:</strong> <a href="{{$json.url}}">{{$json.url}}</a></p><p><strong>Keywords:</strong> {{ Array.isArray($json.keywords_hit) ? $json.keywords_hit.join(", ") : $json.keywords_hit }}</p>',
      },
      credentials: smtpCredential,
    }),
  ],
  connections: {
    'Manual Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Schedule Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Load Keyword Rules': {
      main: [[{ node: 'Seed Search Terms', type: 'main', index: 0 }]],
    },
    'Seed Search Terms': {
      main: [[{ node: 'Prepare Public Search Pages', type: 'main', index: 0 }]],
    },
    'Prepare Public Search Pages': {
      main: [[{ node: 'Fetch OCDS Public Search', type: 'main', index: 0 }]],
    },
    'Fetch OCDS Public Search': {
      main: [[{ node: 'Normalize Search Results', type: 'main', index: 0 }]],
    },
    'Normalize Search Results': {
      main: [[{ node: 'Fetch Process Dates', type: 'main', index: 0 }]],
    },
    'Fetch Process Dates': {
      main: [[{ node: 'Merge Process Dates', type: 'main', index: 0 }]],
    },
    'Merge Process Dates': {
      main: [[{ node: 'Score Filter Prepare', type: 'main', index: 0 }]],
    },
    'Score Filter Prepare': {
      main: [[{ node: 'Upsert Opportunity', type: 'main', index: 0 }]],
    },
    'Upsert Opportunity': {
      main: [[{ node: 'Filter Email Candidates', type: 'main', index: 0 }]],
    },
    'Filter Email Candidates': {
      main: [[{ node: 'Email Responsible', type: 'main', index: 0 }]],
    },
  },
  pinData: {},
  settings: baseSettings,
  versionId: '1e1c29e4-d4d8-4b8c-b28d-5c2b2614a101',
  meta: baseMeta,
  active: true,
  tags: [],
  id: '1001',
};

const ncoWorkflow = {
  name: '02 SERCOP NCO Poller',
  nodes: [
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d00',
      name: 'Manual Trigger',
      type: 'n8n-nodes-base.manualTrigger',
      typeVersion: 1,
      position: [260, 160],
      parameters: {},
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d01',
      name: 'Schedule Trigger',
      type: 'n8n-nodes-base.scheduleTrigger',
      typeVersion: 1.2,
      position: [260, 320],
      parameters: {
        rule: {
          interval: [
            {
              field: 'minutes',
              minutesInterval: 30,
            },
          ],
        },
      },
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d02',
      name: 'Load Keyword Rules',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [520, 320],
      parameters: {
        operation: 'executeQuery',
        query:
          "SELECT rule_type, scope, keyword, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'nco') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d03',
      name: 'Fetch NCO List',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [780, 320],
      parameters: {
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1&draw=1&start=0&length=300',
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d04',
      name: 'Normalize NCO List',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1040, 320],
      parameters: {
        jsCode: ncoNormalizeListCode,
      },
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d05',
      name: 'Fetch NCO Detail',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1300, 320],
      parameters: {
        url: '={{$json.url}}',
        responseFormat: 'string',
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d06',
      name: 'Score Filter Prepare',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1560, 320],
      parameters: {
        jsCode: ncoScoreCode,
      },
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d07',
      name: 'Upsert Opportunity',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [1820, 320],
      parameters: {
        operation: 'executeQuery',
        query: upsertOpportunityQuery,
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d08',
      name: 'Filter Email Candidates',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2080, 320],
      parameters: {
        jsCode: filterEmailCandidatesCode,
      },
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d09',
      name: 'Email Responsible',
      type: 'n8n-nodes-base.emailSend',
      typeVersion: 2.1,
      position: [2340, 320],
      parameters: {
        resource: 'email',
        operation: 'send',
        fromEmail: '={{$env.SMTP_FROM}}',
        toEmail: '={{$env.RESPONSIBLE_EMAIL}}',
        subject: '=SERCOP/NCO {{ $json.tipo }} :: {{ $json.titulo }}',
        emailFormat: 'html',
        html: '= <p><strong>Nueva necesidad NCO confirmada para HDM</strong></p><p><strong>Titulo:</strong> {{$json.titulo}}</p><p><strong>Entidad:</strong> {{$json.entidad}}</p><p><strong>Tipo:</strong> {{$json.tipo}}</p><p><strong>Score:</strong> {{$json.match_score}}</p><p><strong>Fecha limite:</strong> {{$json.fecha_limite || "No visible"}}</p><p><strong>URL:</strong> <a href="{{$json.url}}">{{$json.url}}</a></p><p><strong>Keywords:</strong> {{ Array.isArray($json.keywords_hit) ? $json.keywords_hit.join(", ") : $json.keywords_hit }}</p>',
      },
      credentials: smtpCredential,
    }),
  ],
  connections: {
    'Manual Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Schedule Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Load Keyword Rules': {
      main: [[{ node: 'Fetch NCO List', type: 'main', index: 0 }]],
    },
    'Fetch NCO List': {
      main: [[{ node: 'Normalize NCO List', type: 'main', index: 0 }]],
    },
    'Normalize NCO List': {
      main: [[{ node: 'Fetch NCO Detail', type: 'main', index: 0 }]],
    },
    'Fetch NCO Detail': {
      main: [[{ node: 'Score Filter Prepare', type: 'main', index: 0 }]],
    },
    'Score Filter Prepare': {
      main: [[{ node: 'Upsert Opportunity', type: 'main', index: 0 }]],
    },
    'Upsert Opportunity': {
      main: [[{ node: 'Filter Email Candidates', type: 'main', index: 0 }]],
    },
    'Filter Email Candidates': {
      main: [[{ node: 'Email Responsible', type: 'main', index: 0 }]],
    },
  },
  pinData: {},
  settings: baseSettings,
  versionId: '1e1c29e4-d4d8-4b8c-b28d-5c2b2614a102',
  meta: baseMeta,
  active: true,
  tags: [],
  id: '1002',
};

for (const [name, workflow] of [
  ['01_sercop_ocds_poller.json', ocdsWorkflow],
  ['02_sercop_nco_poller.json', ncoWorkflow],
]) {
  writeFileSync(join(workflowsDir, name), `${JSON.stringify(workflow, null, 2)}\n`, 'utf8');
}

console.log('Workflows regenerados:', '01_sercop_ocds_poller.json', '02_sercop_nco_poller.json');
