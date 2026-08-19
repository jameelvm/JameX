# JameX

A working YouTube clone, built to learn the system design behind it well enough
to defend it in an interview.

The specification is the five-chapter design document in the parent folder
(`1.System Design_ YouTube.pdf` … `5.The Reality Is More Complicated.pdf`). Every
component that document describes has a real, runnable counterpart here.

**Stack:** .NET 10 · PostgreSQL · DynamoDB · S3 · SQS · SNS · Redis · FFmpeg ·
YARP · Next.js · Docker Compose + LocalStack

**Build status:** phases 1–3 complete and verified. Infrastructure, the service
skeleton, and the Identity and Catalog services all run — including the inbox
and outbox patterns that make the event bus safe. Upload and playback arrive in
phase 4.

---

## Table of contents

1. [The problem being solved](#1-the-problem-being-solved)
2. [Architecture](#2-architecture)
3. [Why seven services](#3-why-seven-services)
4. [Repository layout](#4-repository-layout)
5. [Getting it running](#5-getting-it-running)
6. [Phase 1 — the local AWS environment](#6-phase-1--the-local-aws-environment)
7. [Phase 2 — the service architecture](#7-phase-2--the-service-architecture)
8. [Phase 3 — Identity and Catalog](#8-phase-3--identity-and-catalog)
9. [Verification](#9-verification)
10. [Interview talking points](#10-interview-talking-points)
11. [Roadmap](#11-roadmap)

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
├── DESIGN.md            ← system design summary + interview question bank
├── TABLES-WALKTHROUGH.md ← plain-language tour of the Catalog tables
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

Inside a service that owns data, the folders name one concern each:

```
JameX.Catalog/
├── Api/            controllers — routing and status codes only
├── Services/       application logic — returns OperationResult<T>, no HTTP
├── Repositories/   data access — EF and Npgsql stop here; never saves
├── Domain/         entities
├── EventHandlers/  one per subscribed event type
├── Caching/        cache-aside over Redis
├── Contracts/      inbound request records (private to this service)
├── Mapping/        entity → DTO, and object key → CDN URL
├── Validation/     input rules and limits
└── Data/           DbContext, design-time factory, Migrations/
```

`JameX.Contracts` holds **only what crosses a service boundary** — event schemas
and public DTOs. Entities stay private to their owning service. If a type is in
Contracts, changing it is a breaking change to somebody else. Note the
distinction from a service's own `Contracts/` folder, which is inbound-only and
private.

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

## 8. Phase 3 — Identity and Catalog

Phases 1 and 2 built infrastructure. Nothing did any work: seven services
answered `/health/live` and four consumers sat on empty queues.

Phase 3 makes two of them real. Identity owns accounts and channels; Catalog
owns video metadata and reacts to the events that describe a video's life. It is
also where the two patterns that make an at-least-once event bus survivable get
built for real — the **inbox** and the **outbox**.

### 8.1 What exists now

| | Identity | Catalog |
|---|---|---|
| Database | `jamex_users` | `jamex_catalog` |
| Tables | `users`, `channels` | `videos`, `renditions`, `processed_events`, `outbox_messages` |
| Endpoints | 8 | 6 |
| Event handlers | — | 3 |
| Publishes | — | `VideoDeleted`, via the outbox |
| Cache | — | Redis, on the watch page |

### 8.2 The layering inside a service

Every service is built in four layers, and the rule is that each one knows
nothing about the layer above it:

```
Api/            controllers — routing and status codes ONLY
  ↓
Services/       application logic — returns OperationResult<T>, never touches HTTP
  ↓
Repositories/   data access — intention-revealing; EF and Npgsql stop here
  ↓
Domain/         entities
```

Supporting folders: `Contracts/` (inbound request records), `Mapping/`,
`Validation/`, `Caching/`, `Data/`, `EventHandlers/`.

A controller action is one expression:

```csharp
[HttpPost]
public async Task<IActionResult> Create(CreateUserRequest request, CancellationToken ct) =>
    (await userService.CreateAsync(request, ct))
        .ToActionResult(user => Created($"/users/{user.UserId}", user));
```

**Why the service layer returns `OperationResult<T>` and not `IActionResult`.**
The same method has to be callable from an event handler, a background job or a
test with no `HttpContext` in sight. A service that returns `Results.Conflict()`
has quietly become an HTTP endpoint with extra steps. So it reports an *outcome*
— `Success` / `NotFound` / `Conflict` / `Invalid` / `Forbidden` — and one shared
extension maps that to a status code. That mapping living in exactly one place
is what stops two endpoints disagreeing about whether a situation is 400 or 404.

Exceptions were deliberately not used for this. A missing video is not
exceptional; it is Tuesday.

**Why repositories are not generic.** There is no `IRepository<T>` with
`Find(predicate)`. A generic repository over EF Core re-wraps `DbSet<T>` in a
worse `DbSet<T>`, and letting `IQueryable` escape through it lets callers
compose arbitrary queries — exactly the coupling the abstraction was meant to
prevent. Instead every method names a question the application actually asks
(`GetByHandleAsync`, `GetPublicFeedAsync`, `GetRenditionLabelsAsync`), and each
one corresponds to an index on the table.

**Repositories never call `SaveChangesAsync`.** That belongs to
`IUnitOfWork` / `IInboxUnitOfWork`, because the whole point of the inbox and
outbox is that a business change and its bookkeeping row commit *together*. A
repository that saved on its own would split one transaction into two and
destroy the guarantee.

The layering earned its keep immediately: the transport was swapped from minimal
APIs to MVC controllers mid-phase, and the service, repository and domain layers
did not change by a single line. Only two controller files and one mapping
method moved.

### 8.3 Identity — the store that is not allowed to lag

Chapter 2 puts user data in the strongly-consistent half of the design while
video metadata is allowed to be eventually consistent. That one sentence is the
entire reason Identity is a separate service with a separate database: each
store gets tuned for its own consistency requirement instead of the strictest
one being imposed on everything.

**UUIDv7 primary keys**, via `Guid.CreateVersion7()`. Both v4 and v7 are unique;
v7 embeds a timestamp in its high bits so successive ids sort in creation order.
As a primary key that means inserts land at the right-hand edge of the B-tree
instead of scattering across every page — far fewer page splits and a far better
cache hit rate on a table that only grows.

**Uniqueness lives in the index, not in code.** The tempting version is:

```csharp
if (await db.Users.AnyAsync(u => u.Email == email)) return Conflict();   // WRONG
```

That is a race. Two concurrent registrations both read "absent" and both insert;
the index rejects one of them anyway, but now it surfaces as an unhandled 500
instead of a 409. So the write is simply attempted, and SQLSTATE `23505` is
translated into a conflict:

```csharp
catch (DbUpdateException exception) when (exception.IsUniqueViolation(out _))
{
    db.Entry(user).State = EntityState.Detached;   // do not retry a doomed insert
    return false;
}
```

Correct, and one round trip cheaper.

**Batch endpoints exist for the Gateway.** `POST /users/batch` and
`POST /channels/batch` take up to 100 ids and answer in one query:

```sql
SELECT u.id, u.created_at, u.display_name, u.email FROM users AS u WHERE u.id = ANY (@ids)
```

A feed of fifty videos carries fifty channel ids. Without a batch route the
Gateway makes fifty HTTP calls — the N+1 problem, except each "+1" is now a
network round trip with its own latency and failure mode. Ids that do not exist
are simply absent from the response rather than a 404: a partial answer is the
useful answer, and one deleted account should not fail a whole page.

**Handles are resolved exactly once.** `GET /channels/by-handle/{handle}` turns
a public `@name` into an id. Handles are mutable, so nothing else in the system
stores one as a reference — every service speaks `ChannelId` only, and the
translation happens once, at the edge, when a URL arrives.

### 8.4 Catalog — a row assembled from events

The `videos` table has 27 columns, and they group by **which event writes them**:

| Group | Columns | Written by |
|---|---|---|
| Identity | `id`, `channel_id`, `uploader_id` | `VideoUploaded` |
| Metadata | `title`, `description`, `tags`, `privacy`, `status` | `VideoUploaded`, then the API |
| Upload facts | `raw_bucket`, `raw_object_key`, `size_bytes` | `VideoUploaded` |
| Playback facts | `master_playlist_key`, `duration_seconds`, … | `VideoEncoded` |
| Failure facts | `failure_reason`, `failure_stage`, `attempt_count` | `VideoEncodingFailed` |

The playback columns are **nullable by design**. Between upload and encode
completion a video genuinely has no duration and no master playlist, and the
schema should not pretend otherwise.

**`videos.id` is `ValueGeneratedNever()`.** Ingest mints the id before the bytes
finish arriving so the client can poll for progress, and the raw S3 object key
already embeds it. Catalog uses the id it is given; minting a second one would
orphan the file.

**The foreign-key contrast is the ownership rule made physical:**

| Reference | Foreign key? | Why |
|---|---|---|
| `renditions.video_id` → `videos.id` | ✅ yes, cascade | same database, owned by this service |
| `videos.channel_id` → `channels.id` | ❌ **impossible** | different database, owned by Identity |

The database can no longer refuse a video whose channel does not exist. The
service has to. That is the real cost of decomposition, and it is paid here in
exchange for Identity and Catalog scaling independently.

**Six indexes, each earning its place** — verified with `EXPLAIN`:

```sql
tags @> ARRAY['systemdesign']     → Bitmap Index Scan on ix_videos_tags        (GIN)
title ILIKE '%youtube%'           → Bitmap Index Scan on ix_videos_title_trgm  (trigram)
privacy=2 AND status=3 ORDER BY   → Index Scan using ix_videos_published       (partial)
```

The partial index is the most interesting. Its filter is
`WHERE privacy = 2 AND status = 3`, so the plan has **no filter and no recheck**
— every private, queued, transcoding and failed video is absent from the index
entirely rather than being scanned and discarded.

`tags` is a Postgres `text[]`, not a join table: tags are read with the video on
every request and never queried independently of it, so the join a separate
table would force on every read buys nothing — and GIN still answers "videos
tagged X" quickly.

### 8.5 A video's life, event by event

The clearest way to understand the schema is to follow one video. Ids are
shortened here for reading.

> For the long-form version — full event payloads, every column with a "where it
> came from" note, and the crash sequences that motivate the inbox and outbox —
> see **[`TABLES-WALKTHROUGH.md`](TABLES-WALKTHROUGH.md)**.

**Step 1 — Ingest announces a completed upload.**

```json
{ "eventId": "aaaa1111-…", "eventType": "VideoUploaded", "source": "Ingest",
  "data": { "videoId": "019fff10-…", "channelId": "019ffe64-…",
            "title": "How to Cook Pasta", "tags": ["cooking","pasta"],
            "privacy": 2, "rawObjectKey": "uploads/019fff10…/source.mp4" } }
```

Catalog's handler writes **two rows in one transaction** — the video, and the
inbox record saying it handled this message:

```
videos:            status=1 (Queued), privacy=2 (Public)
                   duration_seconds=NULL, master_playlist_key=NULL, published_at=NULL
processed_events:  event_id=aaaa1111-…
```

Eleven columns are NULL. That is not missing data — it is honest data. And
`published_at` is NULL *even though the video is public*, because "public" is
the uploader's intent while "published" means a viewer can actually press play.

**Step 2 — Encoder announces the ladder.**

```json
{ "eventId": "bbbb2222-…", "eventType": "VideoEncoded", "source": "Encoder",
  "data": { "videoId": "019fff10-…", "durationSeconds": 612.5,
            "masterPlaylistKey": "videos/019fff10…/master.m3u8",
            "renditions": [ { "label": "360p", … }, { "label": "720p", … }, { "label": "1080p", … } ] } }
```

Five writes, one transaction: the video is updated, three `renditions` rows are
inserted, and a second inbox row is written.

```
videos:      status=3 (Ready), duration_seconds=612.5,
             master_playlist_key set, published_at STAMPED NOW
renditions:  360p, 720p, 1080p
```

The moment `published_at` is set, the video enters the public feed — because the
partial index only holds rows that are both public and Ready.

**Step 3 — or it fails instead.**

```
videos:  status=4 (Failed), failure_reason='No audio track found in source file',
         failure_stage='probe', attempt_count=3, published_at STILL NULL
```

Those three failure columns are why the uploader sees a real error rather than a
spinner that never stops.

**Step 4 — the uploader deletes it.**

Two writes, one transaction: the video row is deleted (its renditions cascade
away), and a `VideoDeleted` event is written to `outbox_messages` with
`published_at = NULL`. A background relay sends it moments later.

### 8.6 The inbox — surviving at-least-once delivery

SQS guarantees each message is delivered **at least once**, never exactly once.
A message arrives twice when a handler runs longer than the visibility timeout,
when it succeeds but crashes before deleting the message, or when someone
redrives a dead-letter queue.

Some work is naturally safe to repeat — `status = Ready` applied twice is still
Ready. Some is not: `attempt_count + 1` twice, a comment inserted twice, or a
view counter incremented twice. The counter case is the dangerous one, because
nothing errors and nothing alerts — the number is simply wrong forever.

`processed_events` is four columns, and the **primary key is the whole
mechanism**:

```csharp
inbox.ClaimEvent(envelope);              // stages an INSERT into processed_events
video.Status = VideoStatus.Ready;        // stages the actual work
await inbox.TrySaveAsync(ct);            // ONE transaction — both, or neither
```

A redelivery tries to insert a duplicate primary key, the whole transaction
rolls back, and `TrySaveAsync` returns `false`. The handler treats that as
success and deletes the message.

**Why it must be in the same database.** A Redis-based check is a *separate*
system, so this can happen:

```
1. Redis: mark event as seen   ✅
2. …crash…
3. Postgres: apply the change   ❌ never ran
```

The event is now marked handled but the work never happened, and the retry gets
skipped. That converts "might run twice" into "might never run", which is
strictly worse. `RedisEventDeduplicator` still exists and is documented as a
best-effort filter for services with no relational store — Search and Encoder.
Catalog deliberately does not use it.

**Verified**: five messages sent to Catalog's queue — an upload, a duplicate
upload, an encode, a duplicate encode, and a replay of the encode under a new
event id. Result: `videos = 1`, `renditions = 3` (each label exactly once),
`processed_events = 3`. Both duplicates were rejected; the genuine replay was
applied but added no rows.

**Ordering is solved by retrying, not by sequencing.** If `VideoEncoded` is
processed before `VideoUploaded` — which happens, because a batch is processed
in parallel — the handler throws:

```
fail: Video 019fff20-… is not in the catalogue yet; retrying until VideoUploaded is applied.
      (receive #1); leaving it for retry
```

No row was written, and critically **no inbox claim survived either** — proof
that the rollback covers both writes. The message becomes visible again after
the visibility timeout, by which time the upload has landed.

**Ready always wins.** A late `VideoEncodingFailed` for a video that already
succeeded is ignored, because the encoder retries and the two results can arrive
out of order:

```
warn: Ignoring encoding failure for 019fff10-… — it is already Ready.
```

### 8.7 The outbox — closing the dual-write hole

Phase 2's README listed this as an unfixed weakness. It is now fixed.

The problem: committing a change and *then* publishing an event is two writes to
two systems with no transaction spanning them.

```
1. DELETE the video      → committed to Postgres ✅
2. …crash…
3. publish VideoDeleted  → never happens          ❌
```

The video is gone from Catalog, but Search lists it forever and Engagement keeps
its counters. Nothing can detect the drift.

The fix is to make step 2 part of step 1:

```csharp
videos.Remove(video);
outbox.Enqueue(EventTypes.VideoDeleted, new VideoDeleted(videoId, channelId, DateTimeOffset.UtcNow));
await unitOfWork.SaveChangesAsync(ct);   // ← one transaction
```

Caught mid-flight in testing, immediately after `DELETE` returned 204:

```
 video_rows | rendition_rows | outbox_rows
      0     |       0        |      1        ← published_at NULL
```

The video is gone, its renditions cascaded, and the announcement is durable but
unsent.

**The event id is minted once, at write time, and stored.** The relay publishes
those exact bytes — which is why `IEventPublisher` grew a
`PublishEnvelopeAsync` that does *not* build a new envelope. If it did, a retry
would carry a fresh `EventId`, every consumer's inbox would treat the resend as
a brand-new event, and the change would be applied twice with nothing able to
detect it. **A stable `EventId` is the link between the outbox and the inbox.**

**The relay claims rows with `FOR UPDATE SKIP LOCKED`:**

```sql
SELECT * FROM outbox_messages
WHERE published_at IS NULL AND attempt_count < 10
ORDER BY id LIMIT 20
FOR UPDATE SKIP LOCKED
```

This is what makes the dispatcher safe to run on every replica at once — each
locks the rows it takes and the others step *over* them rather than blocking.
Without it, three replicas publish the same batch three times.

**This is where `EnableRetryOnFailure` finally bites.** It installs an execution
strategy, and any code opening its own transaction must run through it — because
a retry has to replay the whole transaction, not resume it halfway:

```csharp
var strategy = db.Database.CreateExecutionStrategy();
return await strategy.ExecuteAsync(async () => { … BeginTransactionAsync … });
```

The trade the outbox makes is worth stating plainly: it converts *"the event
might vanish forever"* into *"the event might arrive twice"*. The first is
unfixable data corruption. The second is what the inbox already handles.

### 8.8 Cache-aside on the watch page

One video is watched by thousands of people, so `GET /videos/{id}` is cached in
Redis — the doc's Memcached tier.

```
1. Ask Redis           → hit? return, Postgres untouched
2. Miss → ask Postgres → not found? 404
3. Populate the cache  → only if status = Ready
4. Return
```

**Only settled rows are cached.** A `Queued` video changes again within minutes;
caching it just guarantees someone sees a stale watch page.

**Invalidate by delete, never overwrite, and always after the commit.**
Overwriting races — two concurrent updates can reach Redis in the opposite order
to Postgres, leaving the cache permanently wrong. Invalidating *before* the
commit leaves a window where a reader repopulates from the old row. Deleting
after means the worst case is one extra database read.

**Feeds are deliberately not cached.** A feed page has no precise invalidation
key: publishing one video shifts the contents of every page after it, so correct
invalidation would mean dropping the entire feed on every publish. The rule
applied throughout is:

> Cache something only if you can name exactly which entry to delete when it
> changes.

Redis is capped at 256 MB with `allkeys-lru`, so the cache never needs a policy
for *which* videos to keep — least-recently-used discovers real popularity from
traffic. A 5-minute TTL bounds the damage from any missed invalidation.

The service also runs correctly with **no Redis at all**: `NullVideoCache` is
registered when no connection string is present, and every Redis call is wrapped
so a fault degrades to a database read. Losing a cache must cost latency, never
availability.

### 8.9 What phase 3 does not do

- **Nothing publishes `VideoUploaded`, `VideoEncoded` or `VideoEncodingFailed`
  yet.** Those come from Ingest and Encoder in phase 4. Phase 3's handlers were
  tested by publishing the events by hand — which is the point of an event bus:
  Catalog has no idea who produced the message.
- **Authorisation is uploader-only.** Catalog can check `uploader_id` because it
  owns that column, but it genuinely cannot verify channel ownership — that
  fact lives in Identity. In production the Gateway would resolve it once and
  forward a signed claim.
- **Engagement counts are zeros.** `VideoDetail.ChannelName`, `Counts` and
  `ViewerReaction` are left empty by Catalog on purpose; the Gateway overlays
  them from Identity and Engagement. Guessing them here would mean reading
  another service's data.
- **No optimistic concurrency yet.** Postgres' `xmin` works as a concurrency
  token with no schema change, so adding it later costs nothing.

---

## 9. Verification

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

### Phase 3 — Identity and Catalog

```bash
# --- Identity -------------------------------------------------------------
# Register, and prove the unique index rejects a case-different duplicate
curl -s -X POST localhost:8081/users -H 'Content-Type: application/json' \
  -d '{"email":"Jameel@Example.COM","displayName":"Jameel"}'
# → 201, email stored lower-cased
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8081/users \
  -H 'Content-Type: application/json' \
  -d '{"email":"JAMEEL@example.com","displayName":"Impostor"}'
# → 409

USER=<the userId returned above>

# Channel creation takes its owner from the caller, never the body
curl -s -o /dev/null -w '%{http_code}\n' -X POST localhost:8081/channels \
  -H 'Content-Type: application/json' -d '{"name":"JameX","handle":"@JameX"}'
# → 401  (no X-JameX-User header)

curl -s -X POST localhost:8081/channels -H 'Content-Type: application/json' \
  -H "X-JameX-User: $USER" -d '{"name":"JameX Official","handle":"@JameX"}'
# → 201, handle normalised to "jamex"

# Handles resolve case-insensitively, with or without the @
curl -s localhost:8081/channels/by-handle/@JaMeX

# Batch lookup is ONE indexed query, and missing ids are simply absent
curl -s -X POST localhost:8081/users/batch -H 'Content-Type: application/json' \
  -d "{\"ids\":[\"$USER\",\"00000000-0000-0000-0000-0000000000ff\"]}"
# → one user returned, no 404

# --- Catalog: the event pipeline ------------------------------------------
Q=$(docker exec jamex-localstack awslocal sqs get-queue-url \
      --queue-name jamex-catalog-events --query QueueUrl --output text)

send() {   # send() <file> <eventType>
  docker exec jamex-localstack awslocal sqs send-message --queue-url "$Q" \
    --message-body "file://$1" \
    --message-attributes "{\"eventType\":{\"DataType\":\"String\",\"StringValue\":\"$2\"}}"
}

# Send an upload, the SAME upload again, an encode, the SAME encode again
send /tmp/uploaded.json VideoUploaded
send /tmp/uploaded.json VideoUploaded
send /tmp/encoded.json  VideoEncoded
send /tmp/encoded.json  VideoEncoded

docker exec jamex-postgres psql -U jamex -d jamex_catalog -c \
 "SELECT (SELECT count(*) FROM videos) videos,
         (SELECT count(*) FROM renditions) renditions,
         (SELECT count(*) FROM processed_events) inbox;"
# → videos=1  renditions=3  inbox=2      ← both duplicates rejected by the inbox

# --- Catalog: cache-aside -------------------------------------------------
curl -sI localhost:8082/videos/$VIDEO | grep X-JameX-Cache   # MISS
curl -sI localhost:8082/videos/$VIDEO | grep X-JameX-Cache   # HIT
docker exec jamex-redis redis-cli KEYS 'jamex:catalog:video:*'
docker exec jamex-redis redis-cli TTL  'jamex:catalog:video:<id>'   # → ~300

# --- Catalog: the outbox --------------------------------------------------
curl -s -o /dev/null -w '%{http_code}\n' -X DELETE localhost:8082/videos/$VIDEO \
  -H "X-JameX-User: $USER"
# → 204

# Immediately: the video is gone AND the announcement is durable but unsent
docker exec jamex-postgres psql -U jamex -d jamex_catalog -c \
 "SELECT (SELECT count(*) FROM videos) videos,
         (SELECT count(*) FROM renditions) renditions,
         (SELECT count(*) FROM outbox_messages WHERE published_at IS NULL) unsent;"
# → videos=0  renditions=0  unsent=1     ← one transaction did all three

# Within ~2s the relay drains it
docker exec jamex-postgres psql -U jamex -d jamex_catalog -c \
 "SELECT event_type, published_at, attempt_count FROM outbox_messages;"
# → VideoDeleted | 2026-… | 0
```

**Known local flakiness.** LocalStack's SNS→SQS fan-out is unreliable under
rapid repeated publishes — messages are sometimes delivered minutes late or
dropped. Two consequences worth knowing:

- Prefer `sqs send-message` directly to a queue when testing a *handler*; it
  removes SNS from the equation and tests exactly the code you care about.
- **Never run `sqs purge-queue`.** AWS documents that messages sent within ~60s
  of a purge may be deleted mid-purge; in LocalStack the queue did not recover
  at all and needed a container restart.

Also note that redirecting a service's stdout to a file block-buffers the log,
so `dotnet run > app.log` lags badly and only flushes on exit. **The database is
the reliable source of truth when verifying handlers.**

---

## 10. Interview talking points

Rehearse these aloud. Each is answerable from what is actually built.

> **[`DESIGN.md`](DESIGN.md)** holds the fuller version — the decision register,
> a failure-mode table, and the question bank grouped by theme. Use this section
> for a quick pass and `DESIGN.md` before an interview.

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
That is the dual-write problem, and it is solved here with the transactional
outbox. The event is written to `outbox_messages` inside the same transaction as
the business change, so the intention to publish is exactly as durable as the
change itself. A background relay drains unsent rows to SNS and stamps
`published_at`. It converts "the event might vanish forever" into "the event
might arrive twice" — and the second is already handled by the consumer's inbox.

**"How do you make an at-least-once consumer safe?"**
The inbox pattern. The handler inserts the event id into a `processed_events`
table in the *same transaction* as the change it applies, and the primary key
rejects redeliveries. The check and the effect commit or roll back together, so
there is no window where one happened without the other. A cache-based check
cannot do this — Redis is a separate system, so marking an event seen and then
crashing before the change commits turns "runs twice" into "never runs", which
is worse.

**"Two replicas both run your outbox relay. Don't they publish everything
twice?"**
No — the relay claims rows with `SELECT … FOR UPDATE SKIP LOCKED`. Each replica
locks the batch it takes and the others step over those rows instead of blocking
on them. That is also why the event id is generated once at write time and
stored: if the relay rebuilt the envelope per attempt, a retry would carry a new
id and every consumer's inbox would treat it as a new event.

**"An event arrives before the one it depends on. How do you order them?"**
Usually you don't. `VideoEncoded` for a video Catalog has not created yet simply
throws; the message is left undeleted, becomes visible again after the
visibility timeout, and succeeds once the upload event has landed. The queue's
retry becomes the sequencing mechanism, and the visibility timeout becomes the
back-off. Because the inbox claim rolls back with the failed work, the aborted
attempt leaves nothing behind to block the successful one.

**"What do you cache, and how do you invalidate it?"**
Cache-aside on the watch page only, keyed by video id, deleted (never
overwritten) after the write commits, with a 5-minute TTL so a missed
invalidation self-heals. Feeds are not cached, because publishing one video
shifts every page after it — there is no precise invalidation key. The rule is:
cache something only if you can name exactly which entry to delete when it
changes. Redis runs `allkeys-lru` under a memory cap, so popularity is
discovered from traffic rather than declared by a policy.

**"Do you use the repository pattern with EF Core?"**
`DbContext` is already a unit of work and `DbSet` is already a repository, so a
generic `IRepository<T>` wrapper adds nothing and leaks `IQueryable`. What earns
its place is a narrow, intention-revealing interface per aggregate: it names the
queries the application actually makes, keeps provider exceptions like SQLSTATE
`23505` from leaking upward, and makes the service layer testable without a
database. Critically, the repositories here never call `SaveChangesAsync` —
committing belongs to the unit of work, because the change and its inbox or
outbox row must land in one transaction.

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

## 11. Roadmap

| Phase | Scope | Status |
|---|---|---|
| **1** | Local AWS substrate: S3, SQS, DynamoDB, Postgres split, Redis, edge cache | ✅ **Done, verified** |
| **2** | Service decomposition, contracts, SNS/SQS event bus, gateway, shared plumbing | ✅ **Done, verified** |
| **3** | Identity + Catalog: EF Core models, migrations, REST APIs, event handlers, inbox + outbox, cache-aside | ✅ **Done, verified** |
| 4 | Ingest + Encoder: resumable multipart upload, FFmpeg ABR ladder, thumbnails | ⬜ Next |
| 5 | Engagement + Search: sharded counters, reactions, comments, inverted index | ⬜ |
| 6 | Gateway BFF aggregation + Next.js frontend with hls.js adaptive player | ⬜ |
| 7 | `DESIGN.md` — doc-to-code mapping and interview question bank | ⬜ |

Stretch goals once the pipeline is end to end: per-shot encoding (chapter 5),
duplicate detection via perceptual hashing / LSH (chapter 4), optimistic
concurrency via Postgres `xmin`, and the two-stage
candidate-generation-plus-ranking recommender.

`PROGRESS.md` holds the live build state and is updated every session.
