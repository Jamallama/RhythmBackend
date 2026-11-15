# Session adapter verification

Evidence recorded during implementation on 2026-11-14 (America/Los_Angeles).
These results do not assert that the new API is deployed or that Unity/phone/glasses
integration has been exercised.

| Check | Result and scope |
| --- | --- |
| ASP.NET Core build | Passed, zero warnings/errors at the initial session implementation checks |
| Session contract runner | 201 checks passed with Azure.Storage.Blobs 12.30.1 and local Azurite 3.37.0, including the published Release app |
| Disposable-session PowerShell smoke script | 30 HTTP checks passed against the local host using Azure SDK + Azurite |
| Existing coaching response checks | All 25 passed; no live Gemini request needed |

Covered behaviors:

- Lost registration acknowledgement and identical replay retain original expiry.
- Changed registration keys fail; plaintext read/upload credentials are absent from
  session manifests; read/upload scopes and cross-session credentials are isolated.
- Exact CSV byte downloads, LF/CRLF acceptance, padded IDs, fractional/rounded BPM,
  one/two-second windows, missing timing, actual zero timing, all-miss sentinel,
  one-hit spread, signed latency with misses, saturation, UTF-8 byte limits, malformed
  CSV and incompatible metadata.
- Concurrent identical uploads publish one attempt and one immutable sequence.
  Changed CSV/metadata conflicts do not overwrite committed data. JSON property
  reordering does not change parsed-content identity.
- **Simulated** second-file failure leaves neither a listable nor downloadable
  attempt; retry succeeds. **Simulated** lost commit acknowledgement does not delete
  successfully published files. A process-equivalent store recreation preserves data.
- **Simulated** revocation between staging and manifest commit defeats publication.
  Revoked/expired sessions reject reads/uploads and cannot be revived by registration.
- **Simulated clock** expiry/cleanup deletes data and session key hashes; a digest-only
  anti-reuse tombstone prevents revival after physical metadata deletion.
- Real local HTTP checks for scoped downloads, conditional-request authorization,
  required fields, JSON request size, operator cleanup auth, CORS preflight for the
  confirmed Pages origin and denial of CORS permission to a different origin.
- A 101-attempt session crosses the actual 100-record pagination boundary. Empty
  continuation pages retain the supplied cursor. Delayed/autoplay metadata is retained.
- QR landing strips the query locally, does not echo its key, and sends no-referrer.
  Captured application stdout/stderr does not contain the disposable credentials.
- Azure SDK readiness rejects an emulator container with public access and an
  emulator account with soft-delete retention enabled.

Still required before release:

- Dedicated Azure storage provisioning/configuration, deployed `/health` and smoke
  checks, and recording the actual deployed commit/schema version.
- External Azure/proxy/telemetry credential-redaction verification, retention and
  backup policy review, and a monitored cleanup schedule that survives App Service
  idling. Local log checks do not establish external-log redaction.
- Unity's successful-hit latency filtering regression tests and real WebGL
  registration/upload retries from `https://seaweedbrainn.github.io`.
- Phone one-scan retrieval, ingestion-cursor behavior, and no duplicate coaching.
- Hardware tests, load testing, failure recovery during actual cloud outages and
  cleanup at deployment-scale load. The optional Bicep template has not been applied
  to an Azure subscription by this implementation session.
