import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = dirname(__dirname);
const workflowsDir = join(root, 'workflows');
const chemistryPolicy = JSON.parse(readFileSync(join(root, 'config', 'chemistry-policy.json'), 'utf8'));

mkdirSync(workflowsDir, { recursive: true });

function jsRegexArray(patterns) {
  return patterns.map((pattern) => `new RegExp(${JSON.stringify(pattern)}, 'i')`).join(',\n    ');
}

function jsTypeScoreRules(rules) {
  return rules.map((rule) => `{ pattern: new RegExp(${JSON.stringify(rule.pattern)}, 'i'), score: ${rule.score} }`).join(',\n    ');
}

const textNormalizationCode = String.raw`
function normalizeText(value) {
  return String(value ?? '')
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLowerCase()
    .replace(/\s+/g, ' ')
    .trim();
}
`;

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
  raw_payload,
  process_category,
  capture_scope,
  is_chemistry_candidate,
  classification_payload
) VALUES (
  '{{$json.source_sql}}',
  '{{$json.external_id_sql}}',
  '{{$json.ocid_or_nic_sql}}',
  '{{$json.process_code_sql}}',
  '{{$json.titulo_sql}}',
  '{{$json.entidad_sql}}',
  '{{$json.tipo_sql}}',
  NULLIF('{{$json.fecha_publicacion_sql}}', '')::timestamptz,
  NULLIF('{{$json.fecha_limite_sql}}', '')::timestamptz,
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
  '{{$json.raw_payload_string}}'::jsonb,
  '{{$json.process_category_sql}}',
  '{{$json.capture_scope_sql}}',
  {{$json.is_chemistry_candidate_sql}},
  '{{$json.classification_payload_string}}'::jsonb
)
ON CONFLICT (source, external_id)
DO UPDATE SET
  process_code = COALESCE(NULLIF(EXCLUDED.process_code, ''), opportunities.process_code),
  ocid_or_nic = CASE
    WHEN EXCLUDED.ocid_or_nic ILIKE 'ocds-%'
      AND (
        opportunities.ocid_or_nic IS NULL
        OR opportunities.ocid_or_nic = ''
        OR opportunities.ocid_or_nic = opportunities.process_code
        OR opportunities.ocid_or_nic NOT ILIKE 'ocds-%'
      )
      THEN EXCLUDED.ocid_or_nic
    ELSE opportunities.ocid_or_nic
  END,
  titulo = EXCLUDED.titulo,
  entidad = EXCLUDED.entidad,
  tipo = EXCLUDED.tipo,
  fecha_publicacion = EXCLUDED.fecha_publicacion,
  fecha_limite = COALESCE(EXCLUDED.fecha_limite, opportunities.fecha_limite),
  monto_ref = COALESCE(EXCLUDED.monto_ref, opportunities.monto_ref),
  moneda = COALESCE(NULLIF(EXCLUDED.moneda, ''), opportunities.moneda),
  url = CASE
    WHEN COALESCE(opportunities.url, '') <> ''
      AND opportunities.url ILIKE '%compraspublicas.gob.ec%'
      AND EXCLUDED.url ILIKE '%datosabiertos.compraspublicas.gob.ec%'
      THEN opportunities.url
    ELSE EXCLUDED.url
  END,
  invited_company_name = CASE
    WHEN EXCLUDED.is_invited_match THEN EXCLUDED.invited_company_name
    ELSE opportunities.invited_company_name
  END,
  is_invited_match = opportunities.is_invited_match OR EXCLUDED.is_invited_match,
  keywords_hit = EXCLUDED.keywords_hit,
  match_score = EXCLUDED.match_score,
  recomendacion = EXCLUDED.recomendacion,
  raw_payload = EXCLUDED.raw_payload,
  process_category = EXCLUDED.process_category,
  capture_scope = EXCLUDED.capture_scope,
  is_chemistry_candidate = EXCLUDED.is_chemistry_candidate,
  classification_payload = EXCLUDED.classification_payload,
  updated_at = NOW()
RETURNING
  id,
  source,
  external_id,
  titulo,
  entidad,
  tipo,
  process_code,
  process_category,
  capture_scope,
  is_chemistry_candidate,
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
const uniqueRules = [];
const seen = new Set();
const maxSearchTerms = Number($env.OCDS_MAX_SEARCH_TERMS || 0);

function normalizeText(value) {
  return String(value ?? '')
    .normalize('NFD')
    .replace(/[\\u0300-\\u036f]/g, '')
    .toLowerCase()
    .replace(/\\s+/g, ' ')
    .trim();
}

for (const rule of rules) {
  if (String(rule.rule_type || '') !== 'include') {
    continue;
  }

  const keyword = normalizeText(rule.keyword_normalized || rule.keyword || '');
  if (!keyword || seen.has(keyword)) {
    continue;
  }

  seen.add(keyword);
  uniqueRules.push({
    json: {
      searchKeyword: keyword,
      family: rule.family || '',
      weight: Number(rule.weight || 1) || 1,
    },
  });
}

for (const seed of ['SIE-', 'RE-']) {
  const keyword = normalizeText(seed);
  if (!keyword || seen.has(keyword)) {
    continue;
  }

  seen.add(keyword);
  uniqueRules.push({
    json: {
      searchKeyword: keyword,
      family: 'modalidad',
      weight: 999,
    },
  });
}

uniqueRules.sort((left, right) => {
  const leftKeyword = normalizeText(left.json.searchKeyword || '');
  const rightKeyword = normalizeText(right.json.searchKeyword || '');
  if ((right.json.weight || 0) !== (left.json.weight || 0)) {
    return (right.json.weight || 0) - (left.json.weight || 0);
  }

  return leftKeyword.localeCompare(rightKeyword);
});

return maxSearchTerms > 0 ? uniqueRules.slice(0, maxSearchTerms) : uniqueRules;`;

const chemistrySupplyGateCode = String.raw`
const chemistryPolicy = {
  maxTaxonomyScore: ${chemistryPolicy.maxTaxonomyScore},
  keywordWeightMultiplier: ${chemistryPolicy.keywordWeightMultiplier},
  heuristicIncludeBonus: ${chemistryPolicy.heuristicIncludeBonus},
  defaultTypeScore: ${chemistryPolicy.defaultTypeScore},
  emptyTypeScore: ${chemistryPolicy.emptyTypeScore},
};

const chemistrySignals = {
  supply: [
    ${jsRegexArray(chemistryPolicy.supplySignals)},
  ],
  laboratoryEquipment: [
    ${jsRegexArray(chemistryPolicy.laboratoryEquipmentSignals)},
  ],
  context: [
    ${jsRegexArray(chemistryPolicy.chemistryContextSignals)},
  ],
  strictExclude: [
    ${jsRegexArray(chemistryPolicy.strictExcludeSignals)},
  ],
  medicalExclude: [
    ${jsRegexArray(chemistryPolicy.medicalExcludeSignals)},
  ],
  pharma: [
    ${jsRegexArray(chemistryPolicy.pharmaSignals)},
  ],
};

const typeScoreRules = [
  ${jsTypeScoreRules(chemistryPolicy.typeScoreRules)},
];

function matchesAny(patterns, haystack) {
  const value = normalizeText(haystack);
  if (!value) {
    return false;
  }

  return patterns.some((pattern) => pattern.test(value));
}

function hasStrongChemistrySignal(haystack) {
  return matchesAny(chemistrySignals.supply, haystack);
}

function hasLabContextSignal(haystack) {
  return matchesAny(chemistrySignals.context, haystack);
}

function hasNoiseSignal(haystack) {
  return matchesAny(chemistrySignals.strictExclude, haystack);
}

function hasPharmaSignal(haystack) {
  return matchesAny(chemistrySignals.pharma, haystack);
}

function hasMedicalSignal(haystack) {
  return matchesAny(chemistrySignals.medicalExclude, haystack);
}

function hasLaboratoryEquipmentSignal(haystack) {
  return matchesAny(chemistrySignals.laboratoryEquipment, haystack);
}

function typeScore(processType) {
  const normalized = normalizeText(processType);
  if (!normalized) {
    return chemistryPolicy.emptyTypeScore;
  }

  for (const rule of typeScoreRules) {
    if (rule.pattern.test(normalized)) {
      return Number(rule.score || 0) || 0;
    }
  }

  return chemistryPolicy.defaultTypeScore;
}

function computeTaxonomyScore(includeHits, excludeHits) {
  if (includeHits.length === 0 || excludeHits.length > 0) {
    return 0;
  }

  const includeWeight = includeHits.reduce((sum, hit) => sum + (Number(hit.weight || 1) || 1), 0);
  return Math.max(0, Math.min(
    chemistryPolicy.maxTaxonomyScore,
    Math.round(includeWeight * chemistryPolicy.keywordWeightMultiplier),
  ));
}
`;

const ecuadorTimestampCode = String.raw`
const ecuadorTimeZone = 'America/Guayaquil';
const ecuadorOffsetSuffix = '-05:00';

