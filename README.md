# JameX

A working YouTube clone, built to learn the system design behind it well enough
to defend it in an interview.

The specification is the five-chapter design document in the parent folder
(`1.System Design_ YouTube.pdf` … `5.The Reality Is More Complicated.pdf`). Every
component that document describes has a real, runnable counterpart here.

**Stack:** .NET 10 · PostgreSQL · DynamoDB · S3 · SQS · SNS · Redis · FFmpeg ·
YARP · Next.js · Docker Compose + LocalStack

**Build status:** phases 1–2 complete and verified. Infrastructure and the
service skeleton run; upload and playback arrive in phases 3–4.

---

## Table of contents

1. [The problem being solved](#1-the-problem-being-solved)
2. [Architecture](#2-architecture)
3. [Why seven services](#3-why-seven-services)
4. [Repository layout](#4-repository-layout)
5. [Getting it running](#5-getting-it-running)
6. [Phase 1 — the local AWS environment](#6-phase-1--the-local-aws-environment)
7. [Phase 2 — the service architecture](#7-phase-2--the-service-architecture)
8. [Verification](#8-verification)
9. [Interview talking points](#9-interview-talking-points)
10. [Roadmap](#10-roadmap)

---

## 1. The problem being solved

### Functional requirements

Six, from chapter 2. Deliberately small — the difficulty is scale, not features.

1. Stream videos
2. Upload videos
3. Search videos by title
4. Like and dislike videos
5. Add comments
6. View thumbnails

### Non-functional requirements

| Requirement | What it actually demands |
|---|---|
| **High availability** | No single point of failure; replication across sites; ≥99% uptime |
| **Scalability** | Users, storage and bandwidth grow without degrading performance |
| **Performance** | Smooth playback, minimal startup latency, no rebuffering |
| **Reliability** | An uploaded video is never lost or corrupted |

### The numbers that drive every decision

These are the back-of-the-envelope figures from chapter 2. Learn to *derive*
them; interviewers ask you to reason, not recall.

**Assumptions**

| Quantity | Value |
|---|---|
| Total users | 1.5 billion |
| Daily active users | 500 million |
| Average video length | 5 minutes |
| Raw size, 5-min video | 600 MB (→ 120 MB per minute of content) |
| Compressed size, 5-min video | 30 MB (→ 6 MB per minute of content) |
| Upload rate | 500 hours of content per minute |
| Upload : view ratio | 1 : 300 |

**Storage**

```
Total_storage = Total_upload_per_min × Storage_per_min

500 hours/min × 60 min/hour = 30,000 minutes of content per minute
30,000 min × 6 MB/min       = 180,000 MB  =  180 GB per minute
```

That is **259 TB/day**, roughly **95 PB/year** — and only one rendition. Store
five rungs of an ABR ladder and it is 900 GB/min, ~473 PB/year. Raw originals
are 20× the compressed size on top of that. This single number is why blob
storage and relational metadata must be separate systems: no relational database
holds 95 PB of video.

**Bandwidth**

```
Upload:  30,000 min/min × 120 MB/min = 3.6 TB/min = 60 GB/s ≈ 480 Gbps

Stream:  at 1:300 and ~10 MB per minute of delivered content
         30,000 × 10 MB × 300 = 90 TB/min = 1.5 TB/s ≈ 12 Tbps
```

Egress dwarfs ingest by ~25×. That asymmetry is the entire justification for the
CDN, the ISP points of presence and the IXP tier in chapter 5 — you cannot serve
12 Tbps out of your own data centres economically.

**Servers**

```
Servers = requests_per_second / server_RPS
        = 500,000,000 / 64,000  =  7,812.5  ≈  8K servers
```

Treat this one with suspicion in an interview, and say so. Using DAU directly as
requests-per-second is a wild over-estimate. The honest framing is that
*concurrency*, not daily totals, sizes a fleet. Volunteering that critique scores
better than reciting the arithmetic.

### The one consistency decision

Chapter 2 is explicit: content distribution does **not** need strong consistency.
A newly uploaded video reaching every subscriber a few seconds late is invisible
to users; blocking uploads on global replication is not. So:

- **Video metadata, counts, feeds → eventual consistency.** Optimise for
  availability and latency.
- **User data → strong consistency.** Account state, ownership and privacy must
  not be stale.

In this build that split is physical: Identity owns one database, Catalog owns
another, and nothing joins across them. It is the cleanest CAP answer you can
give, because the architecture *shows* the split rather than asserting it.

---

## 2. Architecture

Seven services, each owning its data exclusively. Synchronous traffic goes
through the Gateway; everything else propagates as events.

```
                          ┌──────────┐
                          │  Client  │  Next.js + hls.js
                          └────┬─────┘
                  ┌────────────┴────────────┐
                  ▼                         ▼
        ┌──────────────────┐    ┌────────────────────────────┐
        │  Gateway  :8080  │    │  Edge / CDN PoP    :8090   │
        │  YARP + BFF      │    │  nginx proxy_cache          │
        └─┬──┬───┬───┬───┬─┘    │  X-JameX-Cache: HIT/MISS    │
          │  │   │   │   │      └─────────────┬───────────────┘
  ┌───────┘  │   │   │   └──────┐             │ miss → origin
  ▼          ▼   ▼   ▼          ▼             ▼
┌────────┐┌───────┐┌──────┐┌──────────┐┌────────────────────┐
│Identity││Catalog││Ingest││Engagement││        S3          │
│ :8081  ││ :8082 ││:8083 ││  :8084   ││                    │
│        ││       ││      ││          ││  jamex-raw         │◄── presigned
│postgres││postgres│dynamo││ postgres ││  jamex-media       │    multipart PUT
│_users  ││_catalog│upload││_engagement│└────────────────────┘
│        ││+thumbs││ sess ││+counters ││          ▲
└────────┘└───────┘└──┬───┘│+reactions│          │ writes ladder
                      │    └──────────┘          │
        ┌──────┐      │                   ┌──────┴──────┐
        │Search│      │                   │   Encoder   │
        │:8085 │      │                   │    :8086    │
        │dynamo│      │                   │   FFmpeg    │
        │index │      │                   └──────▲──────┘
        └───▲──┘      │ publish                  │ consume
            │         ▼                          │
            │  ┌────────────────────────────────────────────┐
            └──┤     SNS  jamex-video-events                │
               │                                            │
               │  filter policy per subscription:           │
               │   VideoUploaded ─────▶ encoder-jobs        │
               │   VideoUploaded ─────▶ catalog-events      │
               │   VideoEncoded  ─────▶ catalog / search /  │
               │                        engagement          │
               │   each queue has a DLQ after 3 attempts    │
               └────────────────────────────────────────────┘
```

### Event flow, end to end

```
1. Client  ──POST /api/uploads──▶  Ingest        opens S3 multipart upload
2. Client  ──PUT part 1..N────▶   S3 direct     bytes never touch a service
3. Client  ──POST complete───▶    Ingest        finalises, publishes VideoUploaded
                                     │
                    ┌────────────────┴──────────────┐
                    ▼                               ▼
              Encoder                          Catalog
        downloads raw from S3            creates metadata row,
        FFmpeg → ABR ladder              status = Transcoding
        + thumbnails → S3
        publishes VideoEncoded
                    │
        ┌───────────┼───────────┐
        ▼           ▼           ▼
    Catalog      Search     Engagement
  status=Ready  indexes    initialises
  + renditions  terms      counters
                    │
4. Client  ──GET master.m3u8──▶  Edge/CDN ──miss──▶ S3
5. Client  ──GET segments─────▶  Edge/CDN  (HIT thereafter)
```

### Design doc → this repository

| Doc component | Doc's technology | JameX | Where |
|---|---|---|---|
| Load balancer / entry | Local + global LB | YARP gateway | `JameX.Gateway` |
| Web + application servers | Lighttpd + custom stack | 6 ASP.NET Core services | `src/services/` |
| Encoders / transcoders | Custom encoder farm | FFmpeg behind `IEncodingJobRunner` | `JameX.Encoder` |
| Upload storage (temporary) | Internal store | S3 `jamex-raw` | LocalStack |
| Blob storage | GFS / Colossus | S3 `jamex-media` | LocalStack |
| Video metadata DB | MySQL → Vitess | PostgreSQL `jamex_catalog` | Catalog |
| User data DB (decoupled) | MySQL | PostgreSQL `jamex_users` | Identity |
| Bigtable (thumbnails, KV) | Bigtable on GFS | DynamoDB | Catalog, Engagement, Search, Ingest |
| Distributed cache | Memcached (LRU) | Redis (`allkeys-lru`) | shared |
| Search inverted index | Term → postings KV | DynamoDB `jamex-search-index` | Search |
| CDN / colocation / ISP PoP | Google CDN + IXP | nginx `proxy_cache` | `edge` |
| Encoder job fan-out | (implicit) | SNS → SQS with DLQs | LocalStack |

Two rows are not one-to-one, and an interviewer will probe both:

- **Bigtable → DynamoDB.** Both are partitioned, key-ordered, high-throughput
  key-value stores with no joins. Bigtable's row key ↔ DynamoDB's partition +
  sort key. The meaningful difference: Bigtable rows are lexicographically
  ordered *globally*, so scans across the keyspace are natural and hot *ranges*
  are the hazard; DynamoDB hashes the partition key, so ordering exists only
  within a partition and hot *keys* are the hazard. Both push you to the same
  answer for view counts — shard the counter.
- **MySQL/Vitess → PostgreSQL.** Vitess exists to shard MySQL while preserving a
  single logical database. Postgres has no in-the-box equivalent, so sharding
  here stays a design discussion. See §9.

---

## 3. Why seven services

The build started as a modular monolith — one API plus a background worker — and
was deliberately decomposed. The reasoning matters more than the outcome,
because "why did you split it there?" is the question that follows.

### The rule that makes it a service architecture

**Exactly one service may read or write a given store.** Everyone else goes
through its API or reacts to its events.

The moment two services share a table they can no longer be deployed, migrated
or scaled independently — you have a *distributed monolith*: all the operational
cost of distribution, none of the benefit. That single rule is what the whole
decomposition is built to preserve, and it is why the three Postgres databases
are separate databases rather than three schemas.

### Where the seams are, and why

| Service | Split because… |
|---|---|
| **Gateway** | One origin for the client, one place to authenticate, one place to aggregate. Services can be renamed, moved or split without the frontend changing. |
| **Identity** | Strong consistency, low volume, different compliance surface. Chapter 2 explicitly decouples user data from video metadata. |
| **Catalog** | Read-heavy, eventually consistent, grows with uploads. The relational system of record for what a video *is*. |
| **Ingest** | Bandwidth-bound and spiky. Scales on upload rate. Isolating it means an upload flood cannot degrade playback. |
| **Encoder** | CPU-bound, scales on **queue depth**, not request rate. Completely different signal from everything else — the single most important boundary in the system. |
| **Engagement** | Extreme write volume with hot-partition problems. Isolating it means a viral video's counter cannot degrade search or playback. |
| **Search** | Different query engine with a different scaling curve. Swappable for OpenSearch behind the same API. Rebuilds purely from events, so it can be dropped and replayed. |

Notice the pattern: **the seams follow scaling characteristics, not nouns.**
Splitting by entity ("a Video service, a User service, a Comment service") is the
classic mistake — it produces services that must call each other constantly. The
useful question is "what scales on a different signal?".

### What it costs

Being able to state the cost matters as much as the benefit:

- Seven containers instead of two; seven deployments, seven sets of logs.
- Eventual consistency *between* services, on top of the eventual consistency
  already inside them. A video is uploaded before Catalog knows it exists.
- Debugging requires distributed tracing, because a single user action now
  crosses four processes.
- **The dual-write problem.** A service that commits to its database and then
  publishes an event can crash in between, losing the event permanently. The fix
  is the *transactional outbox*: write the event into an `outbox` table inside
  the same transaction, and relay it to SNS separately. Currently unimplemented
  and flagged in `PROGRESS.md` — know it is a gap, and know the fix.

---

## 4. Repository layout

```
App/
├── README.md            ← this file: end-to-end documentation
├── CLAUDE.md            ← project context and conventions
├── PROGRESS.md          ← live build state; what is next
├── JameX.slnx           ← .NET 10 XML solution format
├── docker-compose.yml
├── infra/
│   ├── docker/          Service.Dockerfile (parameterised), Encoder.Dockerfile
│   ├── localstack/init/ 01-bootstrap.sh — buckets, topic, queues, tables
│   ├── postgres/init/   01-create-databases.sql — one DB per owning service
│   └── edge/            nginx.conf — the CDN cache tier
├── src/
│   ├── shared/
│   │   ├── JameX.Contracts/       events + DTOs; no infrastructure deps
│   │   └── JameX.ServiceDefaults/ AWS clients, publisher, consumer, health
│   └── services/
│       ├── JameX.Gateway/     JameX.Identity/   JameX.Catalog/
│       ├── JameX.Ingest/      JameX.Encoder/    JameX.Engagement/
│       └── JameX.Search/
└── web/                 Next.js frontend (phase 6)
```

`JameX.Contracts` holds **only what crosses a service boundary** — event schemas
and public DTOs. Entities stay private to their owning service. If a type is in
Contracts, changing it is a breaking change to somebody else.

---

## 5. Getting it running

### Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.201 | |
| Docker Desktop | 29.x, Compose v5 | Must be **running** before compose |
| Node.js | 24 LTS | Frontend only, from phase 6 |
| FFmpeg | — | Not needed on the host; baked into the encoder image |

### Start everything

```bash
cd "C:/System Design/Youtube/App"

docker compose up -d --build
docker compose ps
```

LocalStack reports healthy only once `infra/localstack/init/01-bootstrap.sh`
finishes, so a healthy LocalStack guarantees every bucket, queue, topic and
table exists before any service starts.

### Build the solution

```bash
dotnet build JameX.slnx
```

### Ports

| Port | Service | |
|---|---|---|
| 3000 | Next.js frontend | phase 6 |
| 8080 | **Gateway** | the only port the browser needs |
| 8081 | Identity | |
| 8082 | Catalog | |
| 8083 | Ingest | |
| 8084 | Engagement | |
| 8085 | Search | |
| 8086 | Encoder | health only; it serves no traffic |
| 8090 | Edge cache (CDN stand-in) | |
| 4566 | LocalStack (S3, SQS, SNS, DynamoDB) | |
| 5432 | PostgreSQL | |
| 6379 | Redis | |

Every service exposes `/health/live`, `/health/ready` and — in Development —
`/scalar` for interactive API docs.

### Tearing down

```bash
docker compose down          # stop, keep the Postgres volume
docker compose down -v       # wipe all data, forcing re-provisioning
```

---

## 6. Phase 1 — the local AWS environment

Phase 1 built the substrate: every storage, queueing and delivery primitive the
rest of the system sits on.

### 6.1 Why LocalStack

LocalStack emulates AWS service APIs locally. Requests go through the genuine
**AWS SDK for .NET v4**; only the endpoint differs:

```csharp
new AmazonS3Config {
    ServiceURL = "http://localstack:4566",
    ForcePathStyle = true
}
```

Moving to a real AWS account means deleting the `ServiceURL` override. No shim
layer, no `if (isLocal)` branches, no second code path that drifts from
production. That property is the whole reason for choosing it over hand-written
fakes.

Three details that trip people up:

**Path-style addressing.** Real S3 prefers virtual-host style
(`https://bucket.s3.amazonaws.com/key`); LocalStack is addressed as
`http://localhost:4566/bucket/key`. `ForcePathStyle = true` selects the latter.

**Presigned URL hostnames.** A presigned URL is signed *including its host*. The
services reach LocalStack at `http://localstack:4566`, but the browser must use
`http://localhost:4566` — a URL signed for the first host is useless to the
browser. Hence two configuration values and two S3 clients:

```yaml
Aws__ServiceUrl:       "http://localstack:4566"   # server-to-server
Aws__PublicServiceUrl: "http://localhost:4566"    # URLs given to a browser
```

`AwsClientFactory` registers the second as a *keyed* singleton so a caller
cannot accidentally presign with the wrong one. The production equivalent is
signing for the same public domain the client will actually call.

**SDK v4 config.** Setting both `ServiceURL` and `RegionEndpoint` on a client
config throws. When a custom endpoint is set, supply `AuthenticationRegion`
instead — that is the region the signature needs.

**Free tier covers S3, SQS, SNS and DynamoDB** — everything this build uses.

---

### 6.2 S3: two buckets, two jobs

```
jamex-raw     uploads/{videoId}/source.{ext}          transient, enormous
jamex-media   videos/{videoId}/master.m3u8            permanent, CDN-facing
              videos/{videoId}/{rendition}/seg_*.ts
              videos/{videoId}/thumbs/{n}.jpg
```

**Why two buckets rather than two prefixes?** Opposite lifecycles, access
patterns and blast radii. Raw originals are written once, read once by an
encoder, then dead weight — at 120 MB per minute of content they accumulate
~3.6 TB/min at the doc's ingest rate. Encoded renditions are written once, read
millions of times, and must be publicly readable through a CDN. Separate buckets
allow different lifecycle rules, CORS, encryption and bucket policies — and make
it impossible to expose a raw original by loosening one policy.

**CORS on `jamex-raw`** exists because the browser PUTs parts directly to S3,
bypassing the services entirely. The critical line:

```json
"ExposeHeaders": ["ETag", "x-amz-request-id"]
```

Completing a multipart upload requires echoing back the `ETag` of every part.
Browsers cannot read a response header absent from
`Access-Control-Expose-Headers`, so without this the upload can never complete —
and the error gives you no hint why. This is *the* classic direct-to-S3 bug.

**Lifecycle rules**

```json
{"ID": "abandoned-multipart-uploads", "AbortIncompleteMultipartUpload": {"DaysAfterInitiation": 1}}
{"ID": "expire-raw-after-encode",     "Expiration": {"Days": 30}, "Filter": {"Prefix": "uploads/"}}
```

The first is one every AWS engineer should have reflexes about: parts of an
abandoned multipart upload are invisible in the console and to `ListObjects`,
but **you are billed for them forever**. Uploads get abandoned constantly — a
tab closes, a phone loses signal — so at ingest scale this is a silent unbounded
cost leak. In production the second rule would transition to Glacier Deep
Archive rather than delete, so a re-encode is possible when a new codec arrives.

---

### 6.3 DynamoDB: the Bigtable stand-in

Five tables, each owned by exactly one service. Key design is the whole game in
DynamoDB — the access pattern comes first and the schema is derived from it,
which is the inverse of relational modelling.

#### `jamex-video-counters` — sharded counters *(owner: Engagement)*

```
PK  videoId     "8f3c…"
SK  counterKey  "VIEWS#0" … "VIEWS#N" | "LIKES" | "DISLIKES" | "COMMENTS"
```

Views are the hottest write in the system. A single DynamoDB partition sustains
roughly **1,000 writes per second**; a viral video generates far more. One item
per video means one partition key, means throttling no matter how much capacity
you provision — the classic **hot partition** problem.

The fix is a scatter-gather counter: writes pick a random shard
(`VIEWS#{random(0,N)}`) and use `UpdateItem … ADD` for an atomic increment; reads
`Query` the partition and sum. Writes scale linearly with N; reads cost one query
over a handful of items. You trade exact-at-any-instant reads for write
throughput — fine, because nobody needs a view count accurate to the individual
view, and YouTube visibly does not provide one.

Note *where* this sits: counters do **not** live in PostgreSQL. A row-level
`UPDATE videos SET views = views + 1` takes a row lock, and every viewer of a
popular video would serialise behind it.

#### `jamex-user-reactions` — idempotent like/dislike *(owner: Engagement)*

```
PK  userId      SK  videoId      → kind (Like|Dislike), createdAt
GSI by-video:  PK videoId, SK userId
```

The doc's `likeDislike(user_id, video_id, like)` API is a *toggle*, and a naive
implementation double-counts on retry or double click. Storing the reaction makes
the correct counter delta computable from the transition:

| Previous | New | Δlikes | Δdislikes |
|---|---|---|---|
| none | Like | +1 | 0 |
| Like | Like | 0 | 0 |
| Like | Dislike | −1 | +1 |
| Like | none | −1 | 0 |

The base table answers "did *I* react to this?" — the per-user read on every
watch page. The GSI answers "who reacted to this video?", a different access
pattern needing a different key order. In DynamoDB you cannot query what you did
not model, so the GSI is a design decision taken up front.

#### `jamex-thumbnails` — the doc's literal Bigtable case *(owner: Catalog)*

```
PK  videoId    SK  thumbnailId ("0001")   → s3Key, width, height, offsetSeconds, isPoster
```

Chapter 3 singles this out: many small records per video, enormous read volume,
no joins. Images live in S3; only *references* live here. Storing binaries in a
KV store wastes the throughput you pay for, and thumbnails must be CDN-cacheable
by URL — which means they must be objects.

#### `jamex-search-index` — the inverted index, as specified *(owner: Search)*

```
PK  term ("guitar")   SK  videoId   → frequency, field (title|description|tags), indexedAt
```

Chapter 3 describes this exactly: key is the search term, value carries the
keyword's frequency and location across documents. One term is a single partition
read; multi-term queries fan out and intersect.

Being honest about the limits earns the credit: no stemming, no fuzzy matching,
no relevance model beyond frequency, and common terms create hot partitions of
their own. Real search is an entire product. Phase 5 ships this **and** Postgres
full-text search so the trade-off is demonstrable rather than asserted.

#### `jamex-upload-sessions` — resumable upload state *(owner: Ingest)*

```
PK  uploadId   → videoId, bucket, key, s3UploadId, parts[], expiresAt (TTL)
```

Chapter 3 notes uploads are split into parts with server-side state so a failure
can resume. `expiresAt` uses DynamoDB TTL so abandoned sessions delete
themselves — no cleanup job to write, operate and get paged for. It pairs with
the bucket's `AbortIncompleteMultipartUpload` rule: TTL reaps the bookkeeping,
the lifecycle rule reaps the bytes.

**All five tables are `PAY_PER_REQUEST`.** Right for bursty local traffic. In
production, counters would move to provisioned capacity with autoscaling once
the floor is understood, because on-demand costs roughly 6–7× more per request
at steady high volume.

---

### 6.4 PostgreSQL: one database per owning service

```sql
CREATE DATABASE jamex_users      OWNER jamex;   -- Identity
CREATE DATABASE jamex_catalog    OWNER jamex;   -- Catalog
CREATE DATABASE jamex_engagement OWNER jamex;   -- Engagement
```

| | `jamex_users` | `jamex_catalog` | `jamex_engagement` |
|---|---|---|---|
| Consistency | Strong | Eventual acceptable | Eventual acceptable |
| Read volume | Moderate | Enormous | High |
| Write pattern | Low, transactional | High, append-heavy | Very high |
| Growth | With users | With uploads | With engagement |
| Blast radius | Login and identity | Playback metadata | Comments only |

Three databases on one server locally; three clusters in production. The
constraint that makes that swap free is that **no query ever joins across
them** — identity is carried as an ID and composition happens at the Gateway.
Allow one join and the split is gone forever, so it must be enforced from the
first line of code.

`pg_trgm` is enabled for trigram similarity, used later by search and by the
near-duplicate detection from chapter 4.

---

### 6.5 Redis: the distributed cache

```yaml
command: ["redis-server", "--maxmemory", "256mb", "--maxmemory-policy", "allkeys-lru"]
```

Chapter 4 specifies Memcached with LRU, reasoning that LRU suits video's
long-tail access pattern. `allkeys-lru` is that policy: at the memory ceiling,
evict least-recently-used keys regardless of whether a TTL was set.

Why LRU is right here: video popularity is extremely skewed — a small hot set
serves most requests and the long tail is requested rarely. LRU keeps the hot set
resident and lets the tail fall out. The failure mode worth naming is a large
sequential scan (a crawler walking the catalogue) evicting the hot set, which is
what makes LFU or a segmented cache worth mentioning as an alternative.

Redis over Memcached because sorted sets give popularity tracking for free — a
ZSET keyed by video with view counts as scores directly implements the
hot/warm/cold tiering that decides what gets pushed to the edge in chapter 5.

Connections are configured with `AbortOnConnectFail = false`: a service must
never fail to start because the cache is unreachable. A cache outage should
degrade latency, not availability.

---

### 6.6 nginx: the CDN / PoP tier

The most-cut corner in system design practice is treating the CDN as a box on a
diagram. Here it is a real caching reverse proxy you can watch work.

```nginx
proxy_cache_path /var/cache/nginx/media levels=1:2 keys_zone=media_cache:16m
                 max_size=2g inactive=24h use_temp_path=off;

add_header X-JameX-Cache $upstream_cache_status always;   # MISS | HIT | STALE
```

`/media/<key>` proxies to `s3://jamex-media/<key>` and caches the response.
Watch `X-JameX-Cache` in the network tab as the player pulls segments and you see
the cache fill in real time.

Three settings carry real design weight:

- **`proxy_cache_lock on`** — on a miss, only the first request goes to origin;
  the rest wait for it. Without this, a video going viral sends thousands of
  simultaneous requests for the same uncached segment straight through. That is
  a **cache stampede**, and it is how a CDN becomes a DDoS against your own
  storage.
- **`proxy_cache_use_stale error timeout updating http_500 http_502 http_503 http_504`** —
  if origin is failing, keep serving the stale copy rather than propagating the
  error. Chapter 4's availability trade-off, made concrete: a slightly stale
  segment plays fine; a 503 stops the video.
- **`proxy_cache_valid 200 206 24h`** — 206 matters. Video is fetched with HTTP
  Range requests, so partial responses must be cacheable too.

HLS segments are immutable once written, which makes a 24h TTL safe. Manifests
are the mutable part and get a short TTL. The general rule for media delivery:
*long-cache the segments, short-cache the manifest.*

---

## 7. Phase 2 — the service architecture

Phase 2 turned one API plus a worker into seven services with an event bus.

### 7.1 `JameX.Contracts` — what crosses a boundary

Events and public DTOs only. No infrastructure dependencies, no entities.

```
Enums.cs                VideoStatus, VideoPrivacy, ReactionKind, PopularityTier
Events/VideoEvents.cs   EventTypes, EventEnvelope<T>, the four events
Dtos/VideoDtos.cs       VideoSummary, VideoDetail, EngagementCounts, …
Dtos/UploadDtos.cs      the resumable multipart upload contract
```

Two decisions worth defending:

**Enum values are numeric on the wire, and their numbers are part of the
contract.** Append new members; never renumber. A consumer built against an
older contract then still deserializes known members instead of throwing on an
unrecognised string.

**Events carry data, not pointers.** `VideoUploaded` repeats the title, tags and
privacy the uploader supplied rather than telling Catalog to "look it up".
Ingest does not own that data and Catalog cannot query Ingest's database, so the
event must carry everything a consumer needs to act. The alternative — a thin
event plus a callback — reintroduces a synchronous dependency in exactly the
place you were trying to remove one, and means the consumer fails whenever the
producer is down.

### 7.2 The event schema

```csharp
EventEnvelope<T>(Guid EventId, string EventType, DateTimeOffset OccurredAt,
                 string Source, T Data)
```

`EventId` is what makes consumers idempotent. SNS→SQS is at-least-once, and a
handler that runs longer than the visibility timeout gets delivered to a second
consumer while the first is still working. `Source` labels the producing service
for tracing.

| Event | Produced by | Consumed by | Meaning |
|---|---|---|---|
| `VideoUploaded` | Ingest | Encoder, Catalog | Raw file is in S3 and complete |
| `VideoEncoded` | Encoder | Catalog, Search, Engagement | Ladder + thumbnails are servable |
| `VideoEncodingFailed` | Encoder | Catalog | Retries exhausted; surface a real error |
| `VideoDeleted` | Catalog | Search, Engagement | Drop all derived state |

Note the direction of `VideoDeleted`: Catalog owns the video lifecycle, so
deletion originates there, and every service holding *derived* state reacts. No
service asks another for permission.

### 7.3 SNS + SQS: one topic, filtered fan-out

```
              ┌──────────────────────────────┐
Ingest ──────▶│  SNS  jamex-video-events     │
Encoder ─────▶│                              │
Catalog ─────▶└──────┬───────────────────────┘
                     │  filter policy per subscription
      ┌──────────────┼──────────────┬──────────────┐
      ▼              ▼              ▼              ▼
 encoder-jobs   catalog-events  search-events  engagement-events
 [VideoUploaded] [Uploaded,     [Encoded,      [Encoded,
                  Encoded,       Deleted]       Deleted]
                  Failed]
      │              │              │              │
      └──── each with a DLQ after 3 failed receives ────┘
```

**Why one topic rather than one per event type?** Adding a consumer becomes a
subscription with a filter policy — no producer changes, no new topic, no
redeploy of anything upstream. Producers stay ignorant of who listens, which is
the entire point of publish/subscribe.

**Why filter policies matter.** Without them, every queue receives every event
and discards what it does not handle. You pay for the delivery, the receive, the
delete and the consumer wakeup each time. With them, `Encoder` never even sees a
`VideoEncoded`. Verified in §8.

**Raw message delivery.** Subscriptions set `RawMessageDelivery=true`, so the SQS
body *is* the published message rather than an SNS notification wrapper, and
message attributes pass through. Consumers then read one shape whether a message
arrived via SNS or was put on the queue directly. `SqsEventConsumerService` still
handles the wrapped form defensively, because forgetting this flag is a very
common and very confusing bug.

**Queue access policy.** Each queue grants `sqs:SendMessage` to
`sns.amazonaws.com`, conditioned on `aws:SourceArn` equalling this topic. Without
the grant SNS cannot deliver; without the condition any topic in the account
could publish into your queue.

**Per-queue tuning**

| Attribute | Encoder | Others | Reasoning |
|---|---|---|---|
| `VisibilityTimeout` | 900s | 60s | Transcoding is slow; metadata updates are not |
| `ReceiveMessageWaitTimeSeconds` | 20 | 20 | Long polling: no empty-receive cost, near-zero pickup latency |
| `MessageRetentionPeriod` | 4 days | 4 days | Room to fix a broken consumer and replay |
| `RedrivePolicy` | 3 → DLQ | 3 → DLQ | One poison message must not starve the queue |

### 7.4 `JameX.ServiceDefaults` — the shared plumbing

Seven services would otherwise each reimplement the same wiring, and drift.

| File | What it solves |
|---|---|
| `Configuration/JameXOptions.cs` | `AwsOptions`, `StorageOptions` (owns every S3 key format, so key layout is defined once), `MessagingOptions` |
| `Aws/AwsClientFactory.cs` | Singleton AWS clients + the keyed presigning client; SDK v4 endpoint/region handling; default credential chain in real AWS |
| `Messaging/SnsEventPublisher.cs` | Wraps payload in an envelope, sets the `eventType` attribute, caches the topic ARN |
| `Messaging/SqsEventConsumerService.cs` | The consumer loop — long poll, dispatch, delete on success only, visibility heartbeat |
| `Messaging/RedisEventDeduplicator.cs` | Best-effort duplicate filter, with the inbox pattern documented as the durable alternative |
| `Hosting/JameXHostingExtensions.cs` | One-line service setup, health endpoints, OpenAPI/Scalar, CORS, `ICurrentUser` |

A service host is now this short:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddJameXServiceDefaults("Catalog");
builder.AddJameXApiDefaults();
builder.Services.AddJameXEventConsumer();

var app = builder.Build();
app.UseCors();
app.MapJameXDefaultEndpoints("Catalog");
app.Run();
```

**Clients are singletons.** AWS SDK clients are thread-safe and hold connection
pools; constructing them per request is a known cause of socket exhaustion.

**Liveness and readiness are different endpoints, deliberately.** `/health/live`
never touches a dependency — if it did, a database blip would restart every
healthy service that talks to it. `/health/ready` may check dependencies, because
failing it removes the instance from the load balancer instead of killing it.
Conflating the two is one of the most common production outages in Kubernetes.

### 7.5 The consumer loop, in detail

`SqsEventConsumerService` is where the interesting failure semantics live.

**Delete only on success.** A handler that throws leaves the message undeleted;
it becomes visible again after the timeout, is retried, and after
`maxReceiveCount` moves to the DLQ. Deleting before handling — or catching and
swallowing — silently destroys data.

**Visibility heartbeat.** A `VisibilityHeartbeat` extends the message's
invisibility on a timer (every third of the window) while a handler runs. The
alternative is one enormous visibility timeout, which also delays recovery when a
consumer *dies* mid-message — the queue must then wait the full timeout before
anyone else can pick the work up.

**The loop never dies.** A transient SQS or network fault is caught, logged and
backed off. An unhandled exception escaping the loop would take the consumer
offline until someone restarted the container.

**Unroutable messages are deleted, not retried.** A message with no `eventType`,
or one whose type has no registered handler, cannot ever succeed — retrying it
three times just burns receives. It is logged at warning level, because it means
the filter policy and the code have drifted apart.

### 7.6 The Gateway

YARP, configured from `appsettings.json` so the topology is data rather than code.

```
/api/identity/**                  → Identity
/api/channels/**                  → Identity
/api/uploads/**                   → Ingest
/api/search/**                    → Search
/api/videos/{id}/comments/**      → Engagement
/api/videos/{id}/reactions/**     → Engagement
/api/videos/{id}/(views|counts)   → Engagement
/api/videos/**                    → Catalog
```

The client sees **one** resource hierarchy under `/api/videos` even though two
services back it — Catalog owns what a video *is*, Engagement owns what people
*did* to it. Literal path segments outrank a catch-all in ASP.NET routing
precedence, so the three specific routes win over the Catalog catch-all without
needing explicit ordering. Service ownership stays clean and the URL stays
REST-shaped.

Destinations are overridden per environment: `localhost:808x` in
`appsettings.json` for running services directly with `dotnet run`, and
`http://catalog:8080/` via compose environment variables inside Docker.

### 7.7 Containers

`Service.Dockerfile` is parameterised by a `SERVICE` build argument, so adding a
service means adding a compose entry rather than another Dockerfile. Project
files are copied and restored *before* the source, so editing code does not
invalidate the slow package-restore layer.

`Encoder.Dockerfile` is the exception: it bakes FFmpeg into the runtime image,
because the transcode ladder shells out to it.

The entrypoint uses `exec` so `dotnet` remains PID 1 and receives `SIGTERM` —
which matters for a consumer that must finish its in-flight message before
shutting down.

---

## 8. Verification

Everything below was run and passed on 2026-08-07.

```bash
# 1. All eleven containers up
docker compose ps

# 2. Every service alive
for p in 8080 8081 8082 8083 8084 8085 8086; do
  curl -s http://localhost:$p/health/live; echo
done
# → {"status":"alive","service":"Gateway"} … Identity, Catalog, Ingest,
#   Engagement, Search, Encoder

# 3. Every consumer attached to its own queue
docker compose logs catalog search engagement encoder | grep consuming
# → Catalog consuming jamex-catalog-events (long poll 20s, batch 10)
#   Search consuming jamex-search-events …
#   Engagement consuming jamex-engagement-events …
#   Encoder consuming jamex-encoder-jobs …

# 4. Gateway routes to every backend
#    404 = reached the service; 502/503 = destination unreachable
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/api/videos/x
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8080/api/uploads/x
# → 404, 404
```

**The filter policies genuinely filter** — the check that matters most:

```bash
TOPIC=arn:aws:sns:us-east-1:000000000000:jamex-video-events

docker exec jamex-localstack awslocal sns publish --topic-arn $TOPIC \
  --message '{"eventType":"VideoUploaded","data":{}}' \
  --message-attributes '{"eventType":{"DataType":"String","StringValue":"VideoUploaded"}}'

docker exec jamex-localstack awslocal sns publish --topic-arn $TOPIC \
  --message '{"eventType":"VideoEncoded","data":{}}' \
  --message-attributes '{"eventType":{"DataType":"String","StringValue":"VideoEncoded"}}'
```

Observed queue depths:

| Queue | Subscribes to | Received | Correct? |
|---|---|---|---|
| `jamex-encoder-jobs` | VideoUploaded | 1 | ✅ did **not** get VideoEncoded |
| `jamex-catalog-events` | Uploaded, Encoded, Failed | 2 | ✅ got both |
| `jamex-search-events` | Encoded, Deleted | 1 | ✅ did **not** get VideoUploaded |
| `jamex-engagement-events` | Encoded, Deleted | 1 | ✅ |

And the full path through to dispatch, with no handlers registered yet:

```
Catalog has no handler for VideoEncoded; discarding.
Search has no handler for VideoEncoded; discarding.
Engagement has no handler for VideoEncoded; discarding.
```

Three services, one publish, correct routing, message consumed and acknowledged.
The bus works end to end.

**Infrastructure checks from phase 1**

```bash
docker exec jamex-localstack awslocal s3 ls                 # 2 buckets
docker exec jamex-localstack awslocal sqs list-queues       # 4 queues + 4 DLQs
docker exec jamex-localstack awslocal dynamodb list-tables  # 5 tables
docker exec jamex-postgres psql -U jamex -d postgres \
  -tAc "select datname from pg_database where datname like 'jamex%'"
# → jamex, jamex_catalog, jamex_engagement, jamex_users

# CDN caches — run twice, watch the header change
curl -sI http://localhost:8090/media/probe/t.m3u8 | grep X-JameX-Cache   # MISS
curl -sI http://localhost:8090/media/probe/t.m3u8 | grep X-JameX-Cache   # HIT
```

---

## 9. Interview talking points

Rehearse these aloud. Each is answerable from what is actually built.

**"How do you handle the write volume on view counts?"**
Sharded counters in DynamoDB. One item per video is a hot partition capped near
1,000 writes/sec; writes scatter across N shard keys and reads gather and sum.
You trade instantaneous exactness for linear write scaling — acceptable, because
a view count is a display value, not a ledger.

**"Why not keep everything in one database?"**
Three data shapes, three requirements. Relational metadata needs joins, filters
and ordering. Counters need extreme write throughput and no joins. Video bytes
need petabyte-scale storage and CDN-addressable URLs. Forcing all three into
Postgres means counters lock rows and blobs blow out the storage budget by orders
of magnitude.

**"Why did you split the services where you did?"**
By scaling signal, not by noun. Ingest scales on upload bandwidth; Encoder on
queue depth; Engagement on write volume; Catalog on read volume. Splitting by
entity produces services that must call each other constantly. The test I applied
was: does this scale on a different signal, and does it own data nobody else
needs to write?

**"What stops this being a distributed monolith?"**
One rule: exactly one service reads or writes a given store. Three separate
Postgres databases, not three schemas. If a service needs data it does not own it
calls the owner's API or reacts to its events. The moment two services share a
table, independent deployment is gone.

**"What happens when a video fails to encode?"**
Three receives, then the DLQ, message retained four days. The DLQ stops one
poison video starving the queue. Encoder also publishes `VideoEncodingFailed` so
Catalog can show the uploader a real error rather than leaving the video stuck in
Transcoding forever. You alarm on DLQ depth, inspect, fix, redrive.

**"Your consumer takes longer than the visibility timeout. What breaks?"**
The message becomes visible again and a second consumer starts the same work. So
consumers must be idempotent — SQS is at-least-once, never exactly-once. Long
handlers heartbeat by extending visibility rather than relying on one huge
timeout, because a huge timeout also delays recovery when a consumer dies.

**"You commit to your database and then publish an event. What if you crash in
between?"**
That is the dual-write problem, and it currently exists in this build. The event
is lost and the system is permanently inconsistent. The fix is the transactional
outbox: write the event into an `outbox` table in the same transaction as the
business change, then relay it to SNS from a separate process. The relay is
at-least-once, which is fine because consumers are idempotent.

**"Strong or eventual consistency?"**
Both, split by data class. Eventual for video metadata and counts, because a few
seconds of staleness is invisible and availability matters more. Strong for user
data, which is why Identity is a separate service with a separate database.
Naming the split and pointing at the schema beats reciting CAP.

**"How would you scale the metadata database?"**
Vertical scaling ends, then read replicas absorb reads but not writes, then
sharding is unavoidable. Sharding by hand pushes routing into application code
and breaks cross-shard ACID. Vitess exists precisely to keep a single logical
MySQL interface over a sharded fleet — YouTube built it for this problem. The
shard key would be `channelId`, so a channel's videos stay co-located and "list a
channel's videos" hits one shard. The doc explicitly rejects denormalisation as
the alternative, because it degrades write performance exactly when write volume
is the problem.

**"How do you stop the CDN hammering origin?"**
`proxy_cache_lock`. On a miss, one request fills the cache and the rest wait —
otherwise a viral video's first seconds become a self-inflicted DDoS. Pair it
with serve-stale-on-error so an origin blip degrades quality instead of stopping
playback.

**"Why direct-to-S3 upload instead of through the API?"**
A 600 MB raw upload through the application tier occupies a request thread for
minutes, needs disk or memory to buffer, and makes the service the bottleneck at
480 Gbps ingest. Presigned multipart URLs let the browser write straight to S3;
the service only issues credentials and records state. Resume comes free, because
parts are independently retryable.

**"Why one SNS topic instead of a topic per event type?"**
Adding a consumer becomes a subscription with a filter policy — no producer
change, no new topic, no upstream redeploy. Producers stay ignorant of who
listens. Filter policies mean a consumer is not even woken for events it does not
handle.

---

## 10. Roadmap

| Phase | Scope | Status |
|---|---|---|
| **1** | Local AWS substrate: S3, SQS, DynamoDB, Postgres split, Redis, edge cache | ✅ **Done, verified** |
| **2** | Service decomposition, contracts, SNS/SQS event bus, gateway, shared plumbing | ✅ **Done, verified** |
| 3 | Identity + Catalog: EF Core models, migrations, REST APIs, event handlers | ⬜ Next |
| 4 | Ingest + Encoder: resumable multipart upload, FFmpeg ABR ladder, thumbnails | ⬜ |
| 5 | Engagement + Search: sharded counters, reactions, comments, inverted index | ⬜ |
| 6 | Gateway BFF aggregation + Next.js frontend with hls.js adaptive player | ⬜ |
| 7 | `DESIGN.md` — doc-to-code mapping and interview question bank | ⬜ |

Stretch goals once the pipeline is end to end: transactional outbox for Catalog,
per-shot encoding (chapter 5), duplicate detection via perceptual hashing / LSH
(chapter 4), and the two-stage candidate-generation-plus-ranking recommender.

`PROGRESS.md` holds the live build state and is updated every session.
