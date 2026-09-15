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

**Last updated:** 2026-09-15
**Phases 1–5: COMPLETE, verified, documented in README.md and DESIGN.md.**
**Phase 6 in progress: Gateway BFF aggregation (the watch page) built and
verified end to end. Next: the Next.js frontend.**
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

**Phase 5 — Engagement and Search.**

- [x] **Module 1 — Engagement data model.** `Comment` (Postgres, `jamex_engagement`)
      is the only relational entity — comments are read as an ordered, paginated
      list per video, a relational access pattern unlike the counters next to
      it. `EngagementDbContext` carries the inbox table only, no outbox:
      Engagement consumes `VideoEncoded`/`VideoDeleted` but never announces its
      own changes.
      `VideoCounterRepository` reaches `jamex-video-counters`: views are split
      across `ViewShardCount` (10) shards keyed `VIEWS#0`…`VIEWS#9` — chapter
      4's write-scaling problem made concrete, since a viral video's view
      counter is the hottest key in the system — while likes/dislikes are a
      single unsharded row each, because the uniqueness check in
      `UserReactionRepository` already caps their write rate far below a raw
      view ping. Both counters use DynamoDB's atomic `ADD`, never
      read-modify-write.
      `UserReactionRepository` reaches `jamex-user-reactions`: one row per
      (user, video) is the entire idempotency mechanism for like/dislike,
      absence of a row meaning "no reaction" rather than a stored value. A
      `by-video` GSI supports the `VideoDeleted` teardown without a table
      scan.
      `EventTables.cs` in `ServiceDefaults` was split into
      `AddJameXInboxTable()` / `AddJameXOutboxTable()` / `AddJameXEventTables()`
      (both) — Engagement is the first service that needs only one of the two.
      `VideoEncodedHandler` initialises likes/dislikes to zero on a video
      becoming playable; `VideoDeletedHandler` drops every counter and
      reaction row. Comments are deliberately left untouched by delete —
      that decision belongs to the comments module, not this one.

      **The cross-store idempotency gap, closed properly, not just noted:**
      Postgres has an inbox, but the DynamoDB writes cannot join its
      transaction — same limitation the Redis deduplicator's remarks describe
      for Encoder. Claiming the event and committing the inbox row first
      narrows the redelivery window; it does not close it. What actually
      closes it is `VideoCounterRepository.InitializeAsync` using a
      `ConditionExpression: attribute_not_exists(videoId)` instead of a plain
      `PutItem` — a redelivered `VideoEncoded` becomes a true no-op rather than
      resetting a counter real traffic has already moved off zero.
      `DeleteAllAsync`/`DeleteAllForVideoAsync` need no such guard: deleting an
      already-deleted row is naturally a no-op.

      **Verified against the live stack** (Docker Desktop + LocalStack,
      messages sent straight to `jamex-engagement-events`, bypassing SNS per
      the reliability note below): first `VideoEncoded` created
      likes=0/dislikes=0; an exact redelivery (same event id) was rejected by
      the inbox with no second Dynamo write; likes bumped to 5 out-of-band via
      `UpdateItem ADD`, then a *fresh* `VideoEncoded` (new event id, same
      video) redelivered — likes stayed at 5, proving the conditional guard
      protects live data even when the inbox can't. `VideoDeleted` then
      removed both counter rows and a planted reaction row (confirmed via the
      `by-video` GSI returning zero items).

- [x] **Module 2 — Reaction/View services and the Engagement REST API.**
      `IReactionService` (like/dislike/switch/withdraw), `IViewService`
      (record-a-view), `IEngagementQueryService` (read counts) sit between
      `EngagementController` and the two repositories from Module 1.
      Routes: `GET/PUT/DELETE /videos/{id}/reactions/me`, `POST
      /videos/{id}/views`, `GET /videos/{id}/counts` — matching the Gateway's
      YARP routes already present in `JameX.Gateway/appsettings.json` from an
      earlier session (`video-reactions`, `video-engagement`).

      **The real problem this module solved:** `UserReactionRepository`
      previously did a plain `PutItem`/`DeleteItem`, which would have forced
      `ReactionService` into read-then-decide-then-write — and that read and
      that write are two separate round trips, so two concurrent clicks from
      the same user could both read "no reaction" and both increment the
      likes counter, double-counting one person. Fixed by having
      `PutAsync`/`RemoveAsync` use DynamoDB's `ReturnValues=ALL_OLD`, which
      makes "write the new reaction" and "learn what it replaced" one atomic
      server-side step — whichever concurrent request is serialised second is
      guaranteed to see the first one's effect, so the counter adjustment it
      computes is always correct.

      **Verified against the live stack**, both directly against Engagement
      (`:8084`) and through the Gateway (`:8080/api/...`): counts read
      0/0/0/0 before any activity; a view ping incremented views to 1;
      reacting Like moved likes to 1; reacting Like again was a true no-op
      (counts unchanged); switching Like→Dislike moved likes back to 0 and
      dislikes to 1 in the same call; removing a reaction and removing it
      again both returned success with counts unaffected the second time; an
      undefined `kind` value returned 400 with a validation body. **The
      concurrency fix specifically**: fired 10 simultaneous identical
      PUT-Like requests from one user — likes stayed at exactly 1, not 10.

