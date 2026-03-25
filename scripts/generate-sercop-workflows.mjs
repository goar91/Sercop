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
const uniqueRules = [];
const seen = new Set();
const ignoredKeywords = new Set(['acido', 'base']);
const preferredKeywords = [
  'laboratorio',
  'reactivo',
  'reactivos',
  'insumo',
  'insumos',
  'material de laboratorio',
  'materiales de laboratorio',
  'insumos quimicos',
  'insumo quimico',
  'quimica',
  'quimico',
  'reagente',
  'vidrieria',
  'micropipeta',
];
const maxSearchTerms = Number($env.OCDS_MAX_SEARCH_TERMS || 12);

for (const rule of rules) {
  if (String(rule.rule_type || '') !== 'include') {
    continue;
  }

  const keyword = String(rule.keyword || '').trim().toLowerCase();
  if (!keyword || seen.has(keyword) || ignoredKeywords.has(keyword)) {
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

uniqueRules.sort((left, right) => {
  const leftKeyword = String(left.json.searchKeyword || '').trim().toLowerCase();
  const rightKeyword = String(right.json.searchKeyword || '').trim().toLowerCase();
  const leftPreferred = preferredKeywords.indexOf(leftKeyword);
  const rightPreferred = preferredKeywords.indexOf(rightKeyword);

  if (leftPreferred !== rightPreferred) {
    if (leftPreferred === -1) {
      return 1;
    }

    if (rightPreferred === -1) {
      return -1;
    }

    return leftPreferred - rightPreferred;
  }

  if ((right.json.weight || 0) !== (left.json.weight || 0)) {
    return (right.json.weight || 0) - (left.json.weight || 0);
  }

  return leftKeyword.localeCompare(rightKeyword);
});

return uniqueRules.slice(0, maxSearchTerms);`;

const chemistrySupplyGateCode = String.raw`
function hasStrongChemistrySignal(haystack) {
  return [
    /reactiv/i,
    /reagent/i,
    /insumos?/i,
    /material(?:es)? de laboratorio/i,
    /material(?:es)? de referencia/i,
    /est[aá]ndares?/i,
    /qu[ií]mic(?:o|a|os|as)/i,
    /insumos? qu[ií]mic/i,
    /solvente/i,
    /etanol/i,
    /isopropanol/i,
    /hidroxido/i,
    /hipoclorito/i,
    /acetileno/i,
    /\bgases?\b/i,
    /kits?/i,
    /tests? fotom[eé]tricos?/i,
    /medios? de cultivo/i,
    /buffer/i,
    /calibradores?/i,
    /controles?/i,
    /colorantes?/i,
  ].some((pattern) => pattern.test(haystack));
}

function hasLabContextSignal(haystack) {
  return [
    /laborator/i,
    /microbiolog/i,
    /fitopatolog/i,
    /bromatolog/i,
    /absorc[ií]on at[oó]mica/i,
    /docencia e investigaci[oó]n/i,
    /control de calidad/i,
    /calidad del agua/i,
    /aguas? residuales?/i,
    /aguas? potables?/i,
    /agua cruda/i,
    /tejidos? vegetales?/i,
    /suelos?/i,
    /contaminantes?/i,
  ].some((pattern) => pattern.test(haystack));
}

function hasNoiseSignal(haystack) {
  return [
    /transporte/i,
    /estiba/i,
    /alimentaci[oó]n/i,
    /uniforme/i,
    /combustible/i,
    /extintor/i,
    /base naval/i,
    /base de datos/i,
    /servidor/i,
    /impresi[oó]n/i,
    /fotocopi/i,
    /outsourcing/i,
    /agropecuar/i,
    /agr[ií]col/i,
    /fertiliz/i,
    /malezas?/i,
    /plagas?/i,
    /[aá]reas? verdes/i,
    /mobiliario/i,
    /equipo inform[aá]tico/i,
    /repuestos?/i,
    /seguridad/i,
    /veh[ií]culo/i,
    /construcci[oó]n/i,
    /servicio(?:s)? de/i,
    /mantenimiento/i,
    /calibraci[oó]n/i,
    /desaduaniz/i,
    /instrumentos? de medici[oó]n/i,
    /aire acondicionado/i,
    /vitrina/i,
    /incubadora/i,
    /centrifug/i,
    /balanzas?/i,
    /electrodom[eé]stic/i,
    /accesorios? electr[oó]nic/i,
    /invernadero/i,
    /malla antipajaro/i,
    /\bsaran\b/i,
    /marmitas?/i,
    /agitaci[oó]n/i,
    /herramient/i,
    /ferreter/i,
    /campanas? de extracci[oó]n/i,
    /adquirir equipos?/i,
    /equipos?\s+para/i,
    /\bmuflas?\b/i,
    /\bestufas?\b/i,
    /celdas? electroqu[ií]mic/i,
    /analizador/i,
    /insumos generales/i,
    /equipos? de laboratorio/i,
    /adquisi(?:ci[oó]n)? de equipos?/i,
  ].some((pattern) => pattern.test(haystack));
}

function hasPharmaSignal(haystack) {
  return [
    /solido oral/i,
    /s[oó]lido oral/i,
    /parenteral/i,
    /ampollas?/i,
    /c[aá]psulas?/i,
    /tabletas?/i,
    /comprimidos?/i,
    /jarabe/i,
    /mg\/ml/i,
  ].some((pattern) => pattern.test(haystack));
}

function hasMedicalSignal(haystack) {
  return [
    /hospital(?:es)?/i,
    /salud/i,
    /cl[ií]nic(?:o|a|os|as)/i,
    /centro m[eé]dic/i,
    /m[eé]dic(?:o|a|os|as)/i,
    /dispositivos? m[eé]dic/i,
    /diagn[oó]stic/i,
    /ex[aá]menes? de laboratorio/i,
    /pacientes?/i,
    /serolog/i,
    /hormonas?/i,
    /sangu[ií]n/i,
    /hematol[oó]g/i,
    /bioqu[ií]mic/i,
    /anatom[ií]a patol[oó]gica/i,
    /laboratorio de patolog[ií]a/i,
    /veterinari/i,
    /hemoaglutin/i,
    /\b(vih|hiv)\b/i,
    /bcr\s*\/?\s*abl/i,
    /\bjak2\b/i,
    /\bpcr\b/i,
    /farmacotecnia/i,
    /hospital del d[ií]a/i,
    /hospitalari/i,
    /apoyo tecnol[oó]gico/i,
    /convenio de uso/i,
    /pruebas? r[aá]pidas?/i,
  ].some((pattern) => pattern.test(haystack));
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
  const maxAgeMs = (recentWindowDays - 1) * 24 * 60 * 60 * 1000;

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
  const raw = String(ocid || '').replace(/^ocds-[^-]+-/i, '').trim();
  return /-\d{3,}$/.test(raw) ? raw.replace(/-\d{3,}$/, '') : raw;
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

function normalizeText(value) {
  return String(value ?? '').toLowerCase();
}

${chemistrySupplyGateCode}
${ecuadorTimestampCode}

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

function typeScore(processType) {
  const normalized = String(processType || '').trim();
  if (/cat[aá]logo/i.test(normalized)) {
    return 0;
  }

  if (/subasta inversa/i.test(normalized)) {
    return 25;
  }

  if (/r[eé]gimen especial/i.test(normalized)) {
    return 22;
  }

  if (/bienes y servicios [uú]nicos|bienes y servicios unicos/i.test(normalized)) {
    return 20;
  }

  if (/infima|ínfima|necesidad|recepci[oó]n de proformas/i.test(normalized)) {
    return 20;
  }

  if (/contrataci[oó]n|cotizaci[oó]n|licitaci[oó]n|menor cuant[ií]a/i.test(normalized)) {
    return 18;
  }

  return normalized ? 15 : 10;
}

function parseDateScore(value) {
  const timestamp = Date.parse(String(value || ''));
  return Number.isFinite(timestamp) ? timestamp : 0;
}

const inputs = $input.all();
for (let index = 0; index < inputs.length; index++) {
  const payload = inputs[index].json;
  const searchKeyword = String($item(index).$node['Seed Search Terms'].json.searchKeyword || '').trim().toLowerCase();

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

    if (!hasStrongChemistrySignal(haystack)) {
      continue;
    }

    if (!hasLabContextSignal(haystack) && !/qu[ií]mic/i.test(haystack)) {
      continue;
    }

    if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack)) {
      continue;
    }

    if (hasPharmaSignal(haystack)) {
      continue;
    }

    const seededHits = searchKeyword ? [{ keyword: searchKeyword, weight: 1 }] : [];
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
    const previewScore = Math.max(0, Math.min(100, taxonomyScore + typeScore(processType) + heuristicScore));

    if (previewScore < threshold) {
      continue;
    }

    const current = candidates.get(ocid) || {
      source: 'ocds',
      external_id: ocid,
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

    candidates.set(ocid, current);
  }
}

return Array.from(candidates.values())
  .sort((left, right) => {
    if ((right.preview_score || 0) !== (left.preview_score || 0)) {
      return (right.preview_score || 0) - (left.preview_score || 0);
    }

    return parseDateScore(right.fecha_publicacion) - parseDateScore(left.fecha_publicacion);
  })
  .slice(0, 25)
  .map((candidate) => ({ json: candidate }));`;

const ocdsScoreCode = `const threshold = Number($env.MATCH_THRESHOLD || 60);
const rules = $items('Load Keyword Rules').map((item) => item.json);
const includeRules = rules.filter((rule) => String(rule.rule_type || '') === 'include');
const excludeRules = rules.filter((rule) => String(rule.rule_type || '') === 'exclude');
const typeWeights = {
  'Subasta Inversa Electrónica': 25,
  'Subasta Inversa Electronica': 25,
  'Ínfimas Cuantías': 20,
  'Infima Cuantia': 20,
  'Necesidades de Contratación y Recepción de Proformas': 20,
  'Necesidades de Contratacion y Recepcion de Proformas': 20,
  'Bienes y Servicios únicos': 20,
  'Bienes y Servicios unicos': 20,
  'Régimen Especial': 22,
  'Regimen Especial': 22,
  'Menor Cuantía': 18,
  'Menor Cuantia': 18,
  'Cotización': 18,
  'Cotizacion': 18,
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
  return String(value ?? '').toLowerCase();
}

function normalizeProcessCode(value) {
  const raw = String(value || '').trim();
  return /-\\d{3,}$/.test(raw) ? raw.replace(/-\\d{3,}$/, '') : raw;
}

${chemistrySupplyGateCode}
${ecuadorTimestampCode}

function typeScore(processType) {
  const normalized = String(processType || '').trim();
  if (/contrataci[oó]n/i.test(normalized)) {
    return Math.max(typeWeights[normalized] ?? 0, 18);
  }

  return typeWeights[normalized] ?? (normalized ? 15 : 10);
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

  if (!hasStrongChemistrySignal(haystack)) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !/qu[ií]mic/i.test(haystack)) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack)) {
    continue;
  }

  if (hasPharmaSignal(haystack)) {
    continue;
  }

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