function formatDateInEcuador(date) {
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat('en-CA', {
      timeZone: ecuadorTimeZone,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: false,
    })
      .formatToParts(date)
      .filter(({ type }) => type !== 'literal')
      .map(({ type, value }) => [type, value]),
  );

  return parts.year + '-' + parts.month + '-' + parts.day + 'T' + parts.hour + ':' + parts.minute + ':' + parts.second + ecuadorOffsetSuffix;
}

function normalizeEcuadorTimestamp(value) {
  const raw = String(value || '').trim();
  if (!raw) {
    return '';
  }

  if (/^\d{4}-\d{2}-\d{2}$/.test(raw)) {
    return raw + 'T00:00:00' + ecuadorOffsetSuffix;
  }

  const isoLocalMatch = raw.match(/^(\d{4})-(\d{2})-(\d{2})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d{1,3})?)?$/);
  if (isoLocalMatch) {
    const [, year, month, day, hour = '00', minute = '00', second = '00'] = isoLocalMatch;
    return year + '-' + month + '-' + day + 'T' + hour + ':' + minute + ':' + second + ecuadorOffsetSuffix;
  }

  const latinMatch = raw.match(/^(\d{2})\/(\d{2})\/(\d{4})(?:[ T](\d{2}):(\d{2})(?::(\d{2}))?)?$/);
  if (latinMatch) {
    const [, day, month, year, hour = '00', minute = '00', second = '00'] = latinMatch;
    return year + '-' + month + '-' + day + 'T' + hour + ':' + minute + ':' + second + ecuadorOffsetSuffix;
  }

  const parsed = Date.parse(raw);
  if (!Number.isFinite(parsed)) {
    return '';
  }

  return formatDateInEcuador(new Date(parsed));
}

function extractDateKey(value) {
  const normalized = normalizeEcuadorTimestamp(value);
  return normalized ? normalized.slice(0, 10) : '';
}

function dateKeyToEpoch(value) {
  const key = String(value || '').trim();
  if (!/^\d{4}-\d{2}-\d{2}$/.test(key)) {
    return Number.NaN;
  }

  return Date.parse(key + 'T00:00:00' + ecuadorOffsetSuffix);
}

function isWithinRecentWindow(...values) {
  const configuredDays = Number($env.SERCOP_RECENT_WINDOW_DAYS || $env.POLL_RECENT_DAYS || 3);
  const recentWindowDays = Number.isFinite(configuredDays)
    ? Math.max(1, Math.min(14, Math.trunc(configuredDays)))
    : 3;
  const todayKey = new Date().toLocaleDateString('sv-SE', { timeZone: ecuadorTimeZone });
  const todayEpoch = dateKeyToEpoch(todayKey);
  const maxAgeMs = recentWindowDays * 24 * 60 * 60 * 1000;

  return values.some((value) => {
    const dateKey = extractDateKey(value);
    if (!dateKey) {
      return false;
    }

    const valueEpoch = dateKeyToEpoch(dateKey);
    if (!Number.isFinite(valueEpoch)) {
      return false;
    }

    const ageMs = todayEpoch - valueEpoch;
    return ageMs >= 0 && ageMs <= maxAgeMs;
  });
}
`;

const ocdsNormalizeSearchResultsCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');
const candidates = new Map();

function extractProcessCode(ocid) {
  return String(ocid || '').replace(/^ocds-[^-]+-/i, '').trim();
}

function asArray(payload) {
  if (Array.isArray(payload)) {
    return payload;
  }

  if (Array.isArray(payload?.data)) {
    return payload.data;
  }

  if (Array.isArray(payload?.records)) {
    return payload.records;
  }

  return [];
}

function pick(record, keys) {
  for (const key of keys) {
    const value = record?.[key];
    if (value !== undefined && value !== null && value !== '') {
      return value;
    }
  }

  return '';
}

function asNumber(value) {
  const normalized = String(value ?? '').replace(/,/g, '');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

${textNormalizationCode}
${chemistrySupplyGateCode}
${ecuadorTimestampCode}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = normalizeText(hit.keyword || '');
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

function parseDateScore(value) {
  const timestamp = Date.parse(String(value || ''));
  return Number.isFinite(timestamp) ? timestamp : 0;
}

const inputs = $input.all();
for (let index = 0; index < inputs.length; index++) {
  const payload = inputs[index].json;
  const searchKeyword = normalizeText($item(index).$node['Seed Search Terms'].json.searchKeyword || '');

  for (const row of asArray(payload)) {
    const ocid = String(pick(row, ['ocid', 'id', 'code']) || '').trim();
    if (!ocid) {
      continue;
    }

    const title = String(pick(row, ['description', 'title', 'ocid']) || 'Sin titulo').trim();
    const entity = String(pick(row, ['buyer', 'buyer_name', 'entity', 'procuringEntity']) || 'Sin entidad').trim();
    const processType = String(pick(row, ['internal_type', 'procurementMethodDetails', 'tipo']) || 'Sin tipo').trim();
    const publishedAt = String(pick(row, ['date', 'publishedDate', 'releaseDate']) || '').trim();
    const deadlineAt = String(pick(row, ['tender_tenderPeriod_endDate', 'tenderPeriod_endDate', 'deadline']) || '').trim();

    if (!isWithinRecentWindow(publishedAt, deadlineAt)) {
      continue;
    }

    const amount = asNumber(pick(row, ['amount', 'budget', 'value_amount']));
    const detailUrl = 'https://datosabiertos.compraspublicas.gob.ec/PLATAFORMA/api/record?ocid=' + encodeURIComponent(ocid);
    const processCode = extractProcessCode(ocid) || ocid;
    const haystack = normalizeText([title, entity, processType, searchKeyword].join(' '));

    const seededHits = searchKeyword ? [{ keyword: searchKeyword, weight: 1 }] : [];
    const includeHits = uniqueHits(
      includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))).concat(seededHits),
    );
    const excludeHits = uniqueHits(
      excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
    );
    const hasKeywordInclude = includeHits.length > 0;

    if (!hasStrongChemistrySignal(haystack) && !(hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack)) && !hasKeywordInclude) {
      continue;
    }

    if (!hasLabContextSignal(haystack) && !hasKeywordInclude) {
      continue;
    }

    if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack)) {
      continue;
    }

    const taxonomyScore = computeTaxonomyScore(includeHits, excludeHits);
    const heuristicScore = includeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
    const previewScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + heuristicScore));

    if (previewScore < threshold) {
      continue;
    }

    const current = candidates.get(processCode) || {
      source: 'ocds',
      external_id: processCode,
      ocid_or_nic: ocid,
      process_code: processCode,
      titulo: title,
      entidad: entity,
      tipo: processType,
      fecha_publicacion: publishedAt,
      fecha_limite: deadlineAt,
      monto_ref: amount,
      url: detailUrl,
      initial_keywords: [],
      candidate_payload: row,
      preview_score: previewScore,
    };

    if (searchKeyword && !current.initial_keywords.includes(searchKeyword)) {
      current.initial_keywords.push(searchKeyword);
    }

    current.preview_score = Math.max(current.preview_score || 0, previewScore);

    if (!current.fecha_publicacion) {
      current.fecha_publicacion = publishedAt;
    }

    if (!current.fecha_limite) {
      current.fecha_limite = deadlineAt;
    }

    if (current.monto_ref === null && amount !== null) {
      current.monto_ref = amount;
    }

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
  .slice(0, 40)
  .map((candidate) => ({ json: candidate }));`;

const ocdsScoreCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
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

function normalizeProcessCode(value) {
  const raw = String(value || '').trim();
  return raw;
}