- [x] **Module 3 — comments.** `ICommentRepository` (Postgres, plain
      `IUnitOfWork` commits — comments have no inbox/outbox involvement of
      their own beyond sharing the DbContext) backs `ICommentService`, exposed
      by `CommentsController` under `/videos/{id}/comments`, matching the
      Gateway's existing `video-comments` route.

      Added a genuine schema change: `Comment.IsDeleted` (new migration
      `AddCommentIsDeleted`). It exists because the self-referencing
      `ParentCommentId` foreign key is `Restrict`, not `Cascade` — deleting a
      top-level comment that still has replies would either violate that
      constraint or silently orphan the replies. `DeleteAsync` checks
      `HasRepliesAsync` first: a comment with replies is tombstoned
      (`IsDeleted = true`, `Text` blanked, row kept so the thread survives) and
      one with none is removed outright. A reply can never have replies of its
      own — the one-level-nesting rule enforced in `CommentService.AddAsync`
      guarantees that — so a reply delete always takes the hard-delete branch.
      `CommentMapping.ToDto` renders a tombstoned row's text as `[deleted]`
      without exposing that a soft-delete happened.

      `EngagementQueryService.GetCountsAsync` now reads `Comments` from
      `ICommentRepository.CountForVideoAsync` (both stores queried in
      parallel) instead of the hardcoded 0 from Module 2 — counts top-level
      comments and replies together, tombstones included.

      **Verified against the live stack**, directly and through the Gateway:
      migration applied cleanly (`ALTER TABLE comments ADD is_deleted...`);
      posting a top-level comment and a reply to it; a reply-to-a-reply
      rejected with 400 ("Replies cannot themselves be replied to"); counts
      read 2 after both; editing rejected for a non-owner (403) and accepted
      for the owner (`isEdited` flips true); deleting a non-owner's comment
      rejected (403); **deleting the top-level comment while its reply still
      existed tombstoned it** — text became `[deleted]`, the row stayed in the
      top-level list, the reply was still reachable, and the count stayed at
      2; **deleting the reply itself (a leaf, no children) physically removed
      it** — it vanished from the replies list and the count dropped to 1;
      editing a tombstoned comment was rejected with a 400.

- [x] **Search — inverted index data model and event handlers.** `Tokenizer`
      (lowercase, split on runs of Unicode letters/digits, count occurrences —
      deliberately no stemming, no stopword removal, no synonyms; the doc's
      honest limits are the point) backs `ISearchIndexRepository` over
      `jamex-search-index`: `IndexAsync` tokenizes title/description/tags,
      merges a term's occurrences across all three fields into one posting
      (summed frequency, tagged with the highest-priority field it appeared
      in — title beats tags beats description), and writes via
      `BatchWriteItem`. `SearchAsync` fans a multi-term query out to one
      `Query` per term (each a single-partition read) and intersects the
      results in memory — AND semantics, matching chapter 3's description
      exactly. `DeleteAllForVideoAsync` needed a new **`by-video` GSI** on
      `jamex-search-index` (table dropped and recreated locally, since it held
      no data yet) — the base table is keyed by term first, so finding every
      posting for one video is impossible without it, the identical problem
      `UserReactionRepository`'s `by-video` GSI solves in Engagement.

      **A real contract gap, found and fixed, not routed around:** Search
      subscribes only to `VideoEncoded`/`VideoDeleted`, but `VideoEncoded`
      carried none of title/description/tags — only Encoder's technical
      output. Adding a synchronous HTTP call to Catalog was the tempting fix
      but wrong: it would make Search's otherwise fully event-driven,
      failure-isolated design depend on Catalog being reachable just to
      process a queue message. Instead `VideoEncoded` now carries `Title`,
      `Description` and `Tags`, sourced from the original `VideoUploaded`
      Encoder already has in hand when it publishes — the same "denormalize
      into the event" reasoning `VideoUploaded` itself already uses. One
      construction site changed (`Encoder/EventHandlers/VideoUploadedHandler`);
      every consumer deserializes by property name, so nothing else moved.

      `VideoEncodedHandler` uses the Redis `IEventDeduplicator`, not an inbox
      — Search owns no relational store, same as Encoder — but leans on
      `IndexAsync` being a plain overwrite of deterministic content, so a
      redelivery can never corrupt anything even if the Redis filter misses.
      `VideoDeletedHandler` needs no dedup at all: deleting rows that are
      already gone is a no-op.

      **Verified against the live stack**: two videos indexed with an
      overlapping term ("guitar", frequency 3 = 1 title + 1 description + 1
      tag occurrence, correctly tagged field=`title`); `VideoDeleted` for one
      video removed every one of its postings via the `by-video` GSI while
      leaving the other video's postings untouched — confirmed a
      video-unique term vanished entirely and the shared term still showed
      exactly one remaining posting.

