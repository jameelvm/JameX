# JameX — system design summary

**What this is.** The design-level view of the build: the numbers, the decisions,
the failure modes each decision defends against, and the questions you should be
able to answer from it. `README.md` explains *how it was built*; this explains
*why it is shaped this way*.

**How to use it.** Read §2 and §3 to reload the whole design in ten minutes.
Read §5 before an interview — it is the question bank, with the answer compressed
to the point you need to make.

**Status.** Covers phases 1–4 (infrastructure, service decomposition, Identity
and Catalog, Ingest and Encoder). Sections marked ⬜ are designed but not yet
built. This document grows at the end of every phase.

---

## Table of contents

1. [The problem in one page](#1-the-problem-in-one-page)
2. [Architecture](#2-architecture)
3. [The decision register](#3-the-decision-register)
4. [Failure modes and what defends against them](#4-failure-modes-and-what-defends-against-them)
5. [Interview question bank](#5-interview-question-bank)
6. [Coverage map — what you can defend](#6-coverage-map--what-you-can-defend)
7. [Glossary](#7-glossary)

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

### The five events

| Event | Published by | Consumed by |
|---|---|---|
| `VideoUploaded` | Ingest ✅ | Encoder, Catalog |
| `VideoEncoded` | Encoder ✅ | Catalog, Search, Engagement |
| `VideoEncodingFailed` | Encoder ✅ | Catalog |
| `VideoDeleted` | **Catalog ✅** | Search, Engagement |

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
| 15 | A view counter becomes a hot partition | Sharded counters, scatter writes / gather reads | ⬜ phase 5 |
| 17 | Two parts of the same upload land at once | `UpdateExpression` on one map key, not read-modify-write | ✅ |
| 18 | Completion is retried (client timeout, double-click) | Event id stored with the state transition; retry reuses it | ✅ |
| 19 | A rendition would be taller than the source | Ladder generation skips it; falls back to source's own height | ✅ |
| 20 | ABR renditions cut keyframes at different moments | Forced GOP (`-g`, `-keyint_min`, `-sc_threshold 0`) | ✅ |
| 21 | A permanent encode failure retries forever | `EncodingFailedException` caught, published, not retried | ✅ |
| 22 | A transient fault (S3 down) is mistaken for a bad file | Left uncaught — propagates to the queue's own retry | ✅ |
| 16 | The metadata database outgrows one machine | Read replicas, then sharding by `channelId` (Vitess) | ⬜ designed only |

---

## 5. Interview question bank

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

**"How do you handle the write volume on view counts?"** ⬜ phase 5
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
| N+1 across a network, BFF aggregation | Good | ✅ batch endpoints |
| Queue mechanics: DLQ, visibility, retry budgets | Good | ✅ |
| Pub/sub fan-out with filtering | Good | ✅ |
| Back-of-envelope estimation | Good | — analysis only |
| Sharded counters, hot partitions | Designed | ⬜ phase 5 |
| Large-file upload, resumability | **Strong** | ✅ verified: concurrent parts, resume, idempotent completion |
| Transcoding, ABR ladder | **Strong** | ✅ verified: GOP alignment, no-upscale, real FFmpeg |
| Data-plane / control-plane separation | **Strong** | ✅ bytes bypass the service; only metadata does not |
| Inverted index vs relational FTS | Designed | ⬜ phase 5 |
| CDN tiering by popularity | Designed | ⬜ phase 5–6 |
| Database sharding, Vitess | Reading only | ⬜ not planned |
| Auth, rate limiting, recommendations | Out of scope | ⬜ |

**The honest framing:** phase 3 was not about video. It was about making an
event-driven system *correct* — which is the part most candidates get wrong. If
asked "you have two services and a queue; how do you stop their data drifting
apart?", you can name both patterns, describe the exact crash sequence each one
prevents, and say what you verified.

---

## 7. Glossary

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
| **LRU** | Least-recently-used — evict whatever has gone untouched the longest |
| **Long tail** | Most items get very few requests; a few get almost all of them |
| **N+1** | One query for a list, then one more per item — ruinous when each is a network call |
| **Outbox pattern** | Writing an outgoing event to your own database in the business transaction, relayed later |
| **Partial index** | An index over only the rows matching a predicate — smaller, and no filter at read time |
| **PoP** | Point of presence — a cache site close to viewers |
| **SKIP LOCKED** | Postgres clause letting concurrent workers claim disjoint row sets without blocking |
| **Visibility timeout** | How long a claimed SQS message stays hidden before becoming redeliverable |