function normalizeText(value) {
  return String(value ?? '').toLowerCase();
}

${chemistrySupplyGateCode}
${ecuadorTimestampCode}

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

function typeScore(processType) {
  const normalized = String(processType || '').trim();
  if (/recepci[oó]n de proformas|necesidades de contrataci[oó]n/i.test(normalized)) {
    return 22;
  }

  if (/infima|infimas|[íi]nfimas cuant[ií]as/i.test(normalized)) {
    return 20;
  }

  return 15;
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

  if (!hasStrongChemistrySignal(haystack)) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !/qu[ií]mic/i.test(haystack)) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack)) {
    continue;
  }

  if (hasPharmaSignal(haystack)) {
    continue;
  }

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
  .slice(0, 180);`;

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

function normalizeText(value) {
  return String(value ?? '').toLowerCase();
}

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
  const normalized = String(processType || '').trim();
  if (/recepci[oó]n de proformas|necesidades de contrataci[oó]n/i.test(normalized)) {
    return 22;
  }

  if (/infima|infimas|[íi]nfimas cuant[ií]as/i.test(normalized)) {
    return 20;
  }

  return 15;
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

  if (!hasStrongChemistrySignal(haystack)) {
    continue;
  }

  if (!hasLabContextSignal(haystack) && !/qu[ií]mic/i.test(haystack)) {
    continue;
  }

  if (hasMedicalSignal(haystack) || hasNoiseSignal(haystack)) {
    continue;
  }

  if (hasPharmaSignal(haystack)) {
    continue;
  }

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