- [x] **Search — REST API with cross-service hydration.** `GET /search?q=&limit=`
      (`SearchController` → `ISearchQueryService` → `ISearchIndexRepository`)
      returns `SearchHit[]` (already in `JameX.Contracts.Dtos` — `Title`,
      `ThumbnailUrl`, `DurationSeconds`, `PublishedAt` are all Catalog-owned
      fields Search has no way to answer from its own store).

      **The one deliberate exception to "services never call each other
      synchronously" in this codebase:** `ICatalogClient` makes a real HTTP
      call from Search to Catalog (`POST /videos/batch`) to hydrate postings
      into full result cards. Distinguished explicitly from the
      `VideoEncoded`-carries-title/description/tags fix in the previous
      module: that was an async queue consumer, where a synchronous
      dependency on Catalog's uptime would have coupled two services'
      availability for no reason. A search *request* is different — the
      caller is already synchronously blocked on this HTTP response, so one
      more HTTP hop costs nothing structurally that wasn't already being
      paid. `CatalogClient` degrades a Catalog outage to "no results" (logs a
      warning, returns empty) rather than a 500 — the index itself stays
      intact either way.

      Ranking is `SearchAsync`'s summed term frequency, unchanged from the
      previous module — `MatchedOn` reports the exact terms extracted from
      the query, not a re-derived guess. A video indexed but hydrated to
      nothing (Catalog no longer has it) is silently dropped from results
      rather than shown with holes.

      **Verified against the live stack, the full real pipeline**: published
      a genuine `VideoUploaded` + `VideoEncoded` pair to Catalog's own queue
      (creating a real, Ready video with real title/description/tags) and the
      same `VideoEncoded` to Search's queue; `GET /search?q=jazz+piano`
      returned the video fully hydrated (real title, real CDN thumbnail URL,
      real duration/publishedAt, correct summed score); `q=jazz+guitar`
      (AND semantics, one non-matching term) returned empty; a tags-only term
      matched with the correct lower score; a missing `q` returned 400;
      deleting the video through Catalog's real API relayed `VideoDeleted`
      through the outbox and the video vanished from Search's results —
      confirming the full pipeline, not just the DynamoDB layer in isolation.

