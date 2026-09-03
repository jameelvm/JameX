# JameX — build progress

## ▶ How to resume

Say this to Claude at the start of the next session:

> Read PROGRESS.md and CLAUDE.md in C:\System Design\Youtube\App, then start
> Phase 5 — Engagement and Search. Build it in short modules, pausing after
> each one so I can review before you continue.

Then run `docker compose up -d` to bring the stack back — and see
**Environment notes** below for local-emulator quirks worth knowing before you
start testing (SQS consumer wedges, edge's stale DNS, S3 not surviving a
restart).

**Build in short modules.** Phases 3 and 4 were each delivered as a sequence of
modules, one concept per module, verified and explained before moving on. Keep
doing that.

---

**Purpose of this file:** if a session is lost, this is the single place that
says where the build stopped and what happens next. Update the *Current state*
and *Next up* sections at the end of every session.

**Standing conventions**

1. At the end of every phase, update `README.md` with full teaching-style
   documentation of what that phase built — architecture, each module, the
   reasoning behind each choice, verification commands and interview talking
   points. The README is revision material, not a change log.
2. Also extend `DESIGN.md` — the system design summary: new decisions in the
   register, new rows in the failure-mode table, new questions in the bank, and
   an updated coverage map.
3. Every phase leaves `dotnet build JameX.slnx` green.
4. Build in short modules, one concept each, pausing after every module.

---

## Current state

**Last updated:** 2026-09-03
**Phases 1–4: COMPLETE, verified, documented in README.md and DESIGN.md.**
**Next: Phase 5 — Engagement and Search.**
**Build:** `dotnet build JameX.slnx` succeeds, 0 warnings, 0 errors.
**Stack:** 11 containers run; all 7 services healthy; event bus verified.
**Runnable end to end: YES.** A real video goes upload → transcoded → playable
HLS through the CDN edge in under 20 seconds, with zero manual intervention on
a clean stack. Verified repeatedly with real FFmpeg-generated clips at
multiple resolutions (720p, 4K) through the full presigned-multipart →
`VideoUploaded` → Encoder → `VideoEncoded` → Catalog → edge cache chain.

### Architecture decision (2026-08-07)

Started as a modular monolith (API + Worker). Changed to **service-oriented,
seven services, each owning its data exclusively** — the user asked whether it
was microservices and chose to build the decomposition properly. Nothing from
phase 1 was wasted; the infrastructure was extended rather than replaced.

Rationale kept in `README.md` §"Why seven services".

### Done

- [x] Read all five design-doc PDFs; requirements, estimates and architecture.
- [x] **Phase 1 — local AWS substrate. Verified.**
      S3 `jamex-raw` + `jamex-media` (CORS exposing ETag, lifecycle rules);
      DynamoDB × 5 (counters, reactions, thumbnails, search-index,
      upload-sessions with TTL); Redis with `allkeys-lru`; nginx edge cache
      observed going MISS → HIT; Postgres with per-service databases.
- [x] `README.md` written with full phase 1 documentation.
- [x] **Phase 2 — service restructure.** In detail:
      - Solution reshaped to `src/shared/` (`JameX.Contracts`,
        `JameX.ServiceDefaults`) and `src/services/` (Gateway, Identity,
        Catalog, Ingest, Encoder, Engagement, Search).
      - `JameX.Contracts`: event schema (`VideoUploaded`, `VideoEncoded`,
        `VideoEncodingFailed`, `VideoDeleted`, `EventEnvelope<T>`), DTOs for
        video/upload/engagement, shared enums.
      - `JameX.ServiceDefaults`: AWS client factory (including the separate
        presigning client bound to the browser-facing endpoint), SNS publisher,
        SQS consumer with visibility heartbeat and DLQ semantics, Redis
        deduplicator, health endpoints, OpenAPI/Scalar, CORS, `ICurrentUser`.
      - Infra extended: SNS topic `jamex-video-events`, four SQS queues each
        with a DLQ and a subscription filter policy, raw message delivery on,
        SQS access policies scoped to the topic ARN.
      - Postgres split into `jamex_users`, `jamex_catalog`, `jamex_engagement`.
      - `Service.Dockerfile` parameterised by `SERVICE` build arg;
        `Encoder.Dockerfile` bakes in FFmpeg.
      - Gateway routing table in YARP config; edge/CDN moved to port 8090 to
        free 8081–8086 for services.
      - **Verified:** publishing `VideoUploaded` reaches encoder-jobs and
        catalog-events only; `VideoEncoded` reaches catalog, search and
        engagement only. Raw message delivery confirmed (SQS body is the
        message, attributes pass through).

      - **Verified:** all 7 images build; 11 containers run; every service
        answers `/health/live`; all 4 consumers attach to their queue; the
        Gateway routes to all 5 backends (404 from the service, not 502).
        Publishing `VideoUploaded` reached encoder-jobs and catalog-events
        only; `VideoEncoded` reached catalog, search and engagement only —
        Encoder correctly never saw it. Raw message delivery confirmed.
