import { mkdirSync, writeFileSync } from 'node:fs';

// Checked-in output is served directly; Node is not needed to run the backend.
const ref = name => ({ $ref: `#/components/schemas/${name}` });
const text = { type: 'string' };
const integer = { type: 'integer', format: 'int32', minimum: 0 };
const timestamp = { type: 'string', format: 'date-time', pattern: 'Z$' };
const id = { type: 'string', pattern: '^[0-9a-f]{32}$' };
const key = { type: 'string', pattern: '^[A-Za-z0-9_-]{32}$' };
const object = (properties, required = Object.keys(properties)) => ({ type: 'object', properties, required, additionalProperties: false });
const json = schema => ({ 'application/json': { schema } });
const response = (description, schema) => ({ description, ...(schema ? { content: json(schema) } : {}) });
const errors = {
  400: response('Malformed JSON, ID, cursor, content type, non-HTTPS request, or query key', ref('Error')),
  401: response('Missing/invalid credential or unknown session', ref('Error')),
  403: response('Valid credential has the wrong scope', ref('Error')),
  404: response('Completed attempt or fixed filename does not exist', ref('Error')),
  409: response('session_conflict or run_conflict; original content is unchanged', ref('Error')),
  410: response('Expired or revoked session with the correct credential', ref('Error')),
  413: response('More than 256 KiB per CSV or 2 MiB total JSON', ref('Error')),
  422: response('Unsupported version or invalid CSV/metadata', ref('Error')),
  429: { ...response('Rate limit or session_quota_exceeded', ref('Error')), headers: { 'Retry-After': { schema: { type: 'integer' }, description: 'Seconds until retry for a temporary rate limit; not provided for full sessions' } } },
  503: response('Storage unavailable/busy, request deadline, or session links not enabled', ref('Error'))
};
const sessionParameter = { name: 'session_id', in: 'path', required: true, schema: id };
const runParameter = { name: 'run_id', in: 'path', required: true, schema: id };
const sessionPath = '/v1/sessions/{session_id}';
const runsPath = `${sessionPath}/runs`;
const runPath = `${runsPath}/{run_id}`;
const request = name => ({ required: true, content: json(ref(name)) });
const op = (operationId, summary, scope, responses, parameters = [], body) => ({
  operationId, summary, security: scope ? [{ [scope]: [] }] : [], parameters,
  ...(body ? { requestBody: request(body) } : {}), responses: { ...errors, ...responses }
});
const runMetadata = {
  finished_at_utc: timestamp, game_id: integer, song_id: integer, chart_id: integer,
  sample_interval_sec: { type: 'integer', enum: [1, 2] }, autoplay: { type: 'boolean' },
  latency_basis: { type: 'string', enum: ['successful_hits_only_v1'] },
  successful_hit_count: integer, summary_exceeds_format_range: { type: 'boolean' }
};
const completed = { run_id: id, sequence: { type: 'integer', format: 'int64', minimum: 1 }, status: { type: 'string', enum: ['completed'] }, completed_at: timestamp };
const paths = {
  '/health': { get: { operationId: 'sessionHealth', security: [], responses: { 200: response('Private storage can be reached', object({ status: { type: 'string', enum: ['ok'] }, session_contract_version: { type: 'integer', enum: [1] } })), 503: errors[503] } } },
  '/v1/sessions': { post: op('registerSession', 'Anonymous, retry-safe registration with independent client-generated keys', null,
    { 201: response('Created', ref('Session')), 200: response('Identical registration retry; original timestamps', ref('Session')) }, [], 'RegisterSession') },
  [sessionPath]: { delete: op('revokeSession', 'Revoke the session; subsequent reads/writes fail', 'UploadBearer', { 204: response('Revoked') }, [sessionParameter]) },
  [runsPath]: { get: op('listRuns', 'List completed attempts by server publication sequence', 'ReadBearer', { 200: response('Committed attempts only', ref('RunPage')) }, [
    sessionParameter,
    { name: 'after_sequence', in: 'query', schema: { type: 'integer', format: 'int64', minimum: 0, default: 0 } },
    { name: 'limit', in: 'query', schema: { type: 'integer', minimum: 1, maximum: 100, default: 100 } }
  ]) },
  [runPath]: { put: op('uploadRun', 'Persist both CSVs, then atomically publish an immutable completed attempt; no Gemini dependency', 'UploadBearer', {
    201: response('First commit', ref('UploadReceipt')), 200: response('Identical parsed metadata and exact CSV byte retry', ref('UploadReceipt'))
  }, [sessionParameter, runParameter], 'UploadRun') },
  [`${runPath}/coach`]: { post: op('coachRun', 'Read a completed run and send its validated CSVs to Gemini for song coaching', 'ReadBearer', {
    200: response('Structured Gemini coaching response', ref('CoachRunResponse')), 502: response('Gemini rejected or could not complete coaching', ref('Error'))
  }, [sessionParameter, runParameter]) },
  [`${sessionPath}/coach`]: { post: op('coachSession', 'Summarize progress across the completed runs in a session', 'ReadBearer', {
    200: response('Structured Gemini session coaching response', ref('CoachSessionResponse')), 502: response('Gemini rejected or could not complete coaching', ref('Error'))
  }, [sessionParameter]) },
  '/sessions/{session_id}/': { get: {
    operationId: 'sessionLanding', summary: 'QR entry only; no data retrieval, redemption, or credential echo', security: [],
    parameters: [sessionParameter, { name: 'key', in: 'query', required: true, schema: key, description: 'Read capability; never use this query parameter on /v1 routes' }],
    responses: { 200: { description: 'Minimal HTML; removes query locally and sends no-referrer/no-store', content: { 'text/html': { schema: text } } }, 400: errors[400], 503: { description: 'QR logging redaction not yet confirmed; minimal HTML' } }
  } }
};
for (const file of ['series', 'summary']) paths[`${runPath}/${file}.csv`] = { get: op(
  `download${file[0].toUpperCase()}${file.slice(1)}`, 'Download the original UTF-8 CSV; authorization and session validity checked on each request', 'ReadBearer', {
    200: { description: 'Exact original CSV content; Cache-Control: no-store; no conditional/range bypass', content: { 'text/csv': { schema: text } } }
  }, [sessionParameter, runParameter]) };