- [x] **Catalog — Postgres trigram title search, closing the comparison.**
      `IVideoRepository.SearchByTitleAsync` reuses the `ix_videos_title_trgm`
      GIN index the schema had already been carrying since phase 3, restricted
      to public and Ready, same as the feed. Exposed as `GET
      /videos/search?q=&page=&pageSize=`, reachable through the Gateway
      automatically — it falls inside the existing `/api/videos/{**catch-all}`
      catalog route, so no Gateway config changed.

      **A real tuning finding, not assumed:** the first version used
      `pg_trgm`'s plain `%`/`similarity()` — and a single-word query like
      "guitar" scored too low to match a real title ("Guitar Solo Techniques
      for Rock") and returned nothing. `similarity()` compares the *entire*
      two strings, so a short query against a long title is penalised purely
      by the size mismatch in their trigram sets, independent of whether the
      word is actually present. Switched to `word_similarity()`
      (`EF.Functions.TrigramsAreWordSimilar`/`TrigramsWordSimilarity`), which
      asks "does some substring of the title match the query this well" —
      the question a search box actually needs answered. Still backed by the
      same GIN index; only the comparison function changed.

      **This is what makes the two engines' trade-offs concrete, not
      asserted:** Postgres FTS is synchronous and always fresh (Catalog
      already owns the data, no event lag) and typo-tolerant (trigram
      similarity has no notion of "exact token"); the DynamoDB inverted index
      is eventually consistent and exact-token-only, but supports real
      multi-term AND queries with a frequency-based relevance signal that
      trigram similarity does not provide at all.

      **Verified against the live stack**: three real public/Ready videos
      created through the genuine `VideoUploaded`→`VideoEncoded` pipeline;
      exact-phrase search found the right video; **a misspelled query
      ("improvisaton" — missing a letter) still found "Jazz Piano
      Improvisation Lesson"**, something the DynamoDB inverted index cannot
      do at all since it matches exact tokens only; a single-word query
      against a longer title matched correctly after the `word_similarity`
      fix; a nonsense query returned zero results; a missing `q` returned 400.

- [x] **Documentation.** `README.md` grew §10 (Phase 5 — Engagement and
      Search, eleven subsections covering both stores, sharded counters,
      atomic reactions, comment tombstoning, the cross-store idempotency
      fix, the inverted index, the `VideoEncoded` contract fix, the one
      synchronous cross-service call, and the trigram FTS comparison), a
      Phase 5 verification block in §11, seven new interview talking points
      in §12, and an updated §13 roadmap. `DESIGN.md` got §3.7 (seven new
      decision-register entries), six new failure-mode rows, a new "Search
      and engagement" question-bank section, and an updated coverage map
      (three rows moved from Designed/⬜ to Strong/✅).

**Phase 6 — Gateway and frontend.**

- [x] **Gateway BFF aggregation — the watch page in one round trip.**
      `WatchController` (`GET /api/watch/{videoId}`) is the one endpoint the
      Gateway serves itself rather than proxying — every other `/api/...`
      route is YARP forwarding. `IWatchAggregationService` calls Catalog
      first and alone (no video, nothing to aggregate onto), then fans
      Engagement (counts + the caller's own reaction) and Identity (channel
      name) out **concurrently** via `Task.WhenAll`, and merges the results
      onto Catalog's `VideoDetail` with a `with` expression — the same
      record `VideoDetail.ChannelName`/`Counts`/`ViewerReaction` fields that
      have sat null/zeroed since phase 3 specifically for this moment.

      Three small typed `HttpClient`s (`ICatalogReadClient`,
      `IEngagementReadClient`, `IIdentityReadClient`), matching Search's
      `ICatalogClient` pattern. Deliberately asymmetric failure handling:
      Catalog's client lets a fault propagate (no video is not a state the
      page can degrade around), Engagement's and Identity's clients catch
      `HttpRequestException` and return null (a watch page with stale/zeroed
      counts or a missing channel name is a visible degradation, not a
      broken page).

      **Forwarding the caller's identity required a small shared-plumbing
      fix.** The Gateway needs to set the `X-JameX-User` header on its own
      *outgoing* call to Engagement (to resolve the viewer's own reaction),
      but the header-name constant lived on `internal sealed class
      HeaderCurrentUser` in `ServiceDefaults` — inaccessible outside that
      assembly. Made the class `public` (the constant was already public;
      only the containing class blocked reuse) rather than duplicating the
      literal `"X-JameX-User"` string in Gateway's client code.

      **Verified against the live stack**: real user/channel created in
      Identity, real video pushed through `VideoUploaded`→`VideoEncoded` to
      both Catalog and Engagement; `GET /api/watch/{id}` anonymously
      returned the channel name, zeroed counts, and `viewerReaction: null`
      in one call; after recording a real view and a real like directly
      against Engagement, the **same** endpoint reflected `views: 1, likes:
      1` and, called **with** the liker's own `X-JameX-User` header,
      returned `viewerReaction: 0` — while an anonymous call for the same
      video still correctly showed `viewerReaction: null` alongside the
      same updated counts. A nonexistent video id returned 404.

### Next up (immediate)

The Next.js frontend: watch page with hls.js (consuming `GET
/api/watch/{id}` and showing live rendition switching), and a resumable
upload UI porting the logic already proven in `web/debug/index.html` into a
real app. Comments still leaves one open thread worth returning to: a real
system would let a channel owner moderate comments on their own videos,
which Engagement cannot authorise today for the same cross-service-ownership
reason Catalog cannot verify channel ownership (see Open questions below).

---

## Next up

Ordered. Each phase leaves the build green **and** updates `README.md`.

1. ~~Local AWS substrate~~ — done.
2. ~~Service restructure and event bus~~ — done.
3. ~~Identity and Catalog~~ — done.
4. ~~Ingest and Encoder~~ — done. The pipeline is playable end to end.
5. ~~Engagement and Search~~ — done. Sharded view counters, idempotent
   reactions, comments; DynamoDB inverted index plus a Postgres trigram FTS
   comparison. `README.md` §10 and `DESIGN.md` §3.7 written and current.
6. **Gateway and frontend** — in progress. BFF aggregation for the watch page
   (`GET /api/watch/{id}`) done and verified. Still needed: Next.js with
   hls.js showing live rendition switching, resumable upload UI.
7. **DESIGN.md** — deeper doc-to-code mapping, once phase 6 gives the Gateway
   something real to map. The decision register, failure-mode table and
   question bank are current through phase 5 already.

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