${textNormalizationCode}
${chemistrySupplyGateCode}
${ecuadorTimestampCode}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = normalizeText(hit.keyword || '');
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
  const publishedAt = String(candidate.fecha_publicacion || '').trim();
  const deadlineAt = String(candidate.fecha_limite || '').trim();

  if (!isWithinRecentWindow(publishedAt, deadlineAt)) {
    continue;
  }

  const amount = candidate.monto_ref ?? null;
  const processCode = normalizeProcessCode(candidate.process_code || candidate.ocid_or_nic);
  const currency = String(candidate.moneda || 'USD').trim() || 'USD';
  const haystack = normalizeText([
    title,
    entity,
    processType,
    ...(Array.isArray(candidate.initial_keywords) ? candidate.initial_keywords : []),
    JSON.stringify(candidate.candidate_payload || {}),
  ].join(' '));

  const seededHits = Array.isArray(candidate.initial_keywords)
    ? candidate.initial_keywords.map((keyword) => ({ keyword, weight: 1 }))
    : [];
  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))).concat(seededHits),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const hasKeywordInclude = includeHits.length > 0;

  if (!hasStrongChemistrySignal(haystack) && !(hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack)) && !hasKeywordInclude) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !hasKeywordInclude) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack)) {
    continue;
  }

  const taxonomyScore = computeTaxonomyScore(includeHits, excludeHits);
  const heuristicScore = includeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
  const matchScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + heuristicScore));

  if (matchScore < threshold) {
    continue;
  }

  const recommendation = 'revisar';
  const rawPayload = {
    source: 'ocds',
    candidate: candidate.candidate_payload || candidate,
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
      fecha_publicacion_sql: sqlText(normalizeEcuadorTimestamp(publishedAt)),
      fecha_limite_sql: sqlText(normalizeEcuadorTimestamp(deadlineAt)),
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
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');

${textNormalizationCode}
${chemistrySupplyGateCode}
${ecuadorTimestampCode}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = normalizeText(hit.keyword || '');
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
const candidates = [];

for (const row of rows) {
  const detailUrl = parseDetailUrl(row.url);
  if (!detailUrl) {
    continue;
  }

  const title = String(row.objeto_contratacion || 'Sin titulo').trim();
  const entity = String(row.razon_social || 'Sin entidad').trim();
  const processType = String(row.tipo_necesidad || 'Necesidad').trim();
  const publishedAt = String(row.fecha_publicacion || '').trim();
  const deadlineAt = String(row.fecha_limite_propuesta || '').trim();

  if (!isWithinRecentWindow(publishedAt, deadlineAt)) {
    continue;
  }

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
    includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const hasKeywordInclude = includeHits.length > 0;

  if (!hasStrongChemistrySignal(haystack) && !(hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack)) && !hasKeywordInclude) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !hasKeywordInclude) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack)) {
    continue;
  }

  const taxonomyScore = computeTaxonomyScore(includeHits, excludeHits);
  const heuristicScore = includeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
  const matchScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + heuristicScore));

  if (matchScore < threshold) {
    continue;
  }

  candidates.push({
    json: {
      source: 'nco',
      external_id: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      ocid_or_nic: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      process_code: String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim(),
      titulo: title,
      entidad: entity,
      tipo: processType,
      fecha_publicacion: publishedAt,
      fecha_limite: deadlineAt,
      monto_ref: null,
      moneda: 'USD',
      url: detailUrl,
      initial_keywords: includeHits.map((hit) => hit.keyword),
      candidate_payload: row,
      preview_score: matchScore,
    },
  });
}

return candidates
  .sort((left, right) => parseDateScore(right.json.fecha_publicacion) - parseDateScore(left.json.fecha_publicacion))
  .slice(0, 220);`;

const ncoPublicPrepareUpsertCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
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

${textNormalizationCode}
${chemistrySupplyGateCode}
${ecuadorTimestampCode}

const nonChemicalKeywordFamilies = new Set(
  ${JSON.stringify(chemistryPolicy.nonChemicalKeywordFamilies)}
    .map((value) => normalizeText(value))
    .filter((value) => value),
);

function clampScore(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return 0;
  }

  return Math.max(0, Math.min(100, Math.round(numeric)));
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = normalizeText(hit.keyword || '');
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword: hit.keyword || keyword,
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
const out = [];
for (const row of rows) {
  const processCode = String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim();
  if (!processCode) {
    continue;
  }

  const detailUrl = parseDetailUrl(row.url);
  if (!detailUrl) {
    continue;
  }

  const title = String(row.objeto_contratacion || 'Sin titulo').trim() || processCode;
  const entity = String(row.razon_social || 'Sin entidad').trim();
  const processType = String(row.tipo_necesidad || 'Necesidad').trim();
  const publishedAt = String(row.fecha_publicacion || '').trim();
  const deadlineAt = String(row.fecha_limite_propuesta || '').trim();

  if (!isWithinRecentWindow(publishedAt, deadlineAt)) {
    continue;
  }

  const normalizedPublishedAt = normalizeEcuadorTimestamp(publishedAt);
  const normalizedDeadlineAt = normalizeEcuadorTimestamp(deadlineAt);
  const haystack = normalizeText(
    [
      title,
      entity,
      processType,
      row.provincia,
      row.canton,
      processCode,
      row.contacto,
    ].join(' '),
  );

  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );

  function isChemistryInclude(hit) {
    const keyword = normalizeText(hit.keyword || '');
    if (!keyword) {
      return false;
    }

    const family = normalizeText(hit.family || '');
    if (family && !nonChemicalKeywordFamilies.has(family)) {
      return true;
    }

    return hasStrongChemistrySignal(keyword)
      || hasLabContextSignal(keyword)
      || hasLaboratoryEquipmentSignal(keyword);
  }

  const chemicalIncludeHits = includeHits.filter(isChemistryInclude);
  const autoExcluded = hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack);
  const isChemistryCandidate = chemicalIncludeHits.length > 0
    || hasStrongChemistrySignal(haystack)
    || (hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack));
  const taxonomyScore = computeTaxonomyScore(chemicalIncludeHits, excludeHits);
  const heuristicScore = chemicalIncludeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
  const matchScoreRaw = isChemistryCandidate && !autoExcluded
    ? (taxonomyScore + typeScore(processType) + heuristicScore)
    : 0;
  const matchScore = clampScore(matchScoreRaw);
  const recommendation = matchScore >= threshold ? 'revisar' : 'descartar';

  const rawPayload = {
    source: 'nco_public',
    candidate: row,
    detail_url: detailUrl,
  };

  out.push({
    json: {
      source_sql: sqlText('nco'),
      external_id_sql: sqlText(processCode),
      ocid_or_nic_sql: sqlText(processCode),
      process_code_sql: sqlText(processCode),
      titulo_sql: sqlText(title),
      entidad_sql: sqlText(entity),
      tipo_sql: sqlText(processType),
      fecha_publicacion_sql: sqlText(normalizedPublishedAt),
      fecha_limite_sql: sqlText(normalizedDeadlineAt),
      monto_ref_sql: sqlNumber(null),
      moneda_sql: sqlText('USD'),
      url_sql: sqlText(detailUrl),
      invited_company_name_sql: '',
      is_invited_match_sql: 'FALSE',
      keywords_hit_csv: chemicalIncludeHits
        .filter((hit) => !excludeHits.some((exclude) => normalizeText(exclude.keyword) === normalizeText(hit.keyword)))
        .map((hit) => String(hit.keyword || '').trim())
        .filter(Boolean)
        .join(','),
      match_score: matchScore,
      ai_score: 0,
      recomendacion_sql: sqlText(recommendation),
      estado: 'nuevo',
      vendedor: '',
      resultado: '',
      raw_payload_string: sqlText(JSON.stringify(rawPayload)),
    },
  });
}

return out;`;

const ncoScoreCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
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

