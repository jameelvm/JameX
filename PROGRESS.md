# JameX — build progress

## ▶ How to resume

Say this to Claude at the start of the next session:

> Read PROGRESS.md and CLAUDE.md in C:\System Design\Youtube\App, then start
> Phase 3 — Identity and Catalog services. Complete the phase fully, update
> README.md with the teaching documentation, and stop for me to review before
> Phase 4.

Then run `docker compose up -d` to bring the stack back.

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

**Last updated:** 2026-08-07
**Phases 1 and 2: COMPLETE, verified, documented in README.md.**
**Next: Phase 3 — Identity and Catalog services.**
**Build:** `dotnet build JameX.slnx` succeeds, 0 warnings, 0 errors.
**Stack:** 11 containers run; all 7 services healthy; event bus verified.
**Runnable end to end:** not yet — services are shells with health endpoints.
No business endpoints and no event handlers exist yet.

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

### In progress

Nothing. Phase 2 is closed. Start Phase 3.

---

## Next up

Ordered. Each phase leaves the build green **and** updates `README.md`.

1. ~~Local AWS substrate~~ — done.
2. **Service restructure and event bus** *(finishing)*.
3. **Identity and Catalog** — the two Postgres-owning services. EF Core models
   and migrations, REST APIs, and Catalog's handlers for VideoUploaded /
   VideoEncoded / VideoEncodingFailed.
4. **Ingest and Encoder** — presigned resumable multipart upload publishing
   VideoUploaded; FFmpeg ABR ladder and thumbnails behind `IEncodingJobRunner`
   publishing VideoEncoded. This is the phase that makes playback work.
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

---

## Open questions / deferred

- Auth is stubbed behind `ICurrentUser` (an `X-JameX-User` header). Real
  identity is not in the doc's functional requirements. In production the
  Gateway would authenticate once and forward a signed identity downstream.
- **Dual-write risk is currently unaddressed.** Services publish events after
  committing their own transaction, so a crash in between loses the event. The
  fix is the transactional outbox pattern: write the event to an `outbox` table
  in the same transaction, and relay it separately. Worth implementing in phase
  3 for Catalog, since it is a strong interview talking point.
- MediaConvert adapter deferred until the FFmpeg path works end to end.
- Per-shot (per-segment) encoding from chapter 5 is a stretch goal after the
  fixed ABR ladder works.
