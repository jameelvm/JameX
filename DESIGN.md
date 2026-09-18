# JameX — system design summary

**What this is.** The design-level view of the build: the numbers, the decisions,
the failure modes each decision defends against, and the questions you should be
able to answer from it. `README.md` explains *how it was built*; this explains
*why it is shaped this way*.

**How to use it.** Read §2 and §3 to reload the whole design in ten minutes.
Read §5 when you need the deeper version — it is the Q&A bank, with the
answer compressed to the point you need to make.

**Status.** Covers phases 1–6 (infrastructure, service decomposition, Identity
and Catalog, Ingest and Encoder, Engagement and Search, the Gateway and
frontend). Sections marked ⬜ are designed but not yet built. This document
grows at the end of every phase.

---

## Table of contents

1. [The problem in one page](#1-the-problem-in-one-page)
2. [Architecture](#2-architecture)
3. [The decision register](#3-the-decision-register)
4. [Failure modes and what defends against them](#4-failure-modes-and-what-defends-against-them)
5. [Design Q&A bank](#5-design-qa-bank)
6. [Coverage map — what you can defend](#6-coverage-map--what-you-can-defend)
7. [Doc-to-code map](#7-doc-to-code-map)
8. [Glossary](#8-glossary)

---

## 1. The problem in one page

Six functional requirements: **stream, upload, search by title, like/dislike,
comment, view thumbnails.** Deliberately small — the difficulty is scale, not
features.

Four non-functional ones: **availability, scalability, performance,
reliability** — in that order of emphasis.

### The four numbers that drive everything

| Quantity | Value | What it forces |
|---|---|---|
| **Storage** | 180 GB/min → ~95 PB/year (one rendition) | Blob storage must be separate from relational metadata. No RDBMS holds 95 PB |
| **Ingest** | ~480 Gbps | Bytes cannot pass through the application tier. Hence presigned direct-to-S3 upload |
| **Egress** | ~12 Tbps | 25× ingest. You cannot serve this from your own data centres — hence CDN and ISP PoPs |
| **Upload : view** | 1 : 300 | The system is overwhelmingly read-heavy. Optimise reads; writes can be slower and asynchronous |

**Derive these, don't recall them.** And treat the "8K servers" figure with
suspicion out loud — using DAU as requests-per-second is a wild over-estimate;
*concurrency* sizes a fleet. Volunteering that critique scores better than the
arithmetic.

### The one consistency decision

Chapter 2 is explicit: content distribution does **not** need strong
consistency.

| Data | Consistency | Why |
|---|---|---|
| Video metadata, counts, feeds | **Eventual** | Seconds of staleness are invisible; availability matters more |
| User data, ownership, privacy | **Strong** | Read-your-writes; a duplicate account must be impossible |

In this build the split is **physical** — Identity owns `jamex_users`, Catalog
owns `jamex_catalog`, and nothing joins across them. That is the cleanest CAP
answer available, because the architecture *shows* the split rather than
asserting it.

---

## 2. Architecture

### Seven services, split by scaling signal

The test applied was: **does this scale on a different signal, and does it own
data nobody else writes?** Splitting by noun produces services that must call
each other constantly.

| Service | Scales on | Owns exclusively |
|---|---|---|
| Gateway | request rate | — (routing + BFF aggregation) |
| Identity | low, steady | `jamex_users` |
| Catalog | **read** volume | `jamex_catalog`, thumbnails table |
| Ingest | upload **bandwidth** | upload sessions, raw bucket |
| Encoder | **queue depth** | media bucket |
| Engagement | **write** volume | counters, reactions, comments |
| Search | query volume | search index |

### The rule that makes it a service architecture

> **Exactly one service reads or writes a given store.** If a service needs data
> it does not own, it calls the owner's API or reacts to its events.

Three separate Postgres *databases*, not three schemas. The moment two services
share a table, independent deployment is gone and you have a distributed
monolith — all the cost of distribution with none of the benefit.

### Synchronous vs asynchronous

```
Browser ──HTTP──► Gateway ──HTTP──► Identity / Catalog / Ingest / Engagement / Search
                                          │
                                          │ events
                                          ▼
                              SNS topic: jamex-video-events
                                          │
                    ┌─────────────┬───────┴───────┬─────────────┐
                    ▼             ▼               ▼             ▼
              encoder-jobs  catalog-events  search-events  engagement-events
                (+ DLQ)        (+ DLQ)         (+ DLQ)        (+ DLQ)
```

**One topic, filtered fan-out.** Each subscription carries a filter policy on
the `eventType` message attribute, so a consumer is not even woken for events it
does not handle. Adding a consumer is a new subscription — no producer change,
no redeploy upstream.

### The four events

| Event | Published by | Consumed by |
|---|---|---|
| `VideoUploaded` | Ingest ✅ | Encoder, Catalog |
| `VideoEncoded` | Encoder ✅ | Catalog, Search, Engagement |
| `VideoEncodingFailed` | Encoder ✅ | Catalog |
| `VideoDeleted` | **Catalog ✅** | Search, Engagement |

`VideoEncoded` grew three fields in phase 5 — `Title`, `Description`, `Tags`,
copied from the `VideoUploaded` that started the encode — purely so Search
can index a video from this one event alone. See §3.7.

### Storage, and why each store exists

| Store | Holds | Chosen because |
|---|---|---|
| PostgreSQL ×3 | Users, video metadata, comments | Joins, filters, ordering, constraints |
| DynamoDB | Counters, reactions, thumbnails, search index | Extreme write throughput, no joins needed |
| S3 ×2 | Raw uploads, encoded renditions | Petabyte scale, CDN-addressable |
| Redis | Watch-page cache | Sub-millisecond reads, LRU eviction |
| nginx | Edge/PoP cache | Serves segments without touching origin |

Three data shapes, three requirements. Forcing all three into Postgres means
counters lock rows and blobs blow the storage budget by orders of magnitude.

---

## 3. The decision register

Each entry: **the decision → the alternative → why**.

### 3.1 Correctness under an unreliable network

These four are the heart of phase 3, and they interlock.

---

**Idempotency lives in the database, not the cache**

- *Alternative:* Redis `SETNX` deduplication.
- *Why:* Redis is a separate system, so "mark seen" and "apply change" fail
  independently. Crash between them and the event is marked handled while the
  work never ran — converting *"might run twice"* into *"might never run"*,
  which is worse. The inbox row is written in the **same transaction** as the
  change, so the primary key rejects redeliveries atomically.
- *Where:* `processed_events`, `InboxUnitOfWork`.
- *Redis is still right* for Search and Encoder, which own no relational store.

---

**Transactional outbox for publishing**

- *Alternative:* commit, then publish to SNS.
- *Why:* two writes to two systems with no transaction spanning them. A crash
  between leaves the video deleted in Catalog but present in Search **forever**,
  with nothing able to detect the drift. Writing the event to `outbox_messages`
  in the same transaction makes the intention to publish exactly as durable as
  the change.
- *The trade:* converts *"the event might vanish forever"* into *"the event
  might arrive twice"*. The first is unfixable corruption; the second is what
  the inbox already handles.
- *Where:* `outbox_messages`, `Outbox<T>`, `OutboxDispatcher<T>`.

---

**The event id is minted once, at write time**

- *Alternative:* let the publisher build a fresh envelope per attempt.
- *Why:* **this is the join between the two patterns above.** A retry carrying a
  regenerated id looks like a brand-new event to every consumer's inbox, and the
  change applies twice with nothing able to catch it. A stable id is what makes
  at-least-once delivery survivable.
- *Where:* `Outbox.Enqueue`, `IEventPublisher.PublishEnvelopeAsync`.

---

**Ordering solved by retry, not sequencing**

- *Alternative:* FIFO queues, sequence numbers, buffering.
- *Why:* if `VideoEncoded` arrives before its video exists, the handler throws.
  The message is left undeleted, becomes visible again after the visibility
  timeout, and succeeds once the upload has landed. **The queue's retry is the
  sequencing mechanism and the visibility timeout is the back-off.** Because the
  inbox claim rolls back with the failed work, the aborted attempt leaves nothing
  behind to block the successful one.

---

**Competing consumers: `FOR UPDATE SKIP LOCKED`**

- *Alternative:* a leader election, or a single-replica relay.
- *Why:* three replicas each running the relay would publish every batch three
  times. `FOR UPDATE` locks the claimed rows; `SKIP LOCKED` makes other replicas
  step *over* them instead of blocking. The relay is then safe to run everywhere
  with no coordination.

---

### 3.2 Data modelling

| Decision | Alternative | Why |
|---|---|---|
| **UUIDv7 primary keys** | UUIDv4 | Time-ordered high bits → inserts land at the B-tree's right edge instead of scattering. Far fewer page splits on append-only tables |
| **Uniqueness from the index** | read-then-write check | The check is a race; two concurrent registrations both read "absent". Catch SQLSTATE `23505` instead — correct *and* one round trip cheaper |
| **`videos.id` never generated** | database default | Ingest mints it before the bytes land and the S3 key embeds it. A second id orphans the file |
| **No FK across a service boundary** | shared database | `videos.channel_id` physically cannot have one. Referential integrity becomes an application concern — the real cost of decomposition |
| **Playback columns nullable** | defaults / sentinel values | A half-encoded video genuinely has no duration. Honest data, not missing data |
| **`tags` as `text[]` + GIN** | join table | Tags are always read with the video and never queried alone, so a join table costs a join on every read for nothing |
| **Partial index for the feed** | full index + filter | `WHERE privacy=2 AND status=3` — every private, queued and failed video is absent from the index entirely. `EXPLAIN` shows no filter, no recheck |
| **`published_at` ≠ `created_at`** | one timestamp | A video uploaded privately in January and published in March must sort by March |
| **Inbox/outbox in shared plumbing** | per-service copies | They are infrastructure, not domain. Three hand-rolled versions would be three sets of bugs |

**Denormalisation — the same-looking problem, opposite answers:**

| | Decision | Why |
|---|---|---|
| `channels.subscriber_count` | **Denormalise** | Low write rate; counting a subscription table per page view is waste |
| Video view counts | **Do not** ⬜ | One hot row cannot absorb the write rate. Sharded DynamoDB counters instead |

The rule: denormalise by **write rate on the hot key**, not by convenience.

---

### 3.3 Caching

| Decision | Why |
|---|---|
| **Cache-aside**, not write-through | Data read far more often than written; write-through makes every write pay for a cache update |
| **Only settled rows cached** | A `Queued` video changes within minutes; caching it guarantees someone sees a stale page |
| **Delete, never overwrite** | Two concurrent updates can reach Redis in the opposite order to Postgres, leaving the cache permanently wrong. Deleting means the next reader repopulates from committed truth |
| **Invalidate *after* commit** | Invalidating first leaves a window where a reader repopulates from the old row and the stale entry outlives the change |
| **TTL as a safety net** | Bounds the damage from a missed invalidation to minutes |
| **Feeds not cached** | Publishing one video shifts every page after it — there is no precise invalidation key |
| **`allkeys-lru` under a memory cap** | Popularity is *discovered from traffic*, never declared by a policy |
| **Cache is optional** | `NullVideoCache` when unconfigured; faults degrade to a database read. Losing a cache must cost latency, never availability |

**The governing rule:**

> Cache something only if you can name exactly which entry to delete when it
> changes.

**Two caches, very different economics:**

| | Metadata cache | Content cache (CDN) |
|---|---|---|
| Item size | ~1 KB | ~300 MB |
| Policy | LRU decides automatically | Deliberate popularity tiering ⬜ |

That 300,000× difference in cost per item is why one can be automatic and the
other cannot.

---

### 3.4 API and service interface design

| Decision | Why |
|---|---|
| **Batch endpoints on every lookup service** | 50 videos carry 50 channel ids. Without batching the Gateway makes 50 HTTP calls — N+1, where each "+1" is a network round trip with its own latency and failure mode |
| **Missing ids absent, not 404** | A partial answer is the useful answer; one deleted account must not fail a whole page |
| **Hard cap on batch and page size** | An unbounded `IN (...)` or `pageSize=1000000` is the cheapest denial of service there is |
| **Handles resolved once, at the edge** | Handles are *mutable*. Every service references channels by immutable id; the translation happens once when a URL arrives |
| **Catalog returns nulls for foreign data** | `ChannelName`, counts and viewer reaction are left empty deliberately — the Gateway overlays them. Guessing would mean reading another service's data |
| **403 not 404 for another user's video** | 404 says "no such thing"; 403 says "it exists and it is not yours" |
| **PATCH, not PUT** | Null means "leave unchanged", so renaming a video cannot silently wipe its description |

---

### 3.5 Operational

| Decision | Why |
|---|---|
| **Liveness never touches dependencies** | A database blip would otherwise restart every healthy service that talks to it. Readiness may check dependencies, because failing it removes the instance from the load balancer rather than killing it |
| **Fail open on the dedup check** | Losing an event is worse than applying it twice — and handlers are idempotent anyway |
| **Retry on transient database faults** | Failovers and restarts are normal, not exceptional. The cost is an execution strategy that manual transactions must run through |
| **Migrations applied at startup — dev only** | Convenient for compose, wrong for production: replicas race, and you get no chance to review a destructive change |
| **DLQ with `maxReceiveCount: 3`** | Stops one poison video starving the queue. You alarm on DLQ depth, inspect, fix, redrive |

---

### 3.6 Upload and transcoding (phase 4)

---

**Data-plane / control-plane split**

- *Alternative:* route uploaded bytes through the application tier.
- *Why:* chapter 2 sizes ingest at ~480 Gbps. A service in that path becomes
  the bottleneck and needs disk or memory to buffer 600 MB originals per
  upload. Ingest's entire API exchanges kilobytes of JSON — ids, part numbers,
  ETags — never bytes. The browser PUTs parts straight to S3 with presigned
  URLs; the service only issues credentials and tracks state.
- *Where:* `RawUploadStore`, `UploadService`.

---

**Atomic per-part writes, not read-modify-write, for resumable upload state**

- *Alternative:* read the session, add the part, write the whole item back.
- *Why:* a browser uploads several parts in parallel. Two completions overlap
  under read-modify-write: both read a 4-entry map, each adds its own key,
  each writes back 5 — one ETag is silently lost, and the upload can never
  complete because S3 requires the full set. `UpdateExpression = "SET
  parts.#n = :etag"` targets one key inside the map server-side; concurrent
  writers touch different keys and cannot conflict.
- *Where:* `UploadSessionRepository.RecordPartAsync`.

---

**Idempotent completion without a database transaction**

- *Alternative:* accept that a crash between "mark complete" and "publish"
  loses the event, the way a naive implementation would.
- *Why:* Ingest owns no relational store, so it cannot use Catalog's
  transactional outbox — there's no transaction to enrol a claim in. Instead,
  the event id is minted once and stored *with* the state transition, guarded
  by a condition only the first caller can satisfy
  (`status = InProgress` at write time). A retried `Complete` call fails that
  condition, reads back the already-stored id, and republishes byte-identical
  bytes — which the consumer's inbox then recognises as a duplicate. Narrower
  than a true outbox (the window shrinks, it doesn't close) but the strongest
  available guarantee with no database.
- *Where:* `UploadSession.CompletionEventId`, `TryMarkCompletedAsync`.

---

**Forced GOP alignment across every rendition**

- *Alternative:* let the encoder place keyframes automatically per rendition.
- *Why:* a player can only switch quality at a segment boundary, and only if
  every rendition's boundaries fall at the same instant. Left alone, an
  encoder places keyframes wherever a scene changes — a different moment in
  every rendition, because each sees the frame differently at that bitrate.
  `-g`, `-keyint_min` and `-sc_threshold 0` force an identical keyframe
  interval everywhere, so every rendition's segments align.
- *Where:* `FfmpegEncodingJobRunner.EncodeRungAsync`.

---

**Never encode a rendition taller than the source**

- *Alternative:* always produce the full configured ladder.
- *Why:* upscaling invents pixels — the output costs more bandwidth and
  storage than the original while looking worse. Ladder selection filters to
  `Height <= source.Height`. The edge case — a source smaller than every
  configured rung — was originally handled by falling back to the smallest
  *configured* rung, which silently upscaled anyway; fixed to fall back to
  the source's own height instead.
- *Where:* `FfmpegEncodingJobRunner.SelectRungs`.

---

**Permanent vs. transient failure, treated as genuinely different**

- *Alternative:* catch every exception and report the job failed; or catch
  nothing and let the queue retry everything blindly.
- *Why:* a corrupt file fails identically on every attempt — retrying it
  burns three receives and compute to reach the same conclusion before
  landing in the DLQ with a generic message. A down S3 endpoint is different
  a minute later — swallowing that as a permanent failure marks a perfectly
  good video as broken forever. `EncodingFailedException` and `TimeoutException`
  are caught and published as `VideoEncodingFailed`; everything else
  propagates uncaught, leaving the message for the queue's own
  retry-with-backoff.
- *Where:* `VideoUploadedHandler.HandleAsync`.

---

### 3.7 Engagement, comments, and search (phase 5)

---

**Sharded view counters over one row per video**

- *Alternative:* a single DynamoDB item holding the view count.
- *Why:* one item caps out around 1,000 writes/sec, and a viral video's view
  counter is the single hottest key in the system. Splitting it into
  `ViewShardCount` (10) items keyed `VIEWS#0..9` and picking one at random per
  write scatters the load; reads sum whatever shards actually exist rather
  than assuming all ten are populated. Likes/dislikes stay unsharded — the
  uniqueness check they require already caps their write rate well below an
  anonymous view ping's.
- *Where:* `VideoCounterRepository`, `EngagementOptions.ViewShardCount`.

---

**Atomic `ReturnValues=ALL_OLD` instead of read-then-decide for reactions**

- *Alternative:* look up the caller's current reaction, decide the counter
  delta in application code, then write the new reaction.
- *Why:* that read and that write are two separate round trips. Two
  concurrent clicks from the same user can both read "no reaction" and both
  increment the counter — a real double-count, not a theoretical one.
  `PutItem`/`DeleteItem` with `ReturnValues=ALL_OLD` makes "write the new
  reaction" and "report what it replaced" one atomic server-side step, so the
  delta computed from the response is always correct even under concurrency.
  Verified: ten concurrent identical like requests from one user landed at
  exactly one like.
- *Where:* `UserReactionRepository.PutAsync`/`RemoveAsync`, `ReactionService`.

---

**Tombstone, not cascade, for a comment with replies**

- *Alternative:* cascade-delete replies along with their parent.
- *Why:* a top-level comment's replies belong to other people; deleting them
  because the parent was deleted destroys content the deleter doesn't own.
  The self-referencing `parent_comment_id` foreign key is `Restrict`, which
  makes that failure mode impossible at the schema level rather than a
  behaviour someone has to remember to avoid in the service. Deletion
  branches on whether replies exist: none → physical delete; any → blank the
  text and set `IsDeleted`, keeping the row so the thread stays intact. A
  reply can never have replies of its own (one-level nesting, enforced in
  `CommentService.AddAsync`), so its delete always takes the physical path.
- *Where:* `Comment.IsDeleted`, `CommentService.DeleteAsync`.

---

**Conditional writes close the cross-store idempotency gap an inbox can't reach**

- *Alternative:* accept that DynamoDB writes triggered by an inbox-protected
  Postgres transaction can still be applied twice on redelivery.
- *Why:* the inbox claim and the DynamoDB write cannot commit as one
  transaction — there is nothing to enrol the second write in. Claiming the
  event first narrows the redelivery window; it doesn't close it. What closes
  it is making the write itself idempotent: `VideoCounterRepository`'s
  counter-initialisation `PutItem` is conditioned on
  `attribute_not_exists(videoId)`, so a redelivered `VideoEncoded` cannot
  reset a counter real traffic has already moved. Verified: bumped likes to 5
  out-of-band, replayed the event with a fresh id, likes stayed at 5.
- *Where:* `VideoCounterRepository.InitializeAsync` (Engagement).

---

**Denormalise metadata into `VideoEncoded` rather than call Catalog back**

- *Alternative:* Search's `VideoEncodedHandler` calls Catalog's read API to
  fetch title/description/tags at index time.
- *Why:* that makes an async, otherwise fully event-driven queue consumer's
  success depend on a second service being reachable, purely to process one
  message — coupling two services' availability for no structural reason.
  `VideoEncoded` instead carries `Title`/`Description`/`Tags`, copied from the
  `VideoUploaded` Encoder already has in hand when it publishes — the same
  reasoning `VideoUploaded` itself already uses to avoid a callback.
- *Where:* `JameX.Contracts.Events.VideoEncoded`, `Encoder/VideoUploadedHandler.PublishEncodedAsync`.

---

**The one synchronous service-to-service HTTP call in this codebase**

- *Alternative:* keep the "never call another service synchronously" rule
  absolute, and have Search return bare video ids with no title or thumbnail.
- *Why:* a search *request* is not a queue consumer — the caller is already
  synchronously blocked on this HTTP response, so one more HTTP hop to
  Catalog's batch endpoint costs nothing structurally that wasn't already
  being paid. The rule the previous entry protects is specifically about
  async handlers gaining a live dependency; a request path gaining one is a
  different, acceptable trade. A Catalog outage degrades this to "no
  results," not a 500.
- *Where:* `ICatalogClient`, `SearchQueryService` (Search).

---

**`word_similarity`, not plain `similarity`, for trigram title search**

- *Alternative:* `pg_trgm`'s `%` operator / `similarity()` function.
- *Why:* `similarity()` compares two entire strings, so a short query like
  "guitar" against a long title is penalised by the size mismatch between
  their trigram sets alone — independent of whether the word is actually
  present. Verified directly: a real title match failed under `similarity()`
  and succeeded under `word_similarity()`, which instead asks whether *some
  substring* of the title matches the query — the question a search box
  actually needs answered. Same GIN index either way; only the comparison
  function changed.
- *Where:* `VideoRepository.SearchByTitleAsync` (Catalog).

---

### 3.8 Frontend and the Gateway as BFF (phase 6)

---

**One Gateway-owned aggregation endpoint, not a purely-proxying gateway**

- *Alternative:* keep the Gateway a pure reverse proxy and let the browser
  call Catalog, Engagement, and Identity separately for the watch page.
- *Why:* three round trips instead of one, three failure points the client
  has to reconcile itself, and every backing service's address exposed
  directly to the public internet. `WatchController`/
  `IWatchAggregationService` is the one deliberate exception to "every route
  is a plain proxy rule": it calls Catalog first and alone (nothing to
  aggregate onto without a video), then fans Engagement and Identity out
  **concurrently** with `Task.WhenAll`, merging onto Catalog's record.
  Failure handling is asymmetric on purpose — Catalog's client propagates a
  fault (no video isn't a degradable state), Engagement's and Identity's
  clients catch and return null (a watch page with stale counts or a
  missing channel name is a visible degradation, not a broken page).
- *Where:* `WatchController`, `WatchAggregationService`, `ICatalogReadClient`
  / `IEngagementReadClient` / `IIdentityReadClient` (Gateway).

---

**A cookie mirrors the viewer's identity specifically for server-rendered requests**

> **Superseded by phase 8 (§3.9), mechanism kept, payload changed.** As
> written below, the cookie held a bare, client-chosen user id, forwarded
> as `X-JameX-User` and trusted outright — the phase 6 guest-identity stub
> had nothing stronger to offer. Phase 8 replaces the payload with a real
> JWT and the trust model with real signature validation; the *reason* a
> cookie exists at all (a Server Component has no `localStorage`) is
> unchanged, which is why this entry is annotated rather than deleted.

- *Alternative:* rely on `localStorage` alone, the way the guest identity is
  otherwise persisted.
- *Why:* the watch page's data comes from a Next.js Server Component
  `fetch`, which runs on the Node process — with no access to the browser's
  `localStorage` at all. Every server-rendered load was therefore anonymous
  regardless of what the browser's guest had actually done, so a like never
  showed as active after a reload even though it had genuinely landed.
  `lib/viewer/cookie.ts` (browser-only `document.cookie`) and
  `lib/viewer/server-viewer.ts` (server-only `next/headers`) are two files,
  not one, because mixing a browser-only and a server-only API in the same
  module breaks bundling in whichever direction wasn't tested. The cookie's
  only job is reaching the server-side call as `X-JameX-User` — the exact
  header `WatchAggregationService` already knew how to use.
- *Where:* `lib/viewer/cookie.ts`, `lib/viewer/server-viewer.ts`,
  `lib/api/watch.ts` (web).

---

**`hide` the origin's own CORS headers at the proxy, don't just `add` on top**

- *Alternative:* trust that LocalStack's S3 emulation sends no
  `Access-Control-*` headers of its own, and let nginx's `add_header`s be
  the only ones on the wire.
- *Why:* it does send its own, on some objects (`.ts` segments, not `.m3u8`
  playlists) — and a response with two `Access-Control-Allow-Origin` values
  is invalid per the CORS spec, rejected outright by every real browser,
  even when both values are the identical wildcard. `curl` and every
  server-side check reported a clean single-header `200 OK`, because
  neither enforces CORS at all — the bug was invisible to anything except an
  actual browser fetch. `proxy_hide_header` on every `Access-Control-*`
  header in the `/media/` location, before `proxy_pass`, makes the edge —
  the CDN boundary — the sole source of the public CORS contract regardless
  of what the origin underneath it sends.
- *Where:* `infra/edge/nginx.conf`, `/media/` location.

---

**A capped retry for *non-fatal* hls.js network errors, not just fatal ones**

- *Alternative:* match only hls.js's own documented fatal-error recovery
  pattern (`startLoad()` on network, `recoverMediaError()` on media) and
  leave non-fatal errors unhandled, since the library itself doesn't
  consider them fatal.
- *Why:* "non-fatal" is a claim that the library can keep running, not a
  guarantee it will keep making progress unattended. A non-fatal
  `levelLoadError` on the level hls.js picked to start playback with can
  leave the stream permanently stalled at zero buffered data even after a
  sibling level's own playlist loaded successfully — hls.js's internal
  retry policy for that specific case had already exhausted itself before
  the error ever reached application code, and nothing further was
  scheduled. A capped retry (`hls.startLoad()`, three attempts) on non-fatal
  `NETWORK_ERROR`s closes that gap without turning a genuinely unreachable
  source into an infinite retry loop.
- *Where:* `VideoPlayer`'s `Events.ERROR` handler (web).

---

**Batch channel-name hydration by *distinct* id, assembled client-side**

- *Alternative:* one Identity call per video row in the home feed.
- *Why:* Catalog's video-list DTO carries a `channelId`, not a display name,
  and a real feed page routinely repeats the same channel across several
  uploads — fetching per row means real duplicate work, not just N+1 in the
  abstract. `hydrateChannelNames` collects the *distinct* channel ids on the
  page, resolves each with one parallel `GET /channels/{id}`, and merges the
  result back by id. This is the identical batch-not-per-row instinct
  behind Identity's `POST /users:batch` and Catalog's `POST /videos:batch`,
  applied against an endpoint that was never built as a batch one — the
  caller, not the resource, is what knows the requests will repeat. A
  channel Identity can't resolve degrades that one card's name to null,
  matching how `IIdentityReadClient` already degrades the watch page.
- *Where:* `lib/api/channel-hydration.ts` (web).

---

**A thumbnail's own load failure is a client-side state, not just a null check**

- *Alternative:* treat a non-null `thumbnailUrl` as sufficient — render an
  `<img>` and let a broken one show the browser's own broken-image icon.
- *Why:* `thumbnailUrl` being present on the wire is Catalog's record, not a
  guarantee the underlying S3 object still exists (LocalStack's free tier
  doesn't persist S3 across a stack restart — see the environment notes).
  A broken-image icon reads as "this app is buggy"; a deliberate placeholder
  reads as "no preview available," which is the true state either way.
  `VideoThumbnail` has to be a client component specifically because
  `<img onError>` only fires in the browser, and falls back to the exact
  same placeholder a `null` URL gets, so a broken load and a genuinely
  absent one are indistinguishable to the viewer.
- *Where:* `components/video/video-thumbnail.tsx` (web).

---

### 3.9 Real authentication (phase 8 — outside the original scope)

The design doc's functional requirements never included login — chapter 2
is explicit that authentication is out of scope, and `ICurrentUser` was
deliberately stubbed behind a plain header from phase 2 onward specifically
*because* building real identity "would add a lot of code that teaches
nothing about video delivery." Phase 8 builds it anyway, at the owner's
request, once every video-delivery concept the doc actually cares about was
already done. It is the one phase in this project with no chapter behind
it — see §7's doc-to-code map, which doesn't attempt to place it under any
of the five chapters for exactly that reason.

---

**`PasswordHasher<TUser>`, not a hand-rolled hash, and not full ASP.NET Identity**

- *Alternative:* a custom PBKDF2/bcrypt call, or pulling in ASP.NET Core
  Identity's full `UserManager`/`SignInManager`/store abstraction.
- *Why:* `PasswordHasher<TUser>` is the one piece of that framework worth
  taking — it embeds its own salt and iteration count in the stored string,
  so there's no separate salt column to manage, and it needs no user store,
  no cookie-auth middleware, and no schema beyond one `string` column to
  use standalone. The full framework's stores and managers solve a problem
  this project doesn't have: exactly one credential type, one identity
  provider, no roles, no external logins.
- *Where:* `Domain/User.cs`, `Services/UserService.cs` (Identity).

---

**A symmetric JWT, issued by Identity, validated only by the Gateway**

- *Alternative:* validate the token independently in every service, or use
  an asymmetric keypair.
- *Why:* asymmetric keys exist to let a party *verify* a token without also
  being able to *forge* one — the right shape when the issuer and the
  verifiers are operated by different parties. Here they're the same
  system, so a shared symmetric key costs nothing extra in trust and one
  fewer moving part. Validating only at the Gateway, not in Identity,
  Catalog, and Engagement independently, is the literal payoff of the
  "authenticate once, forward a trusted identity" design this project
  named as its target back in phase 2 — Catalog and Engagement need zero
  code changes, because they already just read `X-JameX-User`; only what's
  allowed to *set* that header changes.
- *Where:* `TokenService` (Identity), `GatewayRegistrationExtensions.AddJameXJwtBearer`,
  the header-rewrite middleware in `Program.cs` (Gateway).

---

**Strip the client-supplied identity header before trusting anything**

- *Alternative:* leave `X-JameX-User` alone when no valid token is present,
  and only ever *add* a value from a validated token.
- *Why:* "only add, never strip" still lets a caller with no token at all
  set the header directly and have it pass through untouched — the exact
  spoofing vector this design existed to close. The Gateway's middleware
  removes any client-supplied value unconditionally on every request,
  *then* sets it from a validated subject claim if one exists. Verified
  directly: a hand-crafted `X-JameX-User` header with no `Authorization`
  token, sent straight at an endpoint that used to trust it, now 401s.
- *Where:* the header-rewrite middleware in `JameX.Gateway/Program.cs`.

---

**One vague error for every login failure, deliberately**

- *Alternative:* "no account with that email" vs. "incorrect password" as
  distinct messages.
- *Why:* telling an attacker which one failed narrows their search space
  for free — confirming an email exists is most of the work of a
  credential-stuffing attempt. A genuine user gets identical, still fully
  actionable advice ("check your email and password") either way. The
  login path also runs a real password-hash verification against a
  throwaway value even when no account was found, so a timing difference
  between "no such user" and "wrong password" doesn't leak the same
  information a differently-worded message would.
- *Where:* `UserService.LoginAsync` (Identity).

---

**Centralise the auth header in one place, not one per call site**

- *Alternative:* keep threading a `viewerId` (now a token) through every
  function signature in `lib/api/*-client.ts`, the way the phase 6 guest
  stub did.
- *Why:* every authenticated browser call already went through one of
  three shared functions (`browserApiFetch`/`browserApiFetchOrNull`/
  `browserApiMutate`); attaching `Authorization: Bearer` there once means
  reactions, comments, uploads, and channel creation all drop the
  parameter entirely instead of each rebuilding the same header. A request
  with no signed-in viewer simply carries no `Authorization` header, and
  whatever endpoint required one 401s correctly — anonymous browsing is
  the default path, not a special case bolted on.
- *Where:* `lib/api/browser-client.ts` (web).

---

**Anonymous browsing and a real account, not a guest account for everyone**

- *Alternative:* keep phase 6's silent guest-account auto-provisioning
  (a real Identity user created invisibly on first visit) now that real
  accounts exist alongside it.
- *Why:* the two solve different problems and conflating them serves
  neither well. A visitor who never signs up leaves behind a real,
  permanent, useless Identity row under the old design — every anonymous
  visit was quietly an account creation. Dropping it makes "not logged in"
  a first-class, intentional state (`viewer: null`, not a loading flicker
  before an account materialises) and makes signing up mean something: it
  is the first time this browser is asked to create anything at all.
- *Where:* `ViewerProvider` (web) — no longer calls `POST /users` on mount.

---

**`GET /videos/mine` returns every status and privacy level, unlike every other list endpoint**

- *Alternative:* reuse `GetByChannelAsync`'s existing public/Ready filter
  and let "Your videos" show only what a stranger could already see.
- *Why:* the entire point of a personal content list is seeing the videos
  that *aren't* done yet — private ones, ones still transcoding, ones that
  failed. That's safe specifically because the caller is authorised by
  `RequireUserId()` and the query filters by that same id as
  `Video.UploaderId` — there is no path where one account sees another's
  unlisted rows. No cross-service ownership check was needed either:
  Catalog already stores `UploaderId` for its existing PATCH/DELETE
  authorisation, so this reused an existing column rather than asking
  Identity anything.
- *Where:* `VideosController.GetMine`, `VideoQueryService.GetMineAsync`,
  `VideoRepository.GetByUploaderAsync`, and a new
  `ix_videos_uploader_id_created_at` index (Catalog).

---

## 4. Failure modes and what defends against them

The most useful way to hold the design in your head.

| # | What goes wrong | Defence | Built? |
|---|---|---|---|
| 1 | The same message is delivered twice | Inbox — event id as primary key, in the same transaction | ✅ |
| 2 | Crash after committing, before publishing | Outbox — the event is part of the transaction | ✅ |
| 3 | The relay publishes, then crashes before marking sent | Stable event id → the consumer's inbox rejects the resend | ✅ |
| 4 | Three replicas publish the same batch | `FOR UPDATE SKIP LOCKED` | ✅ |
| 5 | An event arrives before the one it depends on | Throw → visibility timeout → retry | ✅ |
| 6 | A stale failure arrives after success | Explicit rule: **Ready always wins** | ✅ |
| 7 | Two concurrent registrations, same email | Unique index; SQLSTATE `23505` → 409 | ✅ |
| 8 | A cached page goes stale after a write | Delete-after-commit, plus a TTL floor | ✅ |
| 9 | Redis dies | `NullVideoCache` + fail-open; degrades to a database read | ✅ |
| 10 | A handler runs longer than the visibility timeout | Heartbeat extends visibility while working | ✅ |
| 11 | A poison message retries forever | DLQ after 3 receives; outbox stops at 10 attempts and logs loudly | ✅ |
| 12 | A client requests a million rows | Page size clamped, batch size capped | ✅ |
| 13 | A viral video's first seconds DDoS the origin | `proxy_cache_lock` — one request fills, the rest wait | ✅ (infra) |
| 14 | A 600 MB upload blocks a request thread | Presigned direct-to-S3 multipart | ✅ |
| 15 | A view counter becomes a hot partition | Sharded counters, scatter writes / gather reads | ✅ |
| 17 | Two parts of the same upload land at once | `UpdateExpression` on one map key, not read-modify-write | ✅ |
| 18 | Completion is retried (client timeout, double-click) | Event id stored with the state transition; retry reuses it | ✅ |
| 19 | A rendition would be taller than the source | Ladder generation skips it; falls back to source's own height | ✅ |
| 20 | ABR renditions cut keyframes at different moments | Forced GOP (`-g`, `-keyint_min`, `-sc_threshold 0`) | ✅ |
| 21 | A permanent encode failure retries forever | `EncodingFailedException` caught, published, not retried | ✅ |
| 22 | A transient fault (S3 down) is mistaken for a bad file | Left uncaught — propagates to the queue's own retry | ✅ |
| 16 | The metadata database outgrows one machine | Read replicas, then sharding by `channelId` (Vitess) | ⬜ designed only |
| 23 | Two concurrent identical reactions from one user double-count | `ReturnValues=ALL_OLD` makes the write atomically report what it replaced | ✅ |
| 24 | A redelivered `VideoEncoded` resets a counter real traffic already moved | `PutItem` conditioned on `attribute_not_exists(videoId)` | ✅ |
| 25 | Deleting a comment with replies orphans them or violates the FK | Tombstone (blank text, keep row) instead of cascade; FK is `Restrict` | ✅ |
| 26 | An inverted-index consumer needs data an event doesn't carry | Denormalise the field into the event, not a synchronous callback | ✅ |
| 27 | The inverted index's base table can't be queried by video id | A `by-video` GSI, same pattern as Engagement's reaction teardown | ✅ |
| 28 | A short query scores too low against a long title in trigram search | `word_similarity()`, not plain `similarity()`, over the same GIN index | ✅ |
| 29 | A response has two conflicting values for the same CORS header | `proxy_hide_header` at the proxy — hide the origin's, add exactly one of your own | ✅ |
| 30 | A player's non-fatal error still leaves playback permanently stalled | Capped app-level retry (`startLoad()`) for non-fatal network errors too | ✅ |
| 31 | A list needs N callers' display names, mostly repeating the same few | Batch by *distinct* id, resolved in parallel, merged back by id | ✅ |
| 32 | A resource's URL is valid on the wire but the underlying object is gone | Client-side `onError` fallback, same placeholder a null URL gets | ✅ |
| 33 | Personalised server-rendered data has no way to know who's asking | Mirror the client identity into a cookie the server *can* read | ✅ |
| 34 | A client sets its own identity header directly and impersonates anyone | Gateway strips it unconditionally, sets it only from a validated JWT | ✅ |
| 35 | A login response tells an attacker whether an email is registered | One error message for both "no such user" and "wrong password" | ✅ |
| 36 | "No such user" resolves faster than "wrong password", leaking which | Hash a throwaway value on the not-found path too, same cost either way | ✅ |
| 37 | Every visitor silently gets a real, permanent account just by browsing | Dropped guest auto-provisioning; anonymous browsing needs no account at all | ✅ |

---

## 5. Design Q&A bank

Grouped by theme. The answer given is the *point to make* — expand from there.

### Consistency and boundaries

**"Strong or eventual consistency?"**
Both, split by data class. Eventual for video metadata and counts, because
seconds of staleness are invisible and availability matters more. Strong for
user data. In this build the split is physical — two databases, no joins across
them. Naming the split and pointing at the schema beats reciting CAP.

**"Why not keep everything in one database?"**
Three data shapes, three requirements: relational metadata needs joins and
ordering; counters need write throughput and no joins; video bytes need
petabyte-scale storage and CDN-addressable URLs. Forcing all three into Postgres
means counters lock rows and blobs blow the storage budget by orders of
magnitude.

**"Why did you split the services where you did?"**
By scaling signal, not by noun. Ingest scales on upload bandwidth, Encoder on
queue depth, Engagement on write volume, Catalog on read volume. Splitting by
entity produces services that must call each other constantly.

**"What stops this being a distributed monolith?"**
One rule: exactly one service reads or writes a given store. Three separate
databases, not three schemas. If a service needs data it does not own it calls
the owner's API or reacts to its events.

**"What did you lose by splitting the database?"**
Referential integrity across the boundary. `videos.channel_id` cannot have a
foreign key, so the database can no longer refuse a video whose channel does not
exist — the service has to. That is the real cost, paid in exchange for
independent scaling and deployment.

### Messaging and consistency between services

**"How do you keep services consistent without distributed transactions?"**
Outbox on the producing side makes publishing atomic with the state change;
inbox on the consuming side makes at-least-once delivery safe. Together they
give effectively-once semantics over an at-least-once bus, with no 2PC.

**"You commit to your database and then publish an event. What if you crash in
between?"**
That is the dual-write problem. The event is lost and the system is permanently
inconsistent with nothing able to detect it. The fix is the transactional
outbox: the event is written to a table in the same transaction, and a relay
publishes it afterwards.

**"How do you make an at-least-once consumer safe?"**
Insert the event id into a `processed_events` table in the same transaction as
the change; the primary key rejects redeliveries. A cache-based check cannot do
this, because a separate system can't share the transaction — and marking an
event seen then crashing turns "runs twice" into "never runs".

**"Two replicas both run your relay. Don't they publish everything twice?"**
No — `SELECT … FOR UPDATE SKIP LOCKED`. Each replica locks the batch it claims
and the others step over those rows. That is also why the event id is generated
once at write time: a rebuilt envelope would carry a new id and defeat the
consumer's inbox.

**"An event arrives before the one it depends on. How do you order them?"**
Usually you don't. The handler throws, the message reappears after the
visibility timeout, and it succeeds once the prerequisite has landed. The retry
is the sequencing mechanism. Because the inbox claim rolls back with the failed
work, the aborted attempt leaves nothing behind.

**"Your consumer takes longer than the visibility timeout. What breaks?"**
The message becomes visible again and a second consumer starts the same work. So
consumers must be idempotent — SQS is at-least-once, never exactly-once. Long
handlers heartbeat by extending visibility rather than setting one huge timeout,
because a huge timeout also delays recovery when a consumer dies.

**"Why one SNS topic instead of a topic per event type?"**
Adding a consumer becomes a subscription with a filter policy — no producer
change, no new topic, no upstream redeploy. Filter policies mean a consumer is
not even woken for events it does not handle.

**"What happens when a video fails to encode?"**
Three receives, then the DLQ. Encoder also publishes `VideoEncodingFailed` so
Catalog can show the uploader a real error instead of leaving the video stuck in
Transcoding forever. You alarm on DLQ depth, inspect, fix, redrive.

### Caching

**"How do you keep a cache consistent?"**
Cache-aside with delete-on-write, invalidated after the commit, plus a TTL short
enough that a missed invalidation self-heals. Deleting rather than overwriting
avoids the race where two updates reach the cache in the opposite order to the
database.

**"What do you cache and what don't you?"**
The watch page, keyed by video id. Not feeds — publishing one video shifts every
page after it, so there is no precise invalidation key. The rule: cache
something only if you can name exactly which entry to delete when it changes.

**"Does the cache hold every video?"**
No, and it shouldn't. Redis runs `allkeys-lru` under a memory cap, so popularity
is discovered from traffic rather than declared. Views follow a long tail — most
videos are barely watched, and caching them would be dead weight. The same
reasoning drives CDN tiering for the bytes, except there the cost per item is
300,000× higher, so placement is deliberate rather than automatic.

**"What happens when Redis dies?"**
Every cache call is wrapped; a fault is treated as a miss and the request is
served from Postgres. The service also starts and runs correctly with no Redis
configured at all. A cache must degrade latency, never availability.

**"How do you stop the CDN hammering origin?"**
`proxy_cache_lock`. On a miss, one request fills the cache and the rest wait —
otherwise a viral video's first seconds become a self-inflicted DDoS. Pair it
with serve-stale-on-error.

### Data and schema

**"How do you prevent duplicate registrations?"**
The unique index, not a pre-check. Checking first is a race — two concurrent
requests both read "absent". The application's job is translating SQLSTATE
`23505` into a 409.

**"Why UUIDv7 and not v4 for primary keys?"**
Both are unique; v7 embeds a timestamp in its high bits so ids sort in creation
order. As a primary key that means inserts land at the right-hand edge of the
B-tree instead of scattering across every page — far fewer page splits and a
much better buffer cache hit rate on a table that only grows.

**"When do you denormalise?"**
By write rate on the hot key. `subscriber_count` is denormalised because
subscriptions are low-volume. View counts are not, because one row cannot absorb
a viral video's write rate — those become sharded counters.

**"How do you handle the write volume on view counts?"**
Sharded counters in DynamoDB. One item per video is a hot partition capped near
1,000 writes/sec; writes scatter across N shard keys and reads gather and sum.
You trade instantaneous exactness for linear write scaling — acceptable, because
a view count is a display value, not a ledger.

**"How would you scale the metadata database?"**
Vertical scaling ends; read replicas absorb reads but not writes; then sharding
is unavoidable. Sharding by hand pushes routing into application code and breaks
cross-shard ACID — Vitess exists to keep one logical interface over a sharded
fleet, and YouTube built it for exactly this. The shard key would be
`channelId`, so a channel's videos stay co-located.

### API design

**"How does your BFF avoid chatty inter-service calls?"**
Batch resolution endpoints on every service that owns lookup data, so the
aggregation layer makes one call per service per page rather than one per item.

**"Why direct-to-S3 upload instead of through the API?"**
A 600 MB upload through the application tier occupies a request thread for
minutes, needs disk or memory to buffer, and makes the service the bottleneck at
480 Gbps ingest. Presigned multipart URLs let the browser write straight to S3;
resume comes free because parts are independently retryable.

**"How do you make a large upload resumable?"**
One record per upload holding every confirmed part's ETag. On reconnect the
client asks which parts already landed — via a strongly-consistent read, so a
part confirmed a moment ago is never reported missing — and re-sends only the
gaps. Recording a part safely under concurrency is the subtle part: a
read-modify-write across parallel part completions loses updates, so the
write targets one key inside a nested map directly (`SET parts.#n = :etag`)
rather than reading the whole item first.

**"You don't have a database in this service. How do you make an operation
idempotent without one?"**
Store the id of the event you're about to publish *with* the state
transition it's tied to, guarded by a condition only the first caller can
satisfy. A retry fails that condition, reads back the id that already won,
and republishes byte-identical bytes — which the downstream consumer's inbox
then recognises as a duplicate. Weaker than a transactional outbox — the
window narrows rather than closes — but it's the strongest guarantee
available with no relational store to enrol a claim in.

**"Walk me through how adaptive bitrate switching actually works."**
Two file types. One master playlist lists every quality with its bandwidth
and resolution; the player reads it once and picks a starting quality — lowest
first, so playback starts immediately rather than stalling on a stream too
big for an unknown connection. Each quality then has its own playlist listing
that quality's segments. Switching mid-playback means fetching a different
quality's playlist and continuing from there — which only works cleanly if
every quality's segments are cut at identical timestamps. That alignment
needs an explicit forced keyframe interval; left alone, the encoder places
keyframes wherever the scene changes, a different moment in every rendition.

**"When would you deliberately not use the highest-quality settings available
for a rendition?"**
When the source doesn't support them. A rendition taller than the source
invents pixels — costing bandwidth and storage to produce output that is both
larger and blurrier than the original. Ladder generation always caps at the
source's own resolution, including the edge case of a source smaller than
every configured rung, where it produces one rendition at the source's native
size rather than upscaling to the smallest configured option.

**"How do you decide whether to retry a failed background job?"**
Split the exception space into permanent and transient, and only retry the
second kind. A corrupt file or an unsupported codec fails identically on
every attempt — retrying burns compute to reach the same conclusion three
times before landing in a dead-letter queue with a generic error. A down
dependency (storage, network) is different tomorrow, so those exceptions are
left to propagate and let the queue's own retry-with-backoff handle them.
Catching everything into one failure path loses this distinction and either
wastes retries on the unfixable or gives up too early on the recoverable.

### Search and engagement (phase 5)

**"A user double-clicks Like. How do you stop the counter incrementing
twice?"**
Don't decide the counter delta from a read; decide it from what the write
itself reports. `PutItem`/`DeleteItem` with `ReturnValues=ALL_OLD` makes
"write the new reaction" and "tell me what it replaced" one atomic step, so
concurrent writes from the same user can't both see "no reaction" and both
increment. Verified with ten concurrent identical requests landing at
exactly one.

**"You have an event consumer with no relational database. How do you make
it safe against redelivery?"**
Two layers, and naming both matters. A Redis-based check narrows the window
the same way it does for a transcoding consumer, but it cannot close it —
Redis is a separate system from wherever the actual effect lands. What
closes it is making the effect itself idempotent: a DynamoDB write
conditioned on the row not already existing turns a redelivered
initialisation into a genuine no-op, instead of resetting real activity.

**"A comment has replies. The author deletes it. What happens to the
thread?"**
It's tombstoned, not cascaded: the text is cleared and a flag is set, but the
row survives so replies don't point at nothing. Cascading would silently
delete other people's comments to satisfy one person's action. The
self-referencing foreign key is `Restrict`, which makes "just delete it" fail
at the database rather than relying on every future code path to remember
the rule.

**"Explain an inverted index the way you'd whiteboard it."**
Flip the key: instead of `document → words it contains`, store `word →
documents containing it`. `term` is the partition key, `videoId` the sort
key; each row also carries how often the term occurred and where. Searching
one word is one partition read. Searching several has no single query — you
read each term's partition independently and keep only the documents that
appeared under *every* one, ranked by summed frequency. Deleting a document
needs its own secondary index keyed by document id, because the base table
can't answer "which rows mention this document" on its own.

**"When would an event carry data that seems out of place for it, like a
video's title inside an 'encoding finished' event?"**
When the alternative is a queue consumer calling another service
synchronously just to fetch it — which makes an otherwise fully
event-driven, independently-failing consumer's success depend on that other
service being up, for one message. Denormalising the field into the event
costs a few extra bytes and keeps the consumer able to act alone. This
system draws the line precisely there: the same codebase makes exactly one
synchronous cross-service call, and only from a request path where a human
is already synchronously waiting on the response, never from a background
handler.

**"Inverted index or relational full-text search — how do you choose?"**
By who owns the data and what the write volume looks like. If you already
own the source data in a database, FTS there is nearly free and never lags
behind a write. Reach for a dedicated index when a different service needs
to serve the queries, or when indexing write volume would overwhelm the
database that owns the source data. The two also fail differently: an
exact-token index has no typo tolerance at all; a trigram/FTS index has
no clean multi-term boolean logic. A misspelled single-word query is the
sharpest way to show the gap live — it succeeds under trigram similarity and
fails outright under exact-token matching, with the same underlying data on
both sides.

### Frontend and the Gateway as BFF (phase 6)

**"Where should aggregation for a page that needs data from several
services live?"**
Behind an API built for that page, not scattered across the client as
several separate calls. `GET /api/watch/{id}` is the Gateway calling
Catalog, Engagement, and Identity itself — Catalog first and alone, then
Engagement and Identity concurrently — and handing back one response. The
alternative costs three round trips, three client-side failure points to
reconcile, and every backing service's address exposed past the edge for no
reason. Failure handling stays asymmetric even inside one aggregator: a
failure that makes the page meaningless propagates, one that only degrades
it returns null instead.

**"You have a foreign key on every row of a list and need the referenced
entity's display name. How do you avoid N+1 without a dedicated batch
endpoint on the other side?"**
Batch by the *distinct* key, not by row, assembled on the calling side. A
video feed's channel ids repeat heavily — a handful of channels across
dozens of uploads — so deduplicate first, then resolve each unique id in
parallel and merge the results back by key. This doesn't need the resource
owner to have built a batch endpoint; the caller is the one that knows the
requests are going to repeat, and a plain by-id GET is enough to build the
batching around.

**"A proxy sits in front of a third-party origin you don't control. The
response ends up with a header value you didn't intend. What's the fix?"**
Don't assume the origin sends none of the headers you plan to set — hide
whatever it sends and set your own explicitly. Two conflicting values for
the same header can be *worse* than one wrong value: a response carrying
two `Access-Control-Allow-Origin` headers is invalid per spec and a real
browser rejects it outright, even when both values are the identical
wildcard — while a tool that doesn't enforce that policy (`curl`, a
server-side health check) sees a perfectly normal single-header response and
reports nothing wrong. The lesson generalises past CORS: any header a proxy
means to own should be actively stripped from the upstream response, not
merely appended to.

**"How do you make a third-party library's error recovery genuinely
robust, beyond matching its documented recovery pattern?"**
Match the documented fatal-error recovery exactly — that part is usually
well specified and the library's own maintainers have already reasoned
about it. Then separately ask what a *non-fatal* error actually promises:
it means the library believes the system can keep running, not that it will
keep making progress unattended. Here, a non-fatal error on the level a
player picked to start playback with could leave it stalled permanently at
zero buffered data, because the library's own retry policy for that case
had already exhausted itself silently before the error reached application
code. Treating "non-fatal" and "will self-heal" as the same claim is
precisely how a transient blip turns into a permanent, silent failure.

**"A page's data comes from a server-rendered request, but personalising it
needs to know which client is asking. The client's identity lives in
`localStorage`. What breaks, and how do you fix it?"**
`localStorage` is a browser API — a server-rendered fetch runs on the
server process and has no access to it, so every server-rendered load looks
anonymous no matter what the browser actually knows. The fix is mirroring
just the identity, not the whole client state, into something the server
*can* read on that request — a cookie forwarded as a header the aggregation
layer already expects. It's a narrow, purpose-built bridge between two
execution contexts, not a redesign of where identity lives.

### Real authentication (phase 8)

**"Your services trust an `X-JameX-User` header set by whoever calls them.
How do you turn that into something you can actually trust?"**
Move the point of trust to exactly one place, and make everything else
trust *it* instead of the caller. The Gateway strips whatever identity
header the client sent — unconditionally, on every request — then sets it
again only from a JWT it just cryptographically validated. Every
downstream service's code is unchanged: it still just reads the header,
but now that header can only ever hold what a valid signature vouched for.
The mistake to avoid is stripping only *sometimes*, or only adding a
validated value without first removing an unvalidated one — either leaves
the original spoofing path open for exactly the requests that have no
token at all.

**"Symmetric or asymmetric signing for your tokens?"**
Depends on whether the issuer and the verifier are the same trust
boundary. Asymmetric keys exist so a party can verify a signature without
being able to *produce* one — valuable when a third party validates
tokens they didn't issue. Here, Identity issues and only the Gateway
verifies, both inside the same system; a shared symmetric key is simpler
and loses nothing, since neither side needed protection from the other.

**"How do you hash passwords, and why not roll your own?"**
Never invent a KDF. Use a maintained one — here, `PasswordHasher<TUser>`
— that embeds its own salt and cost parameter in the stored value, so
there's no separate salt column to manage and no home-grown iteration
count to eventually get wrong. It's also worth knowing what you *didn't*
pull in: the full ASP.NET Identity framework's user store and sign-in
manager solve multi-provider, multi-role problems this system doesn't
have; taking only the hasher avoids a dependency shaped for a much bigger
problem.

**"A login fails. What should the error message say?"**
The same thing, whether the email doesn't exist or the password is wrong.
Distinguishing them for the caller's convenience is exactly what confirms
to an attacker which emails are registered, for free. Match that on
timing too: hash a throwaway value even on the "no such user" path, or the
faster response time for a nonexistent email becomes the same leak in a
different form.

**"Every call to your API needs to know who's calling. Where does that
identity get attached — in each function, or somewhere central?"**
Wherever every one of those calls already funnels through. If there's a
shared low-level client (here, three thin wrappers every browser-side API
call goes through), attaching the credential there once means every
higher-level call — react, comment, upload, create a channel — never
touches the concept at all. The alternative is re-deriving "how do I prove
who I am" at every call site, which is both more code and more places for
one of them to get it wrong.

**"Should an anonymous visitor get an invisible account created for them,
or should 'not logged in' just be a real, supported state?"**
The latter, once real accounts exist. An auto-provisioned guest account
solves "I need *some* identity to authorise a write against" — which
stops being a real problem the moment users can sign up for their own.
Keeping auto-provisioning around after that just means every anonymous
page view is quietly minting a permanent, empty database row. Treating
"no viewer" as a first-class value your UI branches on (browse freely,
prompt to sign in for anything that writes) is both fewer moving parts and
a more honest account of what's actually happening.

### Implementation-level

**"Do you use the repository pattern with EF Core?"**
`DbContext` is already a unit of work and `DbSet` is already a repository, so a
generic `IRepository<T>` adds nothing and leaks `IQueryable`. What earns its
place is a narrow, intention-revealing interface per aggregate. Critically, the
repositories never call `SaveChanges` — committing belongs to the unit of work,
because the change and its inbox or outbox row must land in one transaction.

**"Controllers or minimal APIs?"**
Both are first-class; the axis is endpoint count and team size, not novelty.
Small surface area → minimal APIs; large surface area or many contributors →
controllers, for the enforced consistency and `[ApiController]`'s automatic model
validation. This build switched to controllers mid-phase and the service,
repository and domain layers did not change by a line — which is the real point.

---

## 6. Coverage map — what you can defend

| Area | Depth | Built |
|---|---|---|
| Consistency models, CAP in practice | **Strong** | ✅ two databases |
| Service boundaries, data ownership | **Strong** | ✅ enforced physically |
| Idempotency, at-least-once delivery | **Strong** | ✅ inbox |
| Dual-write, transactional outbox | **Strong** | ✅ + relay |
| Competing consumers, distributed locking | **Strong** | ✅ SKIP LOCKED |
| Caching strategy and invalidation | **Strong** | ✅ cache-aside |
| Index design (partial, GIN, trigram, composite) | **Strong** | ✅ verified by EXPLAIN |
| Race conditions, constraint-based correctness | **Strong** | ✅ |
| N+1 across a network, BFF aggregation | **Strong** | ✅ batch endpoints + a real Gateway BFF endpoint, verified live through a browser |
| Queue mechanics: DLQ, visibility, retry budgets | Good | ✅ |
| Pub/sub fan-out with filtering | Good | ✅ |
| Back-of-envelope estimation | Good | — analysis only |
| Sharded counters, hot partitions | **Strong** | ✅ verified: 10-way shard, 10 concurrent writes, correct sum |
| Large-file upload, resumability | **Strong** | ✅ verified: concurrent parts, resume, idempotent completion — through both the debug harness and the real Next.js UI |
| Transcoding, ABR ladder | **Strong** | ✅ verified: GOP alignment, no-upscale, real FFmpeg |
| Data-plane / control-plane separation | **Strong** | ✅ bytes bypass the service; only metadata does not |
| Inverted index vs relational FTS | **Strong** | ✅ verified: both live, typo tolerance demonstrated live on one, not the other |
| Idempotent writes with no relational store (Dynamo-only services) | **Strong** | ✅ conditional writes + `ReturnValues=ALL_OLD`, both verified under concurrency |
| Soft-delete / tombstoning to preserve referential structure | Good | ✅ comments, including the frontend correctly rendering a server-side tombstone it didn't create itself |
| Adaptive playback client, ABR quality switching | **Strong** | ✅ verified: real levels parsed from a real encode, non-fatal-error recovery, played end to end |
| Reverse-proxy header ownership (CORS at a CDN boundary) | Good | ✅ found and fixed a real duplicate-header bug a browser-only enforces |
| CDN tiering by popularity | Designed | ⬜ phase 5–6 |
| Database sharding, Vitess | Reading only | ⬜ not planned |
| Authentication: password storage, JWT issuance and validation | **Strong** | ✅ phase 8 (outside the design doc's own scope) — verified: spoofed identity header rejected, real token accepted, timing-safe login failure |
| Rate limiting, recommendations | Out of scope | ⬜ |

**The honest framing:** phase 3 was not about video. It was about making an
event-driven system *correct* — which is the part most candidates get wrong. If
asked "you have two services and a queue; how do you stop their data drifting
apart?", you can name both patterns, describe the exact crash sequence each one
prevents, and say what you verified.

---

## 7. Doc-to-code map

The design doc is five chapters: requirements and estimation (2), the actual
design (3), evaluating that design's weak points (4), and the messier
real-world caveats on top of it (5) — chapter 1 is scoping, not content.
Every named concept from chapters 2–5 that this build addresses is listed
below against the file(s) that implement it, the one-line reason *this*
build made that choice, and the decision-register entry (§3) with the full
argument. Concepts the doc raises but this build deliberately didn't
implement are listed too, under **Designed, not built** — naming a gap on
purpose is different from not knowing it exists.

### Chapter 2 — Requirements and estimation

| Concept | Why chosen here | Where | Full argument |
|---|---|---|---|
| Deriving RPS/storage/bandwidth from DAU, not reciting figures | Sizing decisions (fleet size, upload bandwidth) only mean something if you can re-derive them under different assumptions | §1 of this doc, README §1 | — |
| Read-heavy skew (reads vastly outnumber writes) | Justifies every caching decision in chapters 3–4 — there'd be no point caching a workload that wasn't read-heavy | Redis cache-aside (Catalog), nginx edge cache | §3.3 |

### Chapter 3 — Design

| Concept | Why chosen here | Where | Full argument |
|---|---|---|---|
| Service decomposition by scaling signal, not by noun | Splitting by entity produces services that must call each other constantly; splitting by *what makes it scale* doesn't | Seven services — Gateway/Identity/Catalog/Ingest/Encoder/Engagement/Search | §2 "Seven services, split by scaling signal" |
| One service, one store — no shared tables | The single rule that stops this becoming a distributed monolith; independent deployment dies the moment two services share a table | Separate Postgres databases per service, `AddJameXInboxTable`/`AddJameXOutboxTable` per owner | §2 "The rule that makes it a service architecture" |
| Data-plane / control-plane split for uploads | Routing 600 MB uploads through the application tier makes the service the bottleneck at real ingest bandwidth | Presigned multipart PUT straight to S3; Ingest only issues credentials and tracks state | §3.6 |
| Relational metadata store | Video metadata needs joins, filters, and ordering — the shape a KV store answers badly | Catalog's Postgres schema (`videos`, `renditions`) | §3.2 |
| Sharded write-heavy counters | A single row caps out near 1,000 writes/sec; a viral video's view counter is the hottest key in the system | `VideoCounterRepository`, DynamoDB `jamex-video-counters` | §3.7 |
| Inverted index for search | `term → videoId` is the only shape that answers "which documents contain this word" in one partition read | `SearchIndexRepository`, DynamoDB `jamex-search-index`, `Tokenizer` | §3.7 |
| Object storage + CDN for video bytes | Petabyte-scale storage and CDN-addressable URLs are what a relational or KV store were never built for | S3 (`jamex-raw`, `jamex-media`), nginx edge with `proxy_cache_lock` | §2 "Storage, and why each store exists" |
| Adaptive bitrate streaming (master + variant playlists) | A player needs to switch quality mid-playback without re-buffering from zero, which requires every rendition's segments to align in time | FFmpeg ABR ladder (forced GOP alignment), `VideoPlayer`/hls.js | §3.6 |
| Cache-aside with explicit invalidation | Only cache something you can name the exact key to delete when it changes | Catalog's watch-page cache, delete-after-commit, 5-minute TTL floor | §3.3 |
| Async messaging / pub-sub fan-out | A producer that doesn't know its consumers can grow new ones without a redeploy | One SNS topic (`jamex-video-events`), one SQS queue + DLQ per consumer, subscription filter policies | §3.1 |
| Backend-for-frontend aggregation | A page needing several services' data shouldn't cost the browser several round trips, or expose every backing service publicly | `WatchController`/`WatchAggregationService` (Gateway) | §3.8 |

### Chapter 4 — Evaluation (the design's own weak points)

| Concept | Why chosen here | Where | Full argument |
|---|---|---|---|
| The dual-write problem (commit, then crash before publishing) | The doc names this as the sharp edge of "commit to a database, then tell everyone else" | Transactional outbox — event row written in the same transaction as the change | §3.1 |
| At-least-once delivery, never exactly-once | SQS's own delivery guarantee — a consumer that assumes exactly-once will eventually double-apply something | Inbox pattern (`processed_events`, same transaction as the effect) | §3.1 |
| Competing consumers safely claiming disjoint work | Two replicas of the same relay must not publish the same row twice | `SELECT … FOR UPDATE SKIP LOCKED` in the outbox dispatcher | §3.1 |
| Poison-message isolation | One malformed message shouldn't starve a queue for every other message behind it | DLQ after 3 receives, 4-day retention | §4 (failure-mode table) |
| CDN as a shield against origin overload | A viral video's first seconds without protection is a self-inflicted DDoS on the origin | `proxy_cache_lock` in `infra/edge/nginx.conf` | README §13 |
| A reverse proxy owning its public response contract | The doc's CDN discussion assumes the edge fully controls what it serves — it doesn't automatically, if the origin also sets headers | `proxy_hide_header` on every `Access-Control-*` header before `proxy_pass` | §3.8 |

### Chapter 5 — Reality is more complicated

| Concept | Where this build actually lived it, not just read about it |
|---|---|
| "The client can't be trusted to report state honestly" | `getUploadStatus` — the resumable-upload client never assumes what it already sent; it asks the server and reconciles against that |
| "A library's documented behaviour and its actual behaviour diverge" | hls.js's non-fatal errors don't self-heal the way "non-fatal" implies — found only by testing against a real encode, not by reading the docs. §3.8, §4 row 30 |
| "A tool that doesn't enforce a rule will hide a real violation of it" | `curl` and every server-side check reported a clean response while a real browser rejected the same one outright — the duplicate-CORS-header bug. §3.8, §4 row 29 |
| "An automated test's environment isn't the same as a real one" | A backgrounded browser tab silently starves a video player's internal scheduling loop — invisible to any check that doesn't run in a real, focused tab |

**Designed, not built** — named on purpose, not overlooked:

| Concept | Chapter | Why it's out of scope here |
|---|---|---|
| Database sharding via Vitess | 4 | Read replicas solve this build's actual read load; sharding by `channelId` is designed (§5) but never needed in practice at this scale |
| CDN tiering by popularity | 3–4 | The single-tier edge cache already demonstrates the caching pattern; a popularity-aware second tier adds infrastructure without adding a new concept to defend |
| Per-shot (per-segment) encoding | 5 | A genuine stretch goal — the fixed ABR ladder already demonstrates adaptive streaming end to end |
| Duplicate detection via perceptual hashing / LSH | 5 | Out of scope — no content-similarity requirement in this build's six functional requirements |
| Recommendations (candidate generation + ranking) | 5 | Explicitly out of scope from the start (README §1) — a genuinely different system, not an extension of this one |
| Rate limiting | 2, 4 | Named as a gap even after phase 8 built real login — nothing yet throttles repeated login attempts or per-account request volume |

**Beyond the five chapters** — built anyway, at the owner's request, once
every video-delivery concept the doc itself cares about was done (§3.9 has
the full argument for each):

| Concept | Where | Full argument |
|---|---|---|
| Password hashing with an embedded salt, no separate salt column | `Domain/User.cs`, `Services/UserService.cs` (Identity) | §3.9 |
| A symmetric JWT, issued once, validated only at the Gateway | `TokenService` (Identity), `AddJameXJwtBearer` + header-rewrite middleware (Gateway) | §3.9 |
| Stripping a client-supplied identity header before trusting anything | Header-rewrite middleware, `JameX.Gateway/Program.cs` | §3.9 |
| One indistinguishable error, and equal timing, for every login failure | `UserService.LoginAsync` (Identity) | §3.9 |
| One place a browser call attaches its credential, not one per call site | `lib/api/browser-client.ts` (web) | §3.9 |
| Anonymous browsing as a real state, not an invisible auto-created account | `ViewerProvider` (web) | §3.9 |
| An authorised, privacy-blind-to-self video listing ("Your videos") | `VideosController.GetMine` and friends (Catalog) | §3.9 |

---

## 8. Glossary

| Term | Meaning here |
|---|---|
| **ABR ladder** | The set of quality renditions (360p/720p/1080p) a player switches between |
| **At-least-once** | The queue guarantees delivery but may deliver the same message more than once |
| **BFF** | Backend-for-frontend — an aggregation layer composing one response from several services |
| **Cache-aside** | The application reads the cache, and on a miss reads the database and populates it |
| **Dual-write** | Writing to two systems with no transaction spanning them; a crash between loses one |
| **Effectively-once** | At-least-once delivery plus idempotent consumers — the achievable version of "exactly-once" |
| **Fan-out** | One published message delivered to many independent consumers |
| **Idempotent** | Applying it twice has the same effect as applying it once |
| **Inbox pattern** | Recording handled event ids in the same transaction as the change, to reject duplicates |
| **JWT** | JSON Web Token — a signed, self-contained credential; here, the Gateway validates the signature and trusts whatever subject claim it carries |
| **LRU** | Least-recently-used — evict whatever has gone untouched the longest |
| **Long tail** | Most items get very few requests; a few get almost all of them |
| **N+1** | One query for a list, then one more per item — ruinous when each is a network call |
| **Outbox pattern** | Writing an outgoing event to your own database in the business transaction, relayed later |
| **Partial index** | An index over only the rows matching a predicate — smaller, and no filter at read time |
| **PoP** | Point of presence — a cache site close to viewers |
| **SKIP LOCKED** | Postgres clause letting concurrent workers claim disjoint row sets without blocking |
| **Visibility timeout** | How long a claimed SQS message stays hidden before becoming redeliverable |