const spec = {
  openapi: '3.0.3',
  info: {
    title: 'Rhythm session adapter', version: '1.0.0',
    description: 'Session contract v1. All protected responses are no-store. HTTPS is mandatory outside local development. Keys are independent session-scoped capabilities, not proof of game authenticity. Legacy /api/chat and /api/results are separate and not described here. Default lifetime 24 hours; cleanup removes payloads after expiry/revocation and session metadata within the following 24 hours, retaining an ID-only non-reversible anti-reuse tombstone. QR landing requires operator confirmation of external-log redaction. Rate limits are per running app instance; use one instance or an external distributed limiter.'
  },
  servers: [{ url: 'https://rhythm-backend-emmanuel-hvfkdjhxfjc4d9a5.westus-01.azurewebsites.net' }],
  paths,
  components: {
    securitySchemes: {
      ReadBearer: { type: 'http', scheme: 'bearer', description: 'session_key from QR; grants listing and CSV reads only' },
      UploadBearer: { type: 'http', scheme: 'bearer', description: 'Independent upload_key retained only by the game; grants uploads and revocation only' }
    },
    schemas: {
      Error: object({ error: object({ code: text, message: text }) }),
      RegisterSession: object({ contract_version: { type: 'integer', enum: [1] }, session_id: id, session_key: key, upload_key: key }),
      Session: object({ contract_version: { type: 'integer', enum: [1] }, session_id: id, created_at: timestamp, expires_at: timestamp }),
      UploadRun: object({ contract_version: { type: 'integer', enum: [1] }, ...runMetadata,
        series_csv: { type: 'string', minLength: 1, description: 'Complete original series CSV with five metadata rows and header; at most 262144 UTF-8 bytes' },
        summary_csv: { type: 'string', minLength: 1, description: 'One headerless record; optional final newline; at most 262144 UTF-8 bytes' }
      }),
      UploadReceipt: object({ session_id: id, ...completed }),
      Run: object({ ...completed, ...runMetadata, files: object({ series: text, summary: text }) }),
      RunPage: object({ session_id: id, expires_at: timestamp, runs: { type: 'array', items: ref('Run') },
        next_after_sequence: { type: 'integer', format: 'int64', minimum: 0 }, has_more: { type: 'boolean' } }),
      CoachRunResponse: object({ session_id: id, run_id: id, coaching: object({}) })
      ,CoachSessionResponse: object({ session_id: id, coaching: object({}) })
    }
  }
};
mkdirSync(new URL('../docs/', import.meta.url), { recursive: true });
writeFileSync(new URL('../docs/session-api.openapi.json', import.meta.url), JSON.stringify(spec, null, 2) + '\n');
console.log('Generated docs/session-api.openapi.json');