${textNormalizationCode}
${chemistrySupplyGateCode}
${ecuadorTimestampCode}

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
    const keyword = normalizeText(hit.keyword || '');
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
const inputs = $input.all();
for (let index = 0; index < inputs.length; index++) {
  const html = String(inputs[index].json?.body || inputs[index].json || '');
  const candidate = $item(index).$node['Normalize NCO List'].json;

  if (!isWithinRecentWindow(candidate.fecha_publicacion, candidate.fecha_limite)) {
    continue;
  }

  const text = stripHtml(html);
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
    includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))).concat(seededHits),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const hasKeywordInclude = includeHits.length > 0;

  if (!hasStrongChemistrySignal(haystack) && !(hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack)) && !hasKeywordInclude) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !hasKeywordInclude) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack)) {
    continue;
  }

  const taxonomyScore = computeTaxonomyScore(includeHits, excludeHits);
  const heuristicScore = includeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
  const matchScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(candidate.tipo) + heuristicScore));

  if (matchScore < threshold) {
    continue;
  }

  const recommendation = 'revisar';
  const rawPayload = {
    source: 'nco',
    candidate: candidate.candidate_payload,
    detail_url: candidate.url,
    detail_text_excerpt: text.slice(0, 5000),
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
      match_score: matchScore,
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
      fecha_publicacion_sql: sqlText(normalizeEcuadorTimestamp(candidate.fecha_publicacion)),
      fecha_limite_sql: sqlText(normalizeEcuadorTimestamp(candidate.fecha_limite)),
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
          "SELECT rule_type, scope, keyword, keyword_normalized, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'ocds') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
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
      name: 'Fetch OCDS Search',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1040, 320],
      parameters: {
        url: "={{ 'https://datosabiertos.compraspublicas.gob.ec/PLATAFORMA/api/search_ocds?year=' + encodeURIComponent($env.OCDS_YEAR || String(new Date().getFullYear())) + '&search=' + encodeURIComponent($json.searchKeyword) + '&page=1' }}",
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d05',
      name: 'Normalize Search Results',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1300, 320],
      parameters: {
        jsCode: ocdsNormalizeSearchResultsCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d07',
      name: 'Score Filter Prepare',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1560, 320],
      parameters: {
        jsCode: ocdsScoreCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d08',
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
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d09',
      name: 'Filter Email Candidates',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2080, 320],
      parameters: {
        jsCode: filterEmailCandidatesCode,
      },
    }),
    workflowNode({
      id: 'd0fc2f9f-4f1d-4c14-9f68-9d298b2f7d10',
      name: 'Email Responsible',
      type: 'n8n-nodes-base.emailSend',
      typeVersion: 2.1,
      position: [2340, 320],
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
      main: [[{ node: 'Fetch OCDS Search', type: 'main', index: 0 }]],
    },
    'Fetch OCDS Search': {
      main: [[{ node: 'Normalize Search Results', type: 'main', index: 0 }]],
    },
    'Normalize Search Results': {
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
          "SELECT rule_type, scope, keyword, keyword_normalized, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'nco') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
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
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1&draw=1&start=0&length=500',
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d10',
      name: 'Classify NCO Public',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1040, 520],
      parameters: {
        jsCode: ncoPublicPrepareUpsertCode,
      },
    }),
    workflowNode({
      id: 'e0fc2f9f-4f1d-4c14-9f68-9d298b2f7d11',
      name: 'Upsert NCO Public',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [1300, 520],
      parameters: {
        operation: 'executeQuery',
        query: upsertOpportunityQuery,
      },
      credentials: postgresCredential,
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
      main: [[{ node: 'Classify NCO Public', type: 'main', index: 0 }]],
    },
    'Classify NCO Public': {
      main: [[{ node: 'Upsert NCO Public', type: 'main', index: 0 }]],
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

const pcPublicDateWindowCode = `const ecuadorTimeZone = 'America/Guayaquil';
const configuredDays = Number($env.SERCOP_RECENT_WINDOW_DAYS || 5);
const recentWindowDays = Number.isFinite(configuredDays)
  ? Math.max(1, Math.min(14, Math.trunc(configuredDays)))
  : 5;

function toDateKey(date) {
  return date.toLocaleDateString('sv-SE', { timeZone: ecuadorTimeZone });
}

const now = new Date();
const f_fin = toDateKey(now);
const start = new Date(now.getTime() - recentWindowDays * 24 * 60 * 60 * 1000);
const f_inicio = toDateKey(start);

return [{ json: { f_inicio, f_fin } }];`;

const pcPublicParseCountCode = `function getHeader(headers, name) {
  if (!headers || typeof headers !== 'object') {
    return '';
  }

  const match = Object.keys(headers).find((key) => key.toLowerCase() === name.toLowerCase());
  if (!match) {
    return '';
  }

  const value = headers[match];
  return Array.isArray(value) ? String(value[0] ?? '') : String(value ?? '');
}

const window = ($items('Build Date Window')[0] || {}).json || {};
const rawHeader = getHeader($json.headers, 'x-json');
let raw = String(rawHeader || '').trim();
if (raw.startsWith('(') && raw.endsWith(')')) {
  raw = raw.slice(1, -1);
}

let count = 0;
try {
  const payload = raw ? JSON.parse(raw) : {};
  count = Number(payload.count || 0) || 0;
} catch {
  count = 0;
}

return [{
  json: {
    count,
    f_inicio: String(window.f_inicio || ''),
    f_fin: String(window.f_fin || ''),
  },
}];`;

const pcPublicBuildOffsetsCode = `const count = Number($json.count || 0) || 0;
const pageSize = 20;
if (count <= 0) {
  return [];
}

const pages = Math.ceil(count / pageSize);
return Array.from({ length: pages }, (_, index) => ({
  json: {
    offset: index * pageSize,
    count,
    f_inicio: String($json.f_inicio || ''),
    f_fin: String($json.f_fin || ''),
  },
}));`;

const pcPublicParseRowsCode = `function getHeader(headers, name) {
  if (!headers || typeof headers !== 'object') {
    return '';
  }

  const match = Object.keys(headers).find((key) => key.toLowerCase() === name.toLowerCase());
  if (!match) {
    return '';
  }

  const value = headers[match];
  return Array.isArray(value) ? String(value[0] ?? '') : String(value ?? '');
}

const out = [];
for (const item of $input.all()) {
  const rawHeader = getHeader(item.json?.headers, 'x-json');
  let raw = String(rawHeader || '').trim();
  if (raw.startsWith('(') && raw.endsWith(')')) {
    raw = raw.slice(1, -1);
  }

  let rows = [];
  try {
    const payload = raw ? JSON.parse(raw) : [];
    rows = Array.isArray(payload) ? payload : [];
  } catch {
    rows = [];
  }

  for (const row of rows) {
    out.push({ json: row });
  }
}

return out;`;

const pcPublicClassifyRowsCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');

${textNormalizationCode}
${chemistrySupplyGateCode}

const nonChemicalKeywordFamilies = new Set(
  ${JSON.stringify(chemistryPolicy.nonChemicalKeywordFamilies)}
    .map((value) => normalizeText(value))
    .filter((value) => value),
);

function clampScore(value) {
  const numeric = Number(value);
  if (!Number.isFinite(numeric)) {
    return 0;
  }

  return Math.max(0, Math.min(100, Math.round(numeric)));
}

function uniqueHits(rawHits) {
  const deduped = new Map();

  for (const hit of rawHits) {
    const keyword = normalizeText(hit.keyword || '');
    if (!keyword || deduped.has(keyword)) {
      continue;
    }

    deduped.set(keyword, {
      keyword: hit.keyword || keyword,
      family: hit.family || null,
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(deduped.values());
}

function mapTipo(processCode, tipoCodigo) {
  const code = String(processCode || '').trim().toUpperCase();
  if (code.startsWith('SIE-')) {
    return 'Subasta Inversa Electrónica';
  }

  if (code.startsWith('RE-')) {
    return 'Régimen Especial';
  }

  const rawTipo = String(tipoCodigo || '').trim();
  return rawTipo ? ('Tipo ' + rawTipo) : '';
}

function isChemistryInclude(hit) {
  const keyword = normalizeText(hit.keyword || '');
  if (!keyword) {
    return false;
  }

  const family = normalizeText(hit.family || '');
  if (family && !nonChemicalKeywordFamilies.has(family)) {
    return true;
  }

  return hasStrongChemistrySignal(keyword)
    || hasLabContextSignal(keyword)
    || hasLaboratoryEquipmentSignal(keyword);
}

const out = [];
for (const item of $input.all()) {
  const row = item.json || {};
  const processCode = String(row.c || '').trim();
  if (!processCode) {
    out.push(item);
    continue;
  }

  const title = String(row.d || row.c || '').trim();
  const entity = String(row.r || '').trim();
  const processType = mapTipo(processCode, row.t);
  const haystack = normalizeText([title, entity, processType, processCode].join(' '));

  const includeHits = uniqueHits(
    includeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const excludeHits = uniqueHits(
    excludeRules.filter((rule) => haystack.includes(normalizeText(rule.keyword_normalized || rule.keyword || ''))),
  );
  const chemicalIncludeHits = includeHits.filter(isChemistryInclude);

  const autoExcluded = hasMedicalSignal(haystack) || hasNoiseSignal(haystack) || hasPharmaSignal(haystack);
  const isChemistryCandidate = chemicalIncludeHits.length > 0
    || hasStrongChemistrySignal(haystack)
    || (hasLaboratoryEquipmentSignal(haystack) && hasLabContextSignal(haystack));
  const taxonomyScore = computeTaxonomyScore(chemicalIncludeHits, excludeHits);
  const heuristicScore = chemicalIncludeHits.length > 0 ? chemistryPolicy.heuristicIncludeBonus : 0;
  const matchScoreRaw = isChemistryCandidate && !autoExcluded
    ? (taxonomyScore + typeScore(processType) + heuristicScore)
    : 0;
  const matchScore = clampScore(matchScoreRaw);
  const recommendation = matchScore >= threshold ? 'revisar' : 'descartar';

  const keywordsHitCsv = chemicalIncludeHits
    .filter((hit) => !excludeHits.some((exclude) => normalizeText(exclude.keyword) === normalizeText(hit.keyword)))
    .map((hit) => String(hit.keyword || '').trim())
    .filter(Boolean)
    .join(',');

  out.push({
    json: {
      ...row,
      keywords_hit_csv: keywordsHitCsv,
      match_score: matchScore,
      recomendacion: recommendation,
    },
  });
}

return out;`;

const pcPublicPrepareUpsertCode = `function sqlText(value) {
  return String(value ?? '').replace(/'/g, "''");
}

function sqlNumber(value) {
  if (value === null || value === undefined || value === '') {
    return 'NULL';
  }

  const parsed = Number(value);
  return Number.isFinite(parsed) ? String(parsed) : 'NULL';
}

${ecuadorTimestampCode}

function normalizeProcessCode(value) {
  return String(value ?? '').trim();
}

function mapTipo(processCode, tipoCodigo) {
  const code = String(processCode || '').trim().toUpperCase();
  if (code.startsWith('SIE-')) {
    return 'Subasta Inversa Electrónica';
  }

  if (code.startsWith('RE-')) {
    return 'Régimen Especial';
  }

  const prefix = code.split('-', 1)[0] || '';
  const known = {
    FI: 'Feria Inclusiva',
    CDC: 'Contratación Directa',
    LICO: 'Licitación',
    LICB: 'Licitación Bienes y Servicios',
    COTO: 'Cotización',
    MCO: 'Menor Cuantía',
    MCBS: 'Menor Cuantía Bienes y Servicios',
    RFI: 'Régimen Especial (RFI)',
  };

  if (prefix && known[prefix]) {
    return known[prefix];
  }

  const rawTipo = String(tipoCodigo || '').trim();
  return rawTipo ? ('Tipo ' + rawTipo) : (prefix || 'Proceso');
}

const configuredRetentionDays = Number($env.SERCOP_RECENT_WINDOW_DAYS || 5);
const retentionWindowDays = Number.isFinite(configuredRetentionDays)
  ? Math.max(1, Math.min(14, Math.trunc(configuredRetentionDays)))
  : 5;
const retentionCutoffEpoch = Date.now() - retentionWindowDays * 24 * 60 * 60 * 1000;

function isWithinRetentionWindow(normalizedTimestamp) {
  const epoch = Date.parse(normalizedTimestamp);
  return Number.isFinite(epoch) && epoch >= retentionCutoffEpoch;
}

const candidates = new Map();
for (const item of $input.all()) {
  const row = item.json || {};
  const processCode = normalizeProcessCode(row.c);
  if (!processCode) {
    continue;
  }

  const token = String(row.i || '').trim().replace(/,+$/, '');
  const url = token
    ? 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/informacionProcesoContratacion2.cpe?id=' + encodeURIComponent(token)
    : '';

  candidates.set(processCode, {
    process_code: processCode,
    titulo: String(row.d || row.c || '').trim() || processCode,
    entidad: String(row.r || '').trim(),
    tipo: mapTipo(processCode, row.t),
    fecha_publicacion: String(row.f || '').trim(),
    monto_ref: row.p,
    moneda: 'USD',
    url,
    keywords_hit_csv: String(row.keywords_hit_csv || '').trim(),
    match_score: Number(row.match_score || 0) || 0,
    recomendacion: String(row.recomendacion || 'revisar').trim() || 'revisar',
    portal_row: row,
  });
}

const out = [];
for (const candidate of candidates.values()) {
  const normalizedFechaPublicacion = normalizeEcuadorTimestamp(candidate.fecha_publicacion);
  if (!normalizedFechaPublicacion || !isWithinRetentionWindow(normalizedFechaPublicacion)) {
    continue;
  }

  const rawPayload = {
    source: 'portal_pc_public',
    portal_row: candidate.portal_row,
  };

  out.push({
    json: {
      source_sql: sqlText('ocds'),
      external_id_sql: sqlText(candidate.process_code),
      ocid_or_nic_sql: sqlText(candidate.process_code),
      process_code_sql: sqlText(candidate.process_code),
      titulo_sql: sqlText(candidate.titulo),
      entidad_sql: sqlText(candidate.entidad),
      tipo_sql: sqlText(candidate.tipo),
      fecha_publicacion_sql: sqlText(normalizedFechaPublicacion),
      fecha_limite_sql: '',
      monto_ref_sql: sqlNumber(candidate.monto_ref),
      moneda_sql: sqlText(candidate.moneda || 'USD'),
      url_sql: sqlText(candidate.url),
      invited_company_name_sql: '',
      is_invited_match_sql: 'FALSE',
      keywords_hit_csv: String(candidate.keywords_hit_csv || ''),
      match_score: Number(candidate.match_score || 0) || 0,
      ai_score: 0,
      recomendacion_sql: sqlText(candidate.recomendacion || 'revisar'),
      estado: 'nuevo',
      vendedor: '',
      resultado: '',
      raw_payload_string: sqlText(JSON.stringify(rawPayload)),
    },
  });
}

return out;`;

const pcPublicWorkflow = {
  name: '07 SERCOP PC Public Poller',
  nodes: [
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d00',
      name: 'Manual Trigger',
      type: 'n8n-nodes-base.manualTrigger',
      typeVersion: 1,
      position: [260, 160],
      parameters: {},
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d01',
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
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d10',
      name: 'Load Keyword Rules',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [520, 200],
      parameters: {
        operation: 'executeQuery',
        query:
          "SELECT rule_type, scope, keyword, keyword_normalized, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'ocds') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d02',
      name: 'Build Date Window',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [520, 320],
      parameters: {
        jsCode: pcPublicDateWindowCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d03',
      name: 'Fetch PC Count',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [780, 320],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php',
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: '__class', value: 'SolicitudCompra' },
            { name: '__action', value: 'buscarProcesoxEntidadCount' },
            { name: 'txtPalabrasClaves', value: '' },
            { name: 'txtEntidadContratante', value: '' },
            { name: 'cmbEntidad', value: '' },
            { name: 'txtCodigoTipoCompra', value: '' },
            { name: 'txtCodigoProceso', value: '' },
            { name: 'f_inicio', value: '={{$json.f_inicio}}' },
            { name: 'f_fin', value: '={{$json.f_fin}}' },
            { name: 'captccc2', value: '1' },
            { name: 'paginaActual', value: '0' },
          ],
        },
        options: {
          timeout: 45000,
          response: {
            response: {
              fullResponse: true,
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d04',
      name: 'Parse Count',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1040, 320],
      parameters: {
        jsCode: pcPublicParseCountCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d05',
      name: 'Build Offsets',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1300, 320],
      parameters: {
        jsCode: pcPublicBuildOffsetsCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d06',
      name: 'Fetch PC Page',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1560, 320],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php',
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: '__class', value: 'SolicitudCompra' },
            { name: '__action', value: 'buscarProcesoxEntidad' },
            { name: 'txtPalabrasClaves', value: '' },
            { name: 'txtEntidadContratante', value: '' },
            { name: 'cmbEntidad', value: '' },
            { name: 'txtCodigoTipoCompra', value: '' },
            { name: 'txtCodigoProceso', value: '' },
            { name: 'f_inicio', value: '={{$json.f_inicio}}' },
            { name: 'f_fin', value: '={{$json.f_fin}}' },
            { name: 'captccc2', value: "={{ $json.offset === 0 ? '1' : '2' }}" },
            { name: 'paginaActual', value: '={{$json.offset}}' },
            { name: 'count', value: '={{$json.count}}' },
          ],
        },
        options: {
          timeout: 45000,
          response: {
            response: {
              fullResponse: true,
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d07',
      name: 'Parse Rows',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1820, 320],
      parameters: {
        jsCode: pcPublicParseRowsCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d11',
      name: 'Classify Rows',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2080, 320],
      parameters: {
        jsCode: pcPublicClassifyRowsCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d08',
      name: 'Prepare Upsert',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2340, 320],
      parameters: {
        jsCode: pcPublicPrepareUpsertCode,
      },
    }),
    workflowNode({
      id: 'f0fc2f9f-4f1d-4c14-9f68-9d298b2f7d09',
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
  ],
  connections: {
    'Manual Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Schedule Trigger': {
      main: [[{ node: 'Load Keyword Rules', type: 'main', index: 0 }]],
    },
    'Load Keyword Rules': {
      main: [[{ node: 'Build Date Window', type: 'main', index: 0 }]],
    },
    'Build Date Window': {
      main: [[{ node: 'Fetch PC Count', type: 'main', index: 0 }]],
    },
    'Fetch PC Count': {
      main: [[{ node: 'Parse Count', type: 'main', index: 0 }]],
    },
    'Parse Count': {
      main: [[{ node: 'Build Offsets', type: 'main', index: 0 }]],
    },
    'Build Offsets': {
      main: [[{ node: 'Fetch PC Page', type: 'main', index: 0 }]],
    },
    'Fetch PC Page': {
      main: [[{ node: 'Parse Rows', type: 'main', index: 0 }]],
    },
    'Parse Rows': {
      main: [[{ node: 'Classify Rows', type: 'main', index: 0 }]],
    },
    'Classify Rows': {
      main: [[{ node: 'Prepare Upsert', type: 'main', index: 0 }]],
    },
    'Prepare Upsert': {
      main: [[{ node: 'Upsert Opportunity', type: 'main', index: 0 }]],
    },
  },
  pinData: {},
  settings: baseSettings,
  versionId: '3ab53fa0-89c2-42ad-86bf-832ccf641bf2',
  meta: baseMeta,
  active: true,
  tags: [],
  id: '1007',
};

const masterResolveCategoryCode = `
function resolveProcessCategory(candidate) {
  const processCode = String(candidate.process_code || candidate.ocid_or_nic || '').trim().toUpperCase();
  const processType = normalizeText(candidate.tipo || '');
  const source = normalizeText(candidate.source || '');

  if (processCode.startsWith('NIC-')) {
    return 'infimas';
  }

  if (processCode.startsWith('NC-')) {
    return 'nco';
  }

  if (processCode.startsWith('SIE-')) {
    return 'sie';
  }

  if (processCode.startsWith('RE-')) {
    return 're';
  }

  if (source === 'nco') {
    if (processType.includes('infima')) {
      return 'infimas';
    }

    return 'nco';
  }

  if (processType.includes('subasta inversa')) {
    return 'sie';
  }

  if (processType.includes('regimen especial')) {
    return 're';
  }

  return 'other_public';
}
`;

const masterNormalizePcPublicCode = `\
${textNormalizationCode}
${ecuadorTimestampCode}
${masterResolveCategoryCode}

function parseAmount(rawValue) {
  const raw = String(rawValue ?? '').trim();
  if (!raw) {
    return null;
  }

  const normalized = raw
    .replace(/\\s+/g, '')
    .replace(/\\.(?=\\d{3}(?:\\D|$))/g, '')
    .replace(',', '.')
    .replace(/[^0-9.-]/g, '');
  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function inferPcType(processCode, rawTipo) {
  const normalizedCode = String(processCode || '').trim().toUpperCase();
  if (normalizedCode.startsWith('NIC-')) {
    return 'Ínfima Cuantía';
  }

  if (normalizedCode.startsWith('NC-')) {
    return 'Necesidades de Contratación';
  }

  if (normalizedCode.startsWith('SIE-')) {
    return 'Subasta Inversa Electrónica';
  }

  if (normalizedCode.startsWith('RE-')) {
    return 'Régimen Especial';
  }

  const raw = String(rawTipo || '').trim();
  return raw ? raw : 'Proceso de contratación pública';
}

const out = [];
for (const item of $input.all()) {
  const row = item.json || {};
  const processCode = String(row.c || '').trim();
  if (!processCode) {
    continue;
  }

  const publishedAt = normalizeEcuadorTimestamp(row.f);
  if (!isWithinRecentWindow(publishedAt)) {
    continue;
  }

  const tipo = inferPcType(processCode, row.t);
  out.push({
    json: {
      source: 'ocds',
      external_id: processCode,
      ocid_or_nic: processCode,
      process_code: processCode,
      titulo: String(row.d || row.c || '').trim() || processCode,
      entidad: String(row.r || '').trim(),
      tipo,
      fecha_publicacion: publishedAt,
      fecha_limite: '',
      monto_ref: parseAmount(row.p),
      moneda: 'USD',
      url: row.i
        ? 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/PC/informacionProcesoContratacion2.cpe?id=' + String(row.i).trim()
        : '',
      raw_payload: {
        source: 'portal_pc_public',
        candidate: row,
      },
      process_category: resolveProcessCategory({ source: 'ocds', process_code: processCode, tipo }),
    },
  });
}

return out;`;

const masterPcFetchBarrierCode = `return [{ json: { pc_rows_ready: $input.all().length } }];`;
const masterNcoFetchBarrierCode = `return [{ json: { nco_rows_ready: $input.all().length } }];`;

const masterNormalizeNcoPublicCode = `\
${textNormalizationCode}
${ecuadorTimestampCode}
${masterResolveCategoryCode}

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

let payload = $json;
if (typeof payload?.data === 'string') {
  try {
    payload = JSON.parse(payload.data);
  } catch {
    payload = { data: [] };
  }
}

if (typeof payload === 'string') {
  try {
    payload = JSON.parse(payload);
  } catch {
    payload = { data: [] };
  }
}

const rows = Array.isArray(payload?.data) ? payload.data : [];
const out = [];
for (const row of rows) {
  const processCode = String(row.codigo_contratacion || row.tcom_necesidad_contratacion_id || '').trim();
  if (!processCode) {
    continue;
  }

  const publishedAt = normalizeEcuadorTimestamp(row.fecha_publicacion);
  const deadlineAt = normalizeEcuadorTimestamp(row.fecha_limite_propuesta);
  if (!isWithinRecentWindow(publishedAt, deadlineAt)) {
    continue;
  }

  const tipo = String(row.tipo_necesidad || '').trim() || (processCode.toUpperCase().startsWith('NIC-') ? 'Ínfima Cuantía' : 'Necesidades de Contratación');
  out.push({
    json: {
      source: 'nco',
      external_id: processCode,
      ocid_or_nic: processCode,
      process_code: processCode,
      titulo: String(row.objeto_contratacion || processCode).trim() || processCode,
      entidad: String(row.razon_social || '').trim(),
      tipo,
      fecha_publicacion: publishedAt,
      fecha_limite: deadlineAt,
      monto_ref: null,
      moneda: 'USD',
      url: parseDetailUrl(row.url),
      raw_payload: {
        source: 'nco_public',
        candidate: row,
      },
      process_category: resolveProcessCategory({ source: 'nco', process_code: processCode, tipo }),
    },
  });
}

return out.length > 0 ? out : [{ json: { __empty_nco: true } }];`;

const masterNormalizePublicUniverseCode = `\
const pcRows = $items('Normalize PC Public').map((item) => item.json);
const ncoRows = $items('Normalize NCO Public').map((item) => item.json);
const ncRows = $items('Normalize NC Public').map((item) => item.json);
const seen = new Set();
const out = [];

for (const candidate of [...pcRows, ...ncoRows, ...ncRows]) {
  const source = String(candidate.source || '').trim().toLowerCase();
  const processCode = String(candidate.process_code || candidate.ocid_or_nic || '').trim().toUpperCase();
  if (!source || !processCode) {
    continue;
  }

  const key = source + '::' + processCode;
  if (seen.has(key)) {
    continue;
  }

  seen.add(key);
  out.push({ json: candidate });
}

return out;`;

const masterFetchAllPublicCode = `return $items('Normalize Public Universe').map((item) => ({ json: { ...item.json, capture_scope_candidate: 'all_public' } }));`;

function buildScopedSubsetCode(category) {
  return `\
${textNormalizationCode}
${masterResolveCategoryCode}

const matches = $items('Normalize Public Universe')
  .map((item) => item.json)
  .filter((candidate) => resolveProcessCategory(candidate) === '${category}')
  .map((candidate) => ({ json: { ...candidate, capture_scope_candidate: '${category}' } }));

if (matches.length > 0) {
  return matches;
}

return [{ json: { __empty_scope: '${category}' } }];`;
}

const masterFetchInfimasCode = buildScopedSubsetCode('infimas');
const masterFetchNcoCode = buildScopedSubsetCode('nco');
const masterFetchSieCode = buildScopedSubsetCode('sie');
const masterFetchReCode = buildScopedSubsetCode('re');

const masterClassifyAndDedupCode = `\
const threshold = Number($env.MATCH_THRESHOLD || 60);

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

${textNormalizationCode}
${masterResolveCategoryCode}

const nonChemicalFamilies = new Set(
  ${JSON.stringify(chemistryPolicy.nonChemicalKeywordFamilies)}
    .map((value) => normalizeText(value))
    .filter((value) => value),
);

const strictExcludeSignals = [
  ${jsRegexArray(chemistryPolicy.strictExcludeSignals)},
];

const medicalExcludeSignals = [
  ${jsRegexArray(chemistryPolicy.medicalExcludeSignals)},
];

const pharmaSignals = [
  ${jsRegexArray(chemistryPolicy.pharmaSignals)},
];

function matchesAny(patterns, haystack) {
  const normalized = normalizeText(haystack);
  if (!normalized) {
    return false;
  }

  return patterns.some((pattern) => pattern.test(normalized));
}

function uniqueRuleHits(rawHits) {
  const seen = new Map();
  for (const hit of rawHits) {
    const normalizedKeyword = normalizeText(hit.keywordOriginal || hit.keywordNormalized || '');
    if (!normalizedKeyword || seen.has(normalizedKeyword)) {
      continue;
    }

    seen.set(normalizedKeyword, {
      keywordOriginal: hit.keywordOriginal || hit.keywordNormalized || normalizedKeyword,
      keywordNormalized: normalizedKeyword,
      family: normalizeText(hit.family || ''),
      weight: Number(hit.weight || 1) || 1,
    });
  }

  return Array.from(seen.values());
}

function evaluateChemistry(candidate, includeRules, excludeRules) {
  const rawPayloadText = JSON.stringify(candidate.raw_payload || {});
  const primaryHaystack = normalizeText([
    candidate.titulo,
    candidate.entidad,
    candidate.tipo,
    candidate.process_code,
  ].join(' '));
  const haystack = normalizeText([primaryHaystack, rawPayloadText].join(' '));

  const includeHits = uniqueRuleHits(
    includeRules.filter((rule) => rule.keywordNormalized && haystack.includes(rule.keywordNormalized)),
  );
  const chemicalIncludeHits = includeHits.filter((rule) => !nonChemicalFamilies.has(rule.family));
  const allExcludeHits = uniqueRuleHits(
    excludeRules.filter((rule) => {
      if (!rule.keywordNormalized) {
        return false;
      }

      const excludeHaystack = normalizeText(rule.family) === 'medico' ? primaryHaystack : haystack;
      return excludeHaystack.includes(rule.keywordNormalized);
    }),
  );
  const suppressibleGenericExcludes = new Set([
    'adquisicion de equipos',
    'equipos de laboratorio',
    'instrumentos de medicion',
  ]);
  const excludeHits = allExcludeHits.filter((rule) => {
    if (chemicalIncludeHits.length === 0) {
      return true;
    }

    return !(normalizeText(rule.family) === 'ruido' && suppressibleGenericExcludes.has(rule.keywordNormalized));
  });
  const suppressedExcludeHits = allExcludeHits.filter((rule) => !excludeHits.includes(rule));

  const strictExcluded = matchesAny(strictExcludeSignals, primaryHaystack);
  const medicalExcluded = matchesAny(medicalExcludeSignals, primaryHaystack);
  const pharmaExcluded = matchesAny(pharmaSignals, primaryHaystack);
  const autoExcluded = strictExcluded || medicalExcluded || pharmaExcluded;

  const includeWeight = chemicalIncludeHits.reduce((sum, rule) => sum + (Number(rule.weight || 1) || 1), 0);
  const excludeWeight = excludeHits.reduce((sum, rule) => sum + (Number(rule.weight || 1) || 1), 0);
  const baseScore = Math.max(0, Math.min(100, Math.round((includeWeight - excludeWeight) * 25)));
  const isChemistryCandidate = chemicalIncludeHits.length > 0 && excludeHits.length === 0 && !autoExcluded;
  const matchScore = isChemistryCandidate ? Math.max(threshold, baseScore) : Math.min(baseScore, 40);
  const recommendation = isChemistryCandidate ? 'revisar' : 'descartar';
  const reasons = [];

  if (chemicalIncludeHits.length > 0) {
    reasons.push('Coincide con palabra clave química: ' + chemicalIncludeHits[0].keywordOriginal + '.');
  } else {
    reasons.push('No coincide con ninguna palabra clave química activa.');
  }

  if (excludeHits.length > 0) {
    reasons.push('Coincide con palabra excluida: ' + excludeHits[0].keywordOriginal + '.');
  }

  if (suppressedExcludeHits.length > 0) {
    reasons.push('Ignora exclusión genérica por contexto químico: ' + suppressedExcludeHits[0].keywordOriginal + '.');
  }

  if (strictExcluded) {
    reasons.push('Excluido automáticamente por política: señales no químicas.');
  }

  if (medicalExcluded) {
    reasons.push('Excluido automáticamente por política: señales médicas.');
  }

  if (pharmaExcluded) {
    reasons.push('Excluido automáticamente por política: señales farmacéuticas.');
  }

  return {
    isChemistryCandidate,
    matchScore,
    recommendation,
    includeHits: chemicalIncludeHits.map((rule) => rule.keywordOriginal),
    excludeHits: excludeHits.map((rule) => rule.keywordOriginal),
    reasons,
  };
}

function completenessScore(candidate) {
  let score = 0;
  if (candidate.fecha_limite) score += 3;
  if (candidate.monto_ref !== null && candidate.monto_ref !== undefined && candidate.monto_ref !== '') score += 2;
  if (candidate.entidad) score += 1;
  if (candidate.titulo) score += 1;
  if (candidate.url) score += 1;
  return score;
}

function chooseScope(scopes, category) {
  const uniqueScopes = Array.from(new Set((scopes || []).filter(Boolean)));
  const priority = [category, 'all_public', 'infimas', 'nco', 'sie', 're'];
  for (const scope of priority) {
    if (uniqueScopes.includes(scope)) {
      return scope;
    }
  }

  return category === 'other_public' ? 'all_public' : category;
}

function modalityReason(category) {
  switch (category) {
    case 'infimas':
      return 'Clasificado como ínfima por código/tipo SERCOP.';
    case 'nco':
      return 'Clasificado como necesidad de contratación por código/tipo SERCOP.';
    case 'sie':
      return 'Clasificado como subasta inversa por código/tipo SERCOP.';
    case 're':
      return 'Clasificado como régimen especial por código/tipo SERCOP.';
    default:
      return 'Clasificado como proceso público general por código/tipo/origen.';
  }
}

const rules = $items('Load Keyword Rules').map((item) => item.json || {});
const includeRules = rules
  .filter((rule) => String(rule.rule_type || '').toLowerCase() === 'include')
  .map((rule) => ({
    keywordOriginal: String(rule.keyword || rule.keyword_normalized || '').trim(),
    keywordNormalized: normalizeText(rule.keyword_normalized || rule.keyword || ''),
    family: String(rule.family || '').trim(),
    weight: Number(rule.weight || 1) || 1,
  }))
  .filter((rule) => rule.keywordNormalized);
const excludeRules = rules
  .filter((rule) => String(rule.rule_type || '').toLowerCase() === 'exclude')
  .map((rule) => ({
    keywordOriginal: String(rule.keyword || rule.keyword_normalized || '').trim(),
    keywordNormalized: normalizeText(rule.keyword_normalized || rule.keyword || ''),
    family: String(rule.family || '').trim(),
    weight: Number(rule.weight || 1) || 1,
  }))
  .filter((rule) => rule.keywordNormalized);

const staged = [
  ...$items('Fetch All Public').map((item) => item.json),
  ...$items('Fetch Infimas').map((item) => item.json),
  ...$items('Fetch NCO').map((item) => item.json),
  ...$items('Fetch SIE').map((item) => item.json),
  ...$items('Fetch RE').map((item) => item.json),
];

const deduped = new Map();
for (const candidate of staged) {
  const source = String(candidate.source || '').trim().toLowerCase();
  const processCode = String(candidate.process_code || candidate.ocid_or_nic || '').trim();
  if (!source || !processCode) {
    continue;
  }

  const key = source + '::' + processCode.toUpperCase();
  const candidateScore = completenessScore(candidate);
  const current = deduped.get(key);
  if (!current) {
    deduped.set(key, {
      candidate,
      score: candidateScore,
      scopes: new Set([String(candidate.capture_scope_candidate || '').trim()]),
      payloadSources: new Set([candidate.raw_payload?.source || source]),
    });
    continue;
  }

  if (candidateScore > current.score) {
    current.candidate = candidate;
    current.score = candidateScore;
  }

  current.scopes.add(String(candidate.capture_scope_candidate || '').trim());
  current.payloadSources.add(candidate.raw_payload?.source || source);
}

const out = [];
for (const entry of deduped.values()) {
  const candidate = entry.candidate;
  const processCategory = resolveProcessCategory(candidate);
  const captureScope = chooseScope(Array.from(entry.scopes), processCategory);
  const chemistry = evaluateChemistry(candidate, includeRules, excludeRules);
  const reasons = [
    modalityReason(processCategory),
    'Origen de captura: ' + captureScope + '.',
    ...chemistry.reasons,
  ];
  const rawPayload = {
    ...(candidate.raw_payload || {}),
    capture_scopes: Array.from(entry.scopes).filter(Boolean),
    dedup_sources: Array.from(entry.payloadSources).filter(Boolean),
  };
  const classificationPayload = {
    processCategory: processCategory,
    captureScope: captureScope,
    isChemistryCandidate: chemistry.isChemistryCandidate,
    reasons: reasons,
    chemistry: {
      includeHits: chemistry.includeHits,
      excludeHits: chemistry.excludeHits,
      matchScore: chemistry.matchScore,
      recommendation: chemistry.recommendation,
    },
  };

  out.push({
    json: {
      source_sql: sqlText(candidate.source || ''),
      external_id_sql: sqlText(candidate.external_id || candidate.process_code || candidate.ocid_or_nic || ''),
      ocid_or_nic_sql: sqlText(candidate.ocid_or_nic || candidate.process_code || ''),
      process_code_sql: sqlText(candidate.process_code || candidate.ocid_or_nic || ''),
      titulo_sql: sqlText(candidate.titulo || candidate.process_code || ''),
      entidad_sql: sqlText(candidate.entidad || ''),
      tipo_sql: sqlText(candidate.tipo || ''),
      fecha_publicacion_sql: sqlText(candidate.fecha_publicacion || ''),
      fecha_limite_sql: sqlText(candidate.fecha_limite || ''),
      monto_ref_sql: sqlNumber(candidate.monto_ref),
      moneda_sql: sqlText(candidate.moneda || 'USD'),
      url_sql: sqlText(candidate.url || ''),
      invited_company_name_sql: '',
      is_invited_match_sql: 'FALSE',
      keywords_hit_csv: chemistry.includeHits.map((hit) => String(hit).trim()).filter(Boolean).join(','),
      match_score: chemistry.matchScore,
      ai_score: 0,
      recomendacion_sql: sqlText(chemistry.recommendation),
      estado: 'nuevo',
      vendedor: '',
      resultado: '',
      raw_payload_string: sqlText(JSON.stringify(rawPayload)),
      process_category_sql: sqlText(processCategory),
      capture_scope_sql: sqlText(captureScope),
      is_chemistry_candidate_sql: chemistry.isChemistryCandidate ? 'TRUE' : 'FALSE',
      classification_payload_string: sqlText(JSON.stringify(classificationPayload)),
    },
  });
}

return out;`;

const masterWorkflow = {
  name: '01 SERCOP Master Poller',
  nodes: [
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d00',
      name: 'Manual Trigger',
      type: 'n8n-nodes-base.manualTrigger',
      typeVersion: 1,
      position: [240, 180],
      parameters: {},
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d01',
      name: 'Schedule Trigger',
      type: 'n8n-nodes-base.scheduleTrigger',
      typeVersion: 1.2,
      position: [240, 340],
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
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d02',
      name: 'Load Keyword Rules',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [500, 260],
      parameters: {
        operation: 'executeQuery',
        query:
          "SELECT rule_type, scope, keyword, keyword_normalized, family, weight, notes FROM keyword_rules WHERE active = TRUE AND scope IN ('all', 'ocds', 'nco') ORDER BY rule_type ASC, weight DESC, keyword ASC;",
      },
      credentials: postgresCredential,
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d03',
      name: 'Build Date Window',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [760, 260],
      parameters: {
        jsCode: pcPublicDateWindowCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d04',
      name: 'Fetch PC Count',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1020, 260],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php',
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: '__class', value: 'SolicitudCompra' },
            { name: '__action', value: 'buscarProcesoxEntidadCount' },
            { name: 'txtPalabrasClaves', value: '' },
            { name: 'txtEntidadContratante', value: '' },
            { name: 'cmbEntidad', value: '' },
            { name: 'txtCodigoTipoCompra', value: '' },
            { name: 'txtCodigoProceso', value: '' },
            { name: 'f_inicio', value: '={{$json.f_inicio}}' },
            { name: 'f_fin', value: '={{$json.f_fin}}' },
            { name: 'captccc2', value: '1' },
            { name: 'paginaActual', value: '0' },
          ],
        },
        options: {
          timeout: 45000,
          response: {
            response: {
              fullResponse: true,
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d05',
      name: 'Parse PC Count',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1280, 260],
      parameters: {
        jsCode: pcPublicParseCountCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d06',
      name: 'Build PC Offsets',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [1540, 260],
      parameters: {
        jsCode: pcPublicBuildOffsetsCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d07',
      name: 'Fetch All Public Pages',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [1800, 260],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/servicio/interfazWeb.php',
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: '__class', value: 'SolicitudCompra' },
            { name: '__action', value: 'buscarProcesoxEntidad' },
            { name: 'txtPalabrasClaves', value: '' },
            { name: 'txtEntidadContratante', value: '' },
            { name: 'cmbEntidad', value: '' },
            { name: 'txtCodigoTipoCompra', value: '' },
            { name: 'txtCodigoProceso', value: '' },
            { name: 'f_inicio', value: '={{$json.f_inicio}}' },
            { name: 'f_fin', value: '={{$json.f_fin}}' },
            { name: 'captccc2', value: "={{ $json.offset === 0 ? '1' : '2' }}" },
            { name: 'paginaActual', value: '={{$json.offset}}' },
            { name: 'count', value: '={{$json.count}}' },
          ],
        },
        options: {
          timeout: 45000,
          response: {
            response: {
              fullResponse: true,
            },
          },
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d08',
      name: 'Parse All Public Rows',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2060, 260],
      parameters: {
        jsCode: pcPublicParseRowsCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d09',
      name: 'Normalize PC Public',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2320, 260],
      parameters: {
        jsCode: masterNormalizePcPublicCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d10',
      name: 'PC Fetch Barrier',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [2580, 260],
      parameters: {
        jsCode: masterPcFetchBarrierCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d11',
      name: 'Fetch NCO Public',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [2840, 260],
      parameters: {
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1&draw=1&start=0&length=500',
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d12',
      name: 'Normalize NCO Public',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [3100, 260],
      parameters: {
        jsCode: masterNormalizeNcoPublicCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d23',
      name: 'NCO Fetch Barrier',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [3360, 260],
      parameters: {
        jsCode: masterNcoFetchBarrierCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d13',
      name: 'Fetch NC Public',
      type: 'n8n-nodes-base.httpRequest',
      typeVersion: 4.2,
      position: [3620, 260],
      parameters: {
        method: 'POST',
        url: 'https://www.compraspublicas.gob.ec/ProcesoContratacion/compras/NCO/NCORetornaRegistros.cpe?lot=1',
        sendBody: true,
        contentType: 'form-urlencoded',
        specifyBody: 'keypair',
        bodyParameters: {
          parameters: [
            { name: 'sEcho', value: '1' },
            { name: 'iDisplayStart', value: '0' },
            { name: 'iDisplayLength', value: '2000' },
            { name: 'sSearch_0', value: '54089' },
            { name: 'sSearch_5', value: '384' },
            { name: 'iSortCol_0', value: '1' },
            { name: 'sSortDir_0', value: 'desc' },
          ],
        },
        options: {
          timeout: 45000,
        },
      },
      onError: 'continueRegularOutput',
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d21',
      name: 'Normalize NC Public',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [3880, 260],
      parameters: {
        jsCode: masterNormalizeNcoPublicCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d22',
      name: 'Normalize Public Universe',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4140, 260],
      parameters: {
        jsCode: masterNormalizePublicUniverseCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d14',
      name: 'Fetch All Public',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4400, 40],
      parameters: {
        jsCode: masterFetchAllPublicCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d15',
      name: 'Fetch Infimas',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4400, 200],
      parameters: {
        jsCode: masterFetchInfimasCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d16',
      name: 'Fetch NCO',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4400, 360],
      parameters: {
        jsCode: masterFetchNcoCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d17',
      name: 'Fetch SIE',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4400, 520],
      parameters: {
        jsCode: masterFetchSieCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d18',
      name: 'Fetch RE',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4400, 680],
      parameters: {
        jsCode: masterFetchReCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d19',
      name: 'Classify + Dedup',
      type: 'n8n-nodes-base.code',
      typeVersion: 2,
      position: [4660, 680],
      parameters: {
        jsCode: masterClassifyAndDedupCode,
      },
    }),
    workflowNode({
      id: 'a1fc2f9f-4f1d-4c14-9f68-9d298b2f7d20',
      name: 'Upsert Opportunity',
      type: 'n8n-nodes-base.postgres',
      typeVersion: 2.6,
      position: [4920, 680],
      parameters: {
        operation: 'executeQuery',
        query: upsertOpportunityQuery,
      },
      credentials: postgresCredential,
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
      main: [[{ node: 'Build Date Window', type: 'main', index: 0 }]],
    },
    'Build Date Window': {
      main: [[{ node: 'Fetch PC Count', type: 'main', index: 0 }]],
    },
    'Fetch PC Count': {
      main: [[{ node: 'Parse PC Count', type: 'main', index: 0 }]],
    },
    'Parse PC Count': {
      main: [[{ node: 'Build PC Offsets', type: 'main', index: 0 }]],
    },
    'Build PC Offsets': {
      main: [[{ node: 'Fetch All Public Pages', type: 'main', index: 0 }]],
    },
    'Fetch All Public Pages': {
      main: [[{ node: 'Parse All Public Rows', type: 'main', index: 0 }]],
    },
    'Parse All Public Rows': {
      main: [[{ node: 'Normalize PC Public', type: 'main', index: 0 }]],
    },
    'Normalize PC Public': {
      main: [[{ node: 'PC Fetch Barrier', type: 'main', index: 0 }]],
    },
    'PC Fetch Barrier': {
      main: [[{ node: 'Fetch NCO Public', type: 'main', index: 0 }]],
    },
    'Fetch NCO Public': {
      main: [[{ node: 'Normalize NCO Public', type: 'main', index: 0 }]],
    },
    'Normalize NCO Public': {
      main: [[{ node: 'NCO Fetch Barrier', type: 'main', index: 0 }]],
    },
    'NCO Fetch Barrier': {
      main: [[{ node: 'Fetch NC Public', type: 'main', index: 0 }]],
    },
    'Fetch NC Public': {
      main: [[{ node: 'Normalize NC Public', type: 'main', index: 0 }]],
    },
    'Normalize NC Public': {
      main: [[{ node: 'Normalize Public Universe', type: 'main', index: 0 }]],
    },
    'Normalize Public Universe': {
      main: [[{ node: 'Fetch All Public', type: 'main', index: 0 }]],
    },
    'Fetch All Public': {
      main: [[{ node: 'Fetch Infimas', type: 'main', index: 0 }]],
    },
    'Fetch Infimas': {
      main: [[{ node: 'Fetch NCO', type: 'main', index: 0 }]],
    },
    'Fetch NCO': {
      main: [[{ node: 'Fetch SIE', type: 'main', index: 0 }]],
    },
    'Fetch SIE': {
      main: [[{ node: 'Fetch RE', type: 'main', index: 0 }]],
    },
    'Fetch RE': {
      main: [[{ node: 'Classify + Dedup', type: 'main', index: 0 }]],
    },
    'Classify + Dedup': {
      main: [[{ node: 'Upsert Opportunity', type: 'main', index: 0 }]],
    },
  },
  pinData: {},
  settings: baseSettings,
  versionId: '52d4fa5c-1c54-4c1f-af0d-43f8e5c5779c',
  meta: baseMeta,
  active: true,
  tags: [],
  id: '1001',
};

for (const legacyFile of [
  '01_sercop_ocds_poller.json',
  '02_sercop_nco_poller.json',
  '07_sercop_pc_public_poller.json',
]) {
  rmSync(join(workflowsDir, legacyFile), { force: true });
}

for (const [name, workflow] of [
  ['01_sercop_master_poller.json', masterWorkflow],
]) {
  writeFileSync(join(workflowsDir, name), `${JSON.stringify(workflow, null, 2)}\n`, 'utf8');
}

console.log('Workflows regenerados:', '01_sercop_master_poller.json');
