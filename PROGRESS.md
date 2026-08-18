# JameX — build progress

## ▶ How to resume

Say this to Claude at the start of the next session:

> Read PROGRESS.md and CLAUDE.md in C:\System Design\Youtube\App, then start
> Phase 4 — Ingest and Encoder. Build it in short modules, pausing after each
> one so I can review before you continue.

Then run `docker compose up -d` to bring the stack back.

**Build in short modules.** Phase 3 was delivered as seven modules, each one
concept, verified and explained before moving on. Keep doing that.

---

**Purpose of this file:** if a session is lost, this is the single place that
says where the build stopped and what happens next. Update the *Current state*
and *Next up* sections at the end of every session.

**Standing conventions**

1. At the end of every phase, update `README.md` with full teaching-style
   documentation of what that phase built — architecture, each module, the
   reasoning behind each choice, verification commands and interview talking
   points. The README is revision material, not a change log.
2. Every phase leaves `dotnet build JameX.slnx` green.

---

## Current state

**Last updated:** 2026-08-18
**Phases 1, 2 and 3: COMPLETE, verified, documented in README.md.**
**Next: Phase 4 — Ingest and Encoder. This is the phase that makes playback work.**
**Build:** `dotnet build JameX.slnx` succeeds, 0 warnings, 0 errors.
**Stack:** 11 containers run; all 7 services healthy; event bus verified.
**Runnable end to end:** not yet. Identity and Catalog are fully working, but
nothing publishes VideoUploaded / VideoEncoded yet — those come from Ingest and
Encoder in phase 4. Phase 3's handlers were tested by hand-publishing events.

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

### In progress

Nothing. Phase 3 is closed. Start Phase 4.

---

## Next up

Ordered. Each phase leaves the build green **and** updates `README.md`.

1. ~~Local AWS substrate~~ — done.
2. ~~Service restructure and event bus~~ — done.
3. ~~Identity and Catalog~~ — done.
4. **Ingest and Encoder** — presigned resumable multipart upload publishing
   VideoUploaded; FFmpeg ABR ladder and thumbnails behind `IEncodingJobRunner`
   publishing VideoEncoded. This is the phase that makes playback work.
   Catalog's handlers already exist and are tested, so the moment Ingest
   publishes a real event the metadata row appears with no Catalog change.
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
- Redirecting a service's stdout to a file block-buffers the log, so it lags and
  only flushes on process exit. Verify handlers against the database, not the
  log file.

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
- **Not re-verified in the last session:** a `VideoDeleted` published by the
  outbox relay physically arriving in the Search and Engagement queues. The
  relay's SNS publish succeeded and the filter policies are correct, but
  LocalStack's fan-out stopped delivering. Fan-out itself was verified in phase
  2. Re-check on a fresh `docker compose up` before relying on it in phase 4.
- Optimistic concurrency deferred. Postgres' `xmin` works as a concurrency token
  with no schema change, so it can be added whenever contention appears.
- MediaConvert adapter deferred until the FFmpeg path works end to end.
- Per-shot (per-segment) encoding from chapter 5 is a stretch goal after the
  fixed ABR ladder works.