- [x] `README.md` extended with full phase 2 documentation (§3 why seven
      services, §7 the service architecture, §8 verification, §9 talking
      points).
- [x] **Phase 3 — Identity and Catalog.** Delivered as seven modules:

      1. **Identity data model.** `users` + `channels` in `jamex_users`.
         UUIDv7 keys, unique indexes on email and handle, cascade FK.
         Shared `PostgresExtensions` (retry policy, snake_case naming,
         migrate-on-startup, DbContext health check).
      2. **Identity REST API.** 8 endpoints including batch lookup for the
         Gateway. 409 derived from SQLSTATE 23505, not a pre-check race.
      3. **Catalog data model.** `videos` (27 cols), `renditions`,
         `processed_events`, `outbox_messages`. Partial / GIN / trigram
         indexes, all confirmed used via EXPLAIN.
      4. **Catalog event handlers.** VideoUploaded / VideoEncoded /
         VideoEncodingFailed, each claiming the event in the inbox inside the
         same transaction as its change.
      5. **Catalog read API + Redis cache-aside.** 4 endpoints, watch page
         cached with delete-on-write invalidation and a 5-minute TTL.
      6. **Catalog write API + transactional outbox.** PATCH and DELETE with
         uploader authorisation; `VideoDeleted` written to the outbox in the
         delete transaction and relayed by a `FOR UPDATE SKIP LOCKED`
         dispatcher.
      7. **Documentation.** README §8 (phase 3), §9 (verification) and six new
         talking points.

      **Refactors made during the phase, at the owner's request:** repository +
      service layering so no logic sits in endpoints; purpose-named folders
      (`Contracts/`, `Mapping/`, `Validation/`, `Caching/`); and a switch from
      minimal APIs to **MVC controllers** — which touched only the transport
      layer, leaving services, repositories and domain untouched. `CLAUDE.md`
      was updated to match.

      **Verified:** duplicate events rejected by the inbox (5 messages in →
      videos=1, renditions=3, inbox=3); a failed out-of-order handler leaves
      *no* row and *no* inbox claim, proving the rollback covers both; a late
      VideoEncodingFailed cannot demote a Ready video; cache MISS→HIT→
      invalidate→MISS; DELETE leaves videos=0, renditions=0 (cascade) and one
      unsent outbox row, drained by the relay within ~2s.

- [x] **Phase 4 — Ingest and Encoder.** Delivered as six modules:

      1. **Ingest: upload session store.** `UploadSession` in DynamoDB
         (`jamex-upload-sessions`), one item per upload with every part's ETag
         in a nested map. `MultipartPlan` decides slicing from S3's own
         constraints (5 MB min part, 10,000-part ceiling) before any byte
         moves. `RecordPartAsync` uses `UpdateExpression` on one map key, not
         read-modify-write, so parallel part uploads cannot lose an update.
      2. **Ingest: presigned multipart API.** Six endpoints — begin, presign,
         report-part, status, complete, abort. `CompleteAsync` stores its
         `VideoUploaded` event id with the state transition, so a retried
         completion republishes the identical event rather than a new one —
         Ingest's substitute for a transactional outbox, since it owns no
         relational store.
      3. **Encoder: `IEncodingJobRunner` over FFmpeg.** File-in, files-out —
         probe, pick a ladder never taller than the source, encode each rung
         with forced GOP alignment (`-g`/`-keyint_min`/`-sc_threshold 0`) so
         every rendition's segments cut at the same instants, write the
         master playlist lowest-bitrate-first, extract thumbnails. A
         dev-only debug endpoint (`POST /debug/encode`) runs the real ladder
         over an FFmpeg-generated synthetic clip — this is what caught the
         180p-upscaling bug in seconds instead of via the event pipeline.
      4. **Encoder: `VideoUploadedHandler`.** Download → encode → upload
         (master playlist last, deliberately) → publish `VideoEncoded`.
         Permanent failures (`EncodingFailedException`, `TimeoutException`)
         are caught and published as `VideoEncodingFailed`; everything else
         propagates uncaught for the queue's own retry. Redis dedup, not an
         inbox — Encoder owns no relational store.
      5. **Upload + playback debug UI.** Single static page (`web/debug`,
         served on **:3100** — 3000 was already taken on the host) with a
         real presigned-upload flow, a live per-part progress grid,
         Simulate-drop/Resume, and an hls.js player. Found the CORS-`ETag`
         gap and a presigned-URL `https`-vs-`http` scheme mismatch — both
         classes of bug `curl` cannot surface.
      6. **End-to-end verification.** Full pipeline confirmed multiple times:
         upload → Ready in 9–20s, correct renditions, CDN MISS→HIT, correct
         segment `Content-Type`. Found and fixed two real infra bugs along
         the way (Gateway never stripped `/api`; `edge` caches its LocalStack
         upstream IP once at startup and needs a restart after LocalStack is
         recreated) — see Environment notes below for both, plus three
         LocalStack-specific reliability findings from heavy repeated local
         testing that are emulator limitations, not application defects.

      **Verified:** 690 KB and 25 MB real uploads through the complete
      presigned flow; 3 concurrent DynamoDB part-writes all survived;
      `/complete` called twice republished the identical event id; a 720p
      source produced 4 renditions in 4.6s, a 4K source produced 5 in 10.7s;
      a 180p source produced one native-resolution rendition, not an
      upscaled one; the CDN edge served a real segment with the correct
      `video/mp2t` content type.

### In progress

Nothing. Phase 4 is closed. Start Phase 5.

---

## Next up

Ordered. Each phase leaves the build green **and** updates `README.md`.

1. ~~Local AWS substrate~~ — done.
2. ~~Service restructure and event bus~~ — done.
3. ~~Identity and Catalog~~ — done.
4. ~~Ingest and Encoder~~ — done. The pipeline is playable end to end.
5. **Engagement and Search** — sharded view counters, idempotent reactions,
   comments; DynamoDB inverted index plus a Postgres FTS comparison.
6. **Gateway and frontend** — BFF aggregation for the watch page; Next.js with
   hls.js showing live rendition switching, resumable upload UI.
7. **DESIGN.md** — doc-to-code mapping and the interview question bank.

---

## Environment notes

- Docker Desktop must be running before `docker compose up`.
- No FFmpeg on the host — baked into the encoder image, so all transcoding
  happens in Docker.
- .NET SDK 10.0.201, Node 24, npm 11, Docker 29 / Compose v5.
- Solution file is `JameX.slnx` (the .NET 10 XML format), not `.sln`.
- `Microsoft.OpenApi` is pinned to 2.11.0 in `JameX.ServiceDefaults`; the
  version `Microsoft.AspNetCore.OpenApi` 10.0.10 pulls transitively (2.0.0)
  carries advisory GHSA-v5pm-xwqc-g5wc.
- AWS SDK v4 removed `FallbackCredentialsFactory`; use
  `DefaultAWSCredentialsIdentityResolver.GetCredentials()` from
  `Amazon.Runtime.Credentials`.
- Setting both `ServiceURL` and `RegionEndpoint` on an SDK v4 client config
  throws. When a custom endpoint is set, use `AuthenticationRegion` instead.
- `dotnet-ef` is installed as a global tool (10.0.11). Always pass
  `--startup-project` explicitly — it otherwise infers the startup project from
  the shell's current directory and fails with a confusing "dll not found".
- **Never run `awslocal sqs purge-queue`.** AWS deletes messages sent within
  ~60s of a purge; in LocalStack the queue stopped accepting messages entirely
  and needed `docker restart jamex-localstack`.
- LocalStack's SNS→SQS fan-out is unreliable under rapid repeated publishes —
  delivery has been observed anywhere from 4s to over 2 minutes, and some
  messages were dropped. When testing a *handler*, send straight to the queue
  with `sqs send-message` and skip SNS.
- **The `jamex-encoder-jobs` SQS consumer can wedge** after enough repeated
  local testing: the queue shows a message stuck `NotVisible` indefinitely and
  the consumer sits idle (~0% CPU), logging nothing. Restarting the Encoder
  container alone does not fix it. Recovery that has worked: delete and
  recreate the queue, resubscribe it to `jamex-video-events` with the
  `VideoUploaded` filter, **reapply its SNS `sqs:SendMessage` access policy**
  (lost on delete — see `01-bootstrap.sh` for the exact policy JSON), then
  restart Encoder. If that still doesn't hold, a full `docker compose down` /
  `up` has reliably cleared it every time it was tried.
- **`jamex-edge` (nginx) resolves `localstack:4566` once, at container
  startup.** If LocalStack is later recreated (new image, `down`/`up`) while
  `edge` keeps running, nginx keeps routing to the old, dead IP — every
  `/media/...` request 502s with no obvious cause. Fix: `docker compose
  restart edge` after any LocalStack container recreation.
- **LocalStack's Persistence feature (`PERSISTENCE=1`) needs a paid
  Base/Ultimate plan** — this project runs on the free "freemium" tier
  (`LOCALSTACK_AUTH_TOKEN` in `.env`), which does not include it. Verified:
  DynamoDB items survive `docker compose down`/`up` only because its backend
  keeps its own SQLite file under `/var/lib/localstack` regardless of licensed
  persistence, and that file happens to sit in the mounted volume. **S3 has no
  such file on the free tier — every uploaded and encoded video is wiped on
  every stack restart.** Postgres (its own container/volume) is unaffected.
  `PERSISTENCE: "1"` is left set in `docker-compose.yml` as a harmless no-op;
  it would become real if this ever ran against a paid plan. Practical effect:
  after any `docker compose down`, expect to re-upload test videos — old
  Catalog rows will still exist but point at S3 objects that no longer exist.
- Redirecting a service's stdout to a file block-buffers the log, so it lags and
  only flushes on process exit. Verify handlers against the database, not the
  log file.
- **Every service can be debugged locally in Visual Studio alongside its
  running container**, with no config to switch. Each has a `{Service}
  (local)` launch profile on port `50xx` (container is `80xx`); the Gateway
  lists both as destinations per cluster and health-checks them every 5s, so
  stopping a container hands its traffic to the debugger automatically and
  starting it again hands traffic back. Full guide in `DEBUGGING.md`. Two
  caveats: event-driven services (Catalog, Search, Engagement, Encoder) must
  have their container stopped before debugging locally, or both instances
  compete for the same SQS messages and a breakpoint may simply never fire;
  Encoder's local profile additionally needs FFmpeg on `PATH`, which is not
  installed on this machine — debug it in its container instead.
- **The Gateway's YARP routes never stripped the `/api` prefix**, so every
  proxied call 404'd even though the Gateway *reached* the right service —
  looking like it worked (per phase 2's "404 = reached the service" check)
  while actually forwarding a path nothing could match. Fixed with a
  `PathRemovePrefix` transform on every route in
  `JameX.Gateway/appsettings.json`. Worth re-testing after any future route
  addition — a new route with no transform will silently repeat this.

---

## Open questions / deferred

- Auth is stubbed behind `ICurrentUser` (an `X-JameX-User` header). Real
  identity is not in the doc's functional requirements. In production the
  Gateway would authenticate once and forward a signed identity downstream.
- ~~Dual-write risk~~ — **solved in phase 3**. Catalog writes `VideoDeleted`
  to `outbox_messages` in the delete transaction, and `OutboxDispatcher<T>`
  relays it. Reusable by Engagement in phase 5 via `AddJameXEventTables()`.
- **Catalog cannot verify channel ownership.** Writes authorise on
  `uploader_id`, which Catalog owns; whether the caller owns the *channel* lives
  in Identity. The Gateway should resolve it once and forward a signed claim.
- **Still not re-verified:** a `VideoDeleted` published by the outbox relay
  physically arriving in the Search and Engagement queues. Both services have
  no handlers yet (phase 5), so this stays open — but see the LocalStack
  SNS→SQS reliability findings in Environment notes above before assuming a
  single failed delivery attempt means anything is broken; confirm against a
  freshly restarted stack.
- Optimistic concurrency deferred. Postgres' `xmin` works as a concurrency token
  with no schema change, so it can be added whenever contention appears.
- ~~MediaConvert adapter deferred until the FFmpeg path works end to end~~ —
  **the FFmpeg path now works end to end (phase 4).** A MediaConvert
  `IEncodingJobRunner` implementation is a genuine option now, not blocked by
  anything; still deferred by choice, not by dependency.
- **Video and rendition metadata are not deleted from Catalog when a raw
  upload's DynamoDB session TTLs out.** If an upload never completes, the
  `videos` row created by `VideoUploaded` (if that got published) or the
  session itself simply age out independently — there is no reconciliation
  between Ingest's session lifecycle and Catalog's row lifecycle for an
  abandoned upload. Low priority: an abandoned upload never reaches `Ready`
  and is invisible to any feed.
- Per-shot (per-segment) encoding from chapter 5 is a stretch goal after the
  fixed ABR ladder works.
