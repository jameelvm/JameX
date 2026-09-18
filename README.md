# JameX

A working YouTube clone, built to learn and be able to explain and defend
every design decision behind a system at this scale.

The specification is the five-chapter design document in the parent folder
(`1.System Design_ YouTube.pdf` … `5.The Reality Is More Complicated.pdf`). Every
component that document describes has a real, runnable counterpart here.

**Stack:** .NET 10 · PostgreSQL · DynamoDB · S3 · SQS · SNS · Redis · FFmpeg ·
YARP · Next.js · Docker Compose + LocalStack

**Build status:** phases 1–8 complete and verified (phase 8, real
authentication, sits outside the design doc's own scope — see §12). The
whole pipeline runs end to end, in a real browser: sign up, presigned
resumable upload → FFmpeg ABR ladder → Catalog → CDN → hls.js playback,
with live reactions, comments, a real home feed, and search. A real video
goes from upload to playable HLS in
under 20 seconds.

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
9. [Phase 4 — Ingest and Encoder](#9-phase-4--ingest-and-encoder)
10. [Phase 5 — Engagement and Search](#10-phase-5--engagement-and-search)
11. [Phase 6 — Gateway and frontend](#11-phase-6--gateway-and-frontend)
12. [Phase 8 — Real authentication](#12-phase-8--real-authentication)
13. [Verification](#13-verification)
14. [Design talking points](#14-design-talking-points)
15. [Roadmap](#15-roadmap)

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
them — the point is to reason your way there, not recall a number.

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

Treat this one with suspicion, and say so. Using DAU directly as
requests-per-second is a wild over-estimate. The honest framing is that
*concurrency*, not daily totals, sizes a fleet. Naming that critique yourself
is worth more than reciting the arithmetic.

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

Two rows are not one-to-one, and a careful review will probe both:

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
├── DESIGN.md            ← system design summary + design Q&A
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

### Run the frontend

The backend stack above is enough to drive the API directly (curl, Scalar,
the debug harness at `:3100`), but the real Next.js app is a separate
process — `docker compose` does not start it.

```bash
cd web
npm install                        # first time only
cp .env.local.example .env.local   # first time only
npm run dev
```

Open `http://localhost:3000`. Browsing (the home feed, search, watch pages)
works immediately with no account. Reacting, commenting, and uploading need
a real one — use **Sign up** in the header once, then **Sign in** on later
visits; see §12 for how that's actually built.

### Debugging in Visual Studio

Every service can run under the debugger *alongside* its own container —
stop the container, press F5, and the Gateway starts routing to your
debugger within a few seconds, with nothing to reconfigure and nothing to
switch back afterwards. Full guide: **[`DEBUGGING.md`](DEBUGGING.md)**.

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

## 9. Phase 4 — Ingest and Encoder

This is the phase that makes the system actually playable. Phase 3 built a
Catalog that could react to `VideoUploaded` and `VideoEncoded` — but nothing
published them. Phase 4 builds the two services that do, and the moment the
first real one was published, Catalog's already-tested handlers processed it
with **zero code changes**. That's the payoff for building the receiving side
first.

### 9.1 What exists now

| | Ingest | Encoder |
|---|---|---|
| Owns | `jamex-upload-sessions` (DynamoDB), `jamex-raw` writes | `jamex-media` writes |
| Endpoints | 6 (`/uploads/...`) | 1 dev-only debug endpoint |
| Publishes | `VideoUploaded` | `VideoEncoded`, `VideoEncodingFailed` |
| Consumes | — | `VideoUploaded` |
| Scales on | connection count / bandwidth | queue depth |

### 9.2 The constraint that shapes Ingest: bytes never pass through it

Chapter 2 sizes ingest at ~480 Gbps. Routing that through an application
service would make the service the bottleneck and would need disk or memory to
buffer 600 MB originals per upload. So Ingest's entire API surface exchanges
kilobytes of JSON — ids, part numbers, ETags — never bytes. The browser PUTs
parts **directly to S3** using presigned URLs Ingest hands out; Ingest only
issues credentials and tracks state.

This is the data-plane / control-plane split:

```
Data plane   (the video bytes)     Browser ──────────► S3 directly
Control plane (start, track, complete)  Browser ──► Ingest ──► S3 API calls
```

### 9.3 The multipart plan — S3's constraints made concrete

`MultipartPlan.For(totalBytes, preferredPartSizeBytes)` decides how a file is
sliced, and does it **before a single byte moves** — a client that discovered
an impossible part count only at completion would have wasted its entire
upload.

```csharp
public const long MinPartSizeBytes = 5 * 1024 * 1024;   // S3's hard minimum
public const int  MaxParts        = 10_000;              // S3's hard ceiling
```

The two constants are facts AWS imposes, not choices — that's why they're
`const` in `MultipartPlan`, not configuration. What *is* configuration
(`UploadOptions.PreferredPartSizeBytes = 8 MB`, `MaxUploadBytes = 20 GB`) lives
in Ingest's own options class, not shared plumbing — no other service has any
business knowing Ingest's part-size preference.

Verified: a 600 MB file → exactly 75 parts of 8 MB. A 100 GB file would need
the part size to grow, because the 10,000-part ceiling binds before the 5 GB
per-part ceiling does.

### 9.4 Resumability — one DynamoDB item, one atomic write

`UploadSession` (DynamoDB, `jamex-upload-sessions`) is chapter 3's *"server
retains data temporarily to allow resumption"* — the record that lets a
dropped connection resume instead of restarting a 600 MB transfer. One item
per upload; every part's ETag lives inside a single nested `parts` map, not as
separate rows:

```json
{ "uploadId": "...", "parts": { "1": "abc123...", "2": "def456..." } }
```

**The one line that makes concurrent part uploads safe:**

```csharp
UpdateExpression = "SET #parts.#n = :etag"
```

The obvious alternative — read the session, add the part, write it back — is a
lost-update race: a browser uploads several parts in parallel, two completions
overlap, both read a 4-entry map, each adds its own, each writes back 5. One
ETag silently vanishes, and the upload can never complete because S3 requires
the full set. `UpdateItem` targeting one key inside the map sidesteps the race
entirely — concurrent writers touch different map keys and cannot conflict.

Verified with 3 truly concurrent part-record calls: all 3 survived.

### 9.5 Idempotent completion — Ingest's answer to the outbox problem

Ingest owns no relational database, so it cannot use Catalog's transactional
outbox — there's no transaction to enrol an event in. It marks the session
Completed and *then* publishes: two writes, so a crash between them could lose
the event.

The mitigation is to make completion **safely repeatable**:

```csharp
ConditionExpression = "attribute_exists(uploadId) AND #status = :inProgress"
```

Only the first `Complete` call wins that condition; a retry reuses the
**already-stored** `completionEventId` instead of minting a new one. Verified
by calling `/complete` twice — both calls published the identical event id.
Because the id is stable, Catalog's inbox (Phase 3) rejects the duplicate
automatically. Weaker than a true outbox — the window narrows from "lost
forever" to "published on the next attempt" rather than closing outright — but
it's the strongest guarantee available without a database.

### 9.6 Why the video bytes *do* pass through Encoder, and why that's a separate service

Encoder is the one service in the whole system where bytes genuinely flow
through the process — transcoding means decoding every frame, which cannot
happen without the file being local. That's exactly why it's split from
Ingest: Ingest scales on connection count and needs neither CPU nor disk;
Encoder scales on queue depth and needs both. Forcing them into one service
would make the CPU-bound half throttle the connection-bound half.

### 9.7 FFmpeg behind an interface

`IEncodingJobRunner` is deliberately file-in, files-out — it knows nothing
about S3, SQS or events, which is what let it be tested with a debug endpoint
that runs the real ladder over a synthetic clip, no infrastructure required.

```csharp
Task<SourceProbe> ProbeAsync(string sourcePath, CancellationToken ct);
Task<EncodingResult> RunAsync(EncodingJob job, CancellationToken ct);
```

`FfmpegEncodingJobRunner` is one implementation. A MediaConvert adapter is a
second implementation of the same interface — the pipeline that consumes it
never changes.

**The three flags that make adaptive switching actually work:**

```
-g 144 -keyint_min 144 -sc_threshold 0
```

A player can only switch quality **at a segment boundary**, and only if every
rendition's boundaries land at the same instant. Left to itself, FFmpeg inserts
keyframes wherever the scene changes — a different moment in every rendition —
so segments drift apart between qualities and switching produces a stutter or
an outright gap. Fixed GOP length plus disabled scene-change keyframes forces
every rendition to cut at the same six-second marks.

**Never upscale — the rule and the bug it caught.** Rendition selection is
`Where(r => r.Height <= probe.Height)` — never taller than the source, because
inventing pixels costs CPU and bytes to produce something *larger and
blurrier* than the original. The fallback path for a source smaller than every
configured rung originally used the smallest configured rung's height, which
silently upscaled a 180p source to 240p. Fixed to encode at the **source's own
height** instead — verified: a 180p source now produces a single 320×180
rendition, not 428×240.

**The master playlist is the menu, not the food.** Built by hand rather than
via FFmpeg's `var_stream_map`, listed lowest-bitrate first so a player with no
bandwidth estimate starts low and improves rather than stalling:

```
#EXT-X-STREAM-INF:BANDWIDTH=440000,RESOLUTION=428x240,...
240p/playlist.m3u8
#EXT-X-STREAM-INF:BANDWIDTH=3080000,RESOLUTION=1280x720,...
720p/playlist.m3u8
```

`BANDWIDTH` is peak, not average, and includes audio — understating it lets a
player pick a stream too heavy for its link and rebuffer.

### 9.8 The handler — download, encode, upload, announce

`VideoUploadedHandler` orchestrates four steps and cleans up on every path:

```
download raw file from jamex-raw
  → runner.RunAsync()               (Module 3's whole FFmpeg pipeline, LOCAL disk only)
  → upload every rendition + thumbnails + master.m3u8 to jamex-media (master LAST)
  → publish VideoEncoded            (or VideoEncodingFailed)
finally: delete the scratch directory, always
```

**Master playlist uploads last, deliberately.** It's the file a player fetches
first; uploading it before the segments exist would create a window where a
viewer could load the manifest and hit "not found" on every segment it
references. Uploading it last guarantees that the moment it's reachable,
everything it points to already is too.

**Permanent failures are caught; transient ones are not — and that split is
the whole design:**

```csharp
catch (EncodingFailedException ex)   { PublishFailedAsync(...); }  // corrupt file — retrying is pure waste
catch (TimeoutException ex)          { PublishFailedAsync(...); }  // too big once, too big again
// everything else (S3 unreachable, disk full) propagates uncaught
```

If every exception were turned into `VideoEncodingFailed`, a transient network
blip would permanently mark a good video as failed. If none were, a genuinely
corrupt file would retry three times, burn CPU each time, then land in the DLQ
with a generic failure instead of a clear reason. The split gets both cases
right.

**Idempotency here is Redis, not an inbox — and that's the correct tool for
this service.** Encoder owns no relational store, so there's no transaction to
put the claim inside. `RedisEventDeduplicator` narrows the window rather than
closing it, but it's genuinely valuable here: re-encoding a video costs
minutes of CPU, not a wasted `UPDATE`. `NoOpEventDeduplicator` (new in this
phase) makes Redis optional service-wide — a service without Redis configured
now falls back to "treat everything as new" rather than failing to start.

### 9.9 The debug harness — `web/debug/index.html`, port 3100

Testing the upload flow with `curl` only exercises the half that can't fail in
a browser. The presigned-URL flow is browser-shaped by design: the client
slices the file, reads the **`ETag` response header** off its own PUT, and
sends it back to complete — and that header is only readable because of a CORS
`ExposeHeaders` rule, which `curl` doesn't need and would never catch missing.

A single self-contained HTML page — no build step, no framework — with a file
picker, a live per-part progress grid, **Simulate drop / Resume** buttons that
exercise `GetStatus`'s missing-parts logic for real, and an hls.js player with
a manual quality selector. Served on **3100**, not 3000: that port was already
occupied by another app on the host, and 3100 is now an allowed origin in both
the service CORS policy and the raw bucket's CORS rules — 3000 stays free for
the real Next.js frontend in phase 6.

This harness caught the CORS gap directly: the first version of the presigned
URLs came back as `https://`, which LocalStack — plain HTTP on 4566 — refused
outright with a bare connection failure. `curl` never surfaces that class of
bug; a browser does immediately.

### 9.10 What phase 4 does not do, and three environment findings worth knowing

- **No resumable download / range requests on playback** — the edge cache
  serves whole segments; that's standard HLS and needs nothing extra here.
- **No adaptive tiering** (`PopularityTier`) — every video is still `Cold`;
  chapter 5's placement logic is a phase 5–6 concern.
- **LocalStack's SNS→SQS fan-out proved unreliable under heavy repeated local
  testing** — messages were sometimes dropped, and the Encoder's consumer was
  twice observed to wedge (stuck `NotVisible`, consumer idle, no log output)
  in a way that outlived an Encoder restart. Recovery that worked reliably:
  delete and recreate the queue, resubscribe it to the topic with its filter
  policy, **reapply its SNS access policy** (lost on delete — queues don't
  keep it automatically), then restart the consumer. A full `docker compose
  down` / `up` cleared it every time that was tried. This is a property of the
  local emulator, not the application — the one clean run captured for
  verification went upload-to-playable in 9–20 seconds with no intervention.
- **`jamex-edge` (nginx) resolves its LocalStack upstream once, at container
  startup.** Recreating LocalStack (a new image, a `down`/`up`) while `edge`
  keeps running leaves it routing to a dead IP — every `/media/...` request
  502s with no obvious cause. Fix: restart `edge` after any LocalStack
  recreation.
- **LocalStack's Persistence feature needs a paid Base/Ultimate plan** — this
  project runs on the free tier, so S3 objects do not survive a
  `docker compose down`. Postgres and, by an unrelated implementation detail,
  DynamoDB do. See `PROGRESS.md` for the full finding.

---

## 10. Phase 5 — Engagement and Search

Phase 4 made a video playable. Phase 5 makes it a *YouTube* video: a view
count, a like button, a comment thread, and a way to find it again — plus the
one deliberate detour of building the same search feature two different ways,
so the trade-off between them is something you can point at instead of just
recite.

### 10.1 What exists now

| | Engagement | Search |
|---|---|---|
| Owns | `jamex_engagement` (Postgres, comments only), `jamex-video-counters` + `jamex-user-reactions` (DynamoDB) | `jamex-search-index` (DynamoDB) |
| Endpoints | 8 (`/videos/{id}/counts`, `/views`, `/reactions/me`, `/videos/{id}/comments/...`) | 1 (`GET /search`) |
| Publishes | — | — |
| Consumes | `VideoEncoded`, `VideoDeleted` | `VideoEncoded`, `VideoDeleted` |

Catalog also grew one endpoint this phase — `GET /videos/search` — which is
where the second search engine actually lives; see §10.10.

### 10.2 One service, two stores, because the access patterns genuinely differ

Engagement's comments are read as an ordered, paginated list per video — a
relational access pattern. Its counters and reactions are read by direct key
lookup only, at far higher volume. Rather than force both into one store,
`EngagementDbContext` (Postgres) holds `comments` and the inbox only, while
`IVideoCounterRepository` and `IUserReactionRepository` reach DynamoDB
directly. This is the same split the design doc draws between the metadata
database and the counter store, just inside one service instead of two.

One consequence worth naming: `EngagementDbContext` has an inbox but **no
outbox** — split out of the shared `EventTables` helper as
`AddJameXInboxTable()`/`AddJameXOutboxTable()`/`AddJameXEventTables()`, the
first service that needed only one of the two. Engagement consumes events but
never announces its own changes; a like or a view has no downstream
consumer.

### 10.3 Sharded view counters — the write-scaling story made concrete

A single DynamoDB item caps out around 1,000 writes/sec. A viral video's view
counter is the hottest key in the entire system and will blow through that on
one row. The fix is chapter 4's headline write-scaling pattern: split the
counter into `ViewShardCount` (10) items —

```
videoId=abc, counterKey=VIEWS#0 → value: 1050
videoId=abc, counterKey=VIEWS#3 → value: 980
videoId=abc, counterKey=VIEWS#7 → value: 1102
```

— and pick one at random per write. `RecordViewAsync` never checks whether its
chosen shard exists first; DynamoDB's `ADD` auto-vivifies an absent item, so
there's nothing to pre-allocate. A video with three views might have hit only
two of ten shards, and `GetAsync` handles that correctly by summing whatever
rows actually exist rather than assuming all ten are present. The read side
pays for this: one video's counters means one `Query` returning up to ten
rows instead of one, and getting a video's total requires summing them in
application memory.

Likes and dislikes are deliberately **not** sharded. A like requires a unique
reaction row per user first (§10.4), and that uniqueness check already caps
the write rate for one video's likes far below what an anonymous,
unauthenticated view ping can reach — sharding here would add read-side cost
for a write-side problem that doesn't exist.

### 10.4 Idempotent reactions — the atomic swap that a read-then-write can't do safely

One row per (user, video) in `jamex-user-reactions` is the entire idempotency
mechanism for like/dislike: its mere existence, not a stored "none" value, is
what makes absence mean "hasn't reacted." Deciding what a reaction
*transition* means for the counters — first like, switch from dislike to
like, remove a like — sounds like it needs a read first: look up the current
reaction, then decide which counters to adjust.

That read-then-write is a real race. Two concurrent clicks from the same user
could both read "no reaction" and both increment the likes counter,
double-counting one person. The fix is `PutItem`/`DeleteItem` with
`ReturnValues=ALL_OLD` — DynamoDB's own atomic "tell me what this replaced,"
so learning the previous reaction and writing the new one happen as one
server-side step. Verified by firing **ten truly concurrent identical
"like" requests** from one user: the counter landed at exactly 1.

### 10.5 Comments — a tombstone, not a cascade

`comments.parent_comment_id` is a self-referencing foreign key set to
`Restrict`, not `Cascade`. That is a deliberate choice: deleting a top-level
comment that still has replies cannot simply remove the row without either
violating the constraint or orphaning every reply underneath it. `Comment`
carries an `IsDeleted` tombstone flag for exactly this case —

```
HasRepliesAsync(commentId)?
    true  → IsDeleted = true, Text = ""   (row stays, thread survives, renders as "[deleted]")
    false → row physically removed
```

A reply can never itself have replies — the one-level-nesting rule is
enforced in `CommentService.AddAsync`, not the schema, because a
self-referencing foreign key cannot express "at most one level deep" on its
own — so deleting a reply always takes the simple removal path.

### 10.6 The cross-store idempotency gap, closed rather than just documented

Postgres has a real inbox: claim an event id and apply its effect in one
transaction, so a redelivery is rejected atomically by a primary key. The
DynamoDB writes in this service **cannot join that transaction** — there is
no way to make "mark this event processed" and "write to Dynamo" atomic
across two different databases. Claiming the event first only narrows the
window; a crash between the two writes is still possible.

What actually closes it is making the DynamoDB write itself safe to repeat.
`VideoEncodedHandler` initialises a fresh video's likes/dislikes to zero using
a `PutItem` conditioned on `attribute_not_exists(videoId)` rather than a
plain overwrite — so a redelivered `VideoEncoded` is a genuine no-op even if
real likes have already landed. Verified by bumping a video's likes to 5
out-of-band, then replaying `VideoEncoded` with a fresh event id: likes
stayed at 5. `DeleteAllAsync`/`DeleteAllForVideoAsync` need no such guard —
deleting rows that are already gone is naturally idempotent.

### 10.7 Search's inverted index — tokenize, merge, fan out, intersect

`jamex-search-index` is chapter 3's inverted index, keyed exactly as
specified: `term` (partition) → `videoId` (sort), carrying `frequency` and
which `field` the term matched on. `Tokenizer` is deliberately the simplest
thing that works — lowercase, split on runs of Unicode letters/digits, count
occurrences — with no stemming, no stopword removal, no synonyms. Naming
those gaps instead of hiding them is the honest half of the comparison this
phase makes.

**Indexing** merges a term's occurrences across title, description and tags
into a single row: frequency summed across all three fields, tagged with
whichever field ranks highest (title beats tags beats description). A term
appearing in the title, description, *and* tags of one video produces one row
with frequency 3, not three rows.

**Searching** is the doc's stated cost of this design made concrete — there
is no single query that answers "which videos match all these terms":

```
Query(term="guitar")   → {Video1: freq 3, Video2: freq 3}    ← one partition read
Query(term="acoustic") → {Video1: freq 1}                     ← one partition read
                        ────────────────────────────────────
intersect               → Video1 only (matched BOTH terms)
```

AND semantics, not "any word matches": a video that hits every term *except
one* is dropped entirely, ranked by summed frequency across the terms it did
match. `DeleteAllForVideoAsync` needed a **new `by-video` GSI** — the base
table is keyed by term first, so finding every posting for one video is
otherwise impossible, the identical problem Engagement's reaction teardown
solves the same way.

### 10.8 A real contract gap: `VideoEncoded` didn't carry what Search needed

Search subscribes to `VideoEncoded`/`VideoDeleted` only — but `VideoEncoded`
originally carried nothing except Encoder's technical output (bitrates,
playlist keys, duration). No title, no description, no tags: nothing to
index.

The tempting fix was a synchronous call from `VideoEncodedHandler` to
Catalog's read API. That would have been wrong: it makes an otherwise fully
event-driven, failure-isolated queue consumer's success depend on a second
service being reachable, purely to process one message. The actual fix
extends `VideoEncoded` to carry `Title`, `Description` and `Tags`, sourced
from the original `VideoUploaded` that Encoder already has in hand when it
publishes — the identical "denormalise into the event" reasoning
`VideoUploaded` itself already uses, so a consumer never needs a callback to
act.

### 10.9 The one synchronous service-to-service call in this codebase

`GET /search` cannot answer with just a video id and a score — a search
result needs a title, a thumbnail, a duration. Search doesn't own any of
that. `ICatalogClient` makes a real HTTP call to Catalog's `POST
/videos/batch` to hydrate postings into full result cards.

This is deliberately inconsistent with §10.8's fix, and the inconsistency is
the point: a *request* is different from a *queue consumer*. The browser is
already synchronously blocked on this HTTP response, so one more HTTP hop
costs nothing structurally that wasn't already being paid — whereas an async
handler blocking on Catalog's uptime would turn one service's outage into
two. `CatalogClient` degrades a Catalog outage to "no results" rather than a
500; the index itself stays intact either way.

### 10.10 Catalog's other search engine — Postgres trigram similarity

`ix_videos_title_trgm`, a GIN index on `Video.Title` using `gin_trgm_ops`, has
existed since phase 3 specifically for this comparison. `GET
/videos/search?q=` queries it directly — no event handler, no second store,
no indexing step, because Catalog already owns this data and keeps it fresh
on every write. It's reachable through the Gateway with **no routing
change**: it falls inside the existing `/api/videos/{**catch-all}` route.

**A real tuning finding, not assumed.** The first version used `pg_trgm`'s
plain `similarity()` (the `%` operator) — and a one-word query like "guitar"
scored too low to match a real title, *"Guitar Solo Techniques for Rock."*
`similarity()` compares two entire strings, so a six-letter query against a
thirty-character title is penalised by the size mismatch alone, independent
of whether the word is actually present. Switched to `word_similarity()`
(`EF.Functions.TrigramsAreWordSimilar` / `TrigramsWordSimilarity`), which asks
"does *some part* of the title match this query" — the question a search box
actually needs answered, still backed by the same GIN index.

**What this buys, verified directly against the DynamoDB side's limits:** a
misspelled query — `"improvisaton"`, missing a letter — still found *"Jazz
Piano Improvisation Lesson."* The inverted index in §10.7 cannot do this at
all: it matches exact tokens, and a misspelled token is simply a different,
unindexed word.

| | Search (DynamoDB) | Catalog (Postgres) |
|---|---|---|
| Consistency | Eventually consistent — async, via events | Always fresh — same store as the source |
| Query model | Exact tokens, multi-term AND | Fuzzy substring, typo-tolerant |
| `"improvisaton"` (typo) | No match | Matches |
| Relevance signal | Summed term frequency | String similarity score |
| Availability coupling | None until a search request needs hydration | None — self-contained |

### 10.11 What phase 5 does not do

- **No comment moderation by the channel owner.** Engagement can only verify
  the *comment's* author, because that's the only ownership fact it holds;
  whether the caller owns the *channel* the video belongs to lives in
  Identity, the same cross-service-ownership gap Catalog already has for
  video writes.
- **No anti-abuse throttling on view pings.** `RecordViewAsync` has no
  uniqueness check at all — deliberately, since a raw view ping is meant to
  be cheap and anonymous — so nothing stops a refresh loop or a bot inflating
  a count. A real system gates this behind a minimum watch time and a
  per-viewer cooldown.
- **No stemming, synonyms, or phrase search on either engine.** Named, not
  hidden — see §10.7 and §10.10.

---

## 11. Phase 6 — Gateway and frontend

Every earlier phase proved a backend concept against `curl` and DynamoDB
scans. Phase 6 is the one that has to survive an actual browser: a real
person clicking, a real `<video>` element, a real cross-origin request — and
it surfaced bugs none of the earlier, service-level verification ever could,
because none of it involved a browser enforcing anything.

### 11.1 What exists now

| | Gateway (BFF addition) | `web/` (Next.js) |
|---|---|---|
| Owns | — (aggregates, doesn't store) | — (a client, not a service) |
| New surface | `GET /api/watch/{videoId}` — the one endpoint it serves itself | App Router pages: `/`, `/search`, `/watch/[videoId]`, `/upload` |
| Calls | Catalog, Engagement, Identity (fanned out, not sequential) | Catalog, Engagement, Identity, Search, Ingest — all through the Gateway |
| Scales on | request fan-out cost, same as Catalog/Engagement/Identity combined | static hosting / CDN, not a backend concern at all |

### 11.2 The Gateway as its own BFF — one endpoint it serves, everything else it forwards

Every other `/api/...` route is a pure YARP reverse-proxy rule — path in,
path out, no logic. `WatchController` is the deliberate exception: the watch
page needs a video's Catalog record, its Engagement counts and the caller's
own reaction, and its channel's name from Identity, and showing it means one
round trip from the browser, not four. `IWatchAggregationService` calls
Catalog first and alone — there is nothing to aggregate onto without a video
— then fans Engagement and Identity out **concurrently** with
`Task.WhenAll`, merging both onto Catalog's record with a `with` expression.

The failure handling is deliberately asymmetric. Catalog's client lets a
fault propagate — "no video" isn't a state the page can degrade around.
Engagement's and Identity's clients catch `HttpRequestException` and return
null instead: a watch page with stale counts or a missing channel name is a
visible degradation, not a broken page. This is the same choice chapter 2
makes about aggregation layers generally — a BFF should make its callers'
outages cheaper to tolerate, not just cheaper to detect.

### 11.3 Viewer identity — a guest stub, and the cookie gap that broke "your own reaction"

Auth was never in scope (see the design doc's stated boundaries), but every
interactive feature in this phase — reacting, commenting, uploading — needs
*a* caller identity to authorise against. `ViewerProvider` provisions one
real Identity user on first visit (`POST /users`), persists
`{userId, displayName}` in `localStorage`, and reuses it on every later
visit. It is not a security mechanism; it is the minimum viable stand-in for
the header the doc's own Gateway-authenticates-once design would forward in
production.

**A real gap, caught by reloading the page, not assumed away.** After
liking a video, reloading it showed the correct count but never the active
highlight — the like had genuinely happened. The watch page's data comes
from a Server Component `fetch`, which runs on the Node process with no
access to the browser's `localStorage`; every server-rendered load was
therefore anonymous no matter what the browser's guest had actually done.
The fix mirrors just the viewer id into a cookie (`lib/viewer/cookie.ts`
writes it from the browser, `lib/viewer/server-viewer.ts` reads it via
`next/headers` on the server — two files, not one, because mixing
browser-only `document.cookie` and server-only `cookies()` in the same
module breaks bundling in whichever direction you didn't test) and forwards
it as `X-JameX-User` on the server-side call to `GET /api/watch/{id}` — the
exact header `WatchAggregationService` already knew how to use. No backend
change; the frontend was missing a way to say who was asking.

### 11.4 Reactions and comments — optimistic UI over the same backend guarantees phase 5 proved

`ReactionButtons` seeds from the server-rendered `VideoDetail`, then owns its
own state: a click updates the UI immediately and calls the API in the
background, rolling back to the pre-click snapshot on failure. This is
purely a UX choice sitting on top of an already-idempotent backend — §10.4's
`ReturnValues=ALL_OLD` swap is what makes it safe to fire the request without
waiting, not something this phase had to re-solve. A small pure helper,
`applyReactionChange`, computes the counts delta for every case (fresh,
switch, remove) so the component itself never open-codes the arithmetic.

Comments surfaced one more real gap from actually clicking through the flow,
not from reading the API contract. §10.5's tombstone convention has no
`isDeleted` field on the wire — the marker *is* the literal string
`"[deleted]"` in `comment.text`. The first version tracked "did I just
delete this" as purely local client state, so a page reloaded after a
tombstone (deleted in an earlier session, or by someone else) still showed
live Edit/Delete controls on it. The fix, `isCommentDeleted()`
(`lib/comments.ts`), checks the text itself as a fallback everywhere a
comment renders — the same lesson §10.5 already teaches about the backend
applied one layer up, in the UI that has to render that backend's contract
honestly.

### 11.5 hls.js adaptive playback — two false trails and one real bug

This took the longest of anything in this phase, and the false trails are as
instructive as the real fix.

**False trail 1 — the tab was never actually playing.** Automated browser
tabs run backgrounded by design (an automation tool that stole the user's
foreground focus would be a much worse tool). hls.js's `StreamController`
exits its own tick loop immediately whenever the document isn't visible —
confirmed directly by reading `hls.streamController.state` (`"IDLE"`) and
`document.visibilityState` (`"hidden"`) on a live instance — so the manifest
and every rendition playlist loaded correctly, the quality selector showed
real parsed levels, and `video.readyState` still sat at 0 forever. Every
earlier "curl and fetch succeed, only hls.js's own loader fails" finding in
this project's history was this, not a network problem: none of curl,
fetch, or XHR go through a stream controller's tick loop, so none of them
were ever affected by the tab being backgrounded.

**False trail 2 — a real hls.js resilience gap, worth fixing regardless.**
`VideoPlayer`'s `Events.ERROR` handler originally only ever acted on
*fatal* errors, matching hls.js's own documented recovery pattern exactly
(`startLoad()` on network, `recoverMediaError()` on media, destroy
otherwise). But a non-fatal `levelLoadError` on the level hls.js picks to
start with can leave the stream permanently stalled at zero buffered data
even after a sibling level's playlist has loaded — hls.js doesn't retry that
specific case on its own. Added a capped retry (`hls.startLoad()`, three
attempts) for non-fatal network errors too. This is a legitimate hardening
independent of what caused the original error — a real transient blip
against any HTTP resource can produce the identical non-fatal, no-recovery
gap.

**The real bug, found only once verification moved to a real foregrounded
tab.** `.ts` segment responses — but never `.m3u8` playlist responses —
carried **two** `Access-Control-Allow-Origin` headers: nginx's own
`add_header ... always` plus one LocalStack's S3 emulation adds on some
objects. A response with two ACAO values is invalid under the CORS spec,
and every real browser rejects it outright
(`net::ERR_FAILED` — *"the 'Access-Control-Allow-Origin' header contains
multiple values '\*, \*', but only one is allowed"*). `curl` and every
server-side check up to this point reported a clean `200 OK` with a single
header, because neither enforces CORS at all — which is exactly why nothing
short of an actual browser fetch could ever have surfaced this. Fixed with
`proxy_hide_header` on every `Access-Control-*` header in
`infra/edge/nginx.conf`'s `/media/` location before `proxy_pass`, so the
edge — the CDN boundary — is the sole source of the public CORS contract
regardless of what the origin sends underneath it.

The methodological point survives the specific bug: **a proxy in front of a
third-party origin should never assume the origin sends none of the headers
it itself intends to own.** It should `hide` and replace, not `add` and
hope.

### 11.6 The resumable upload UI

`useResumableUpload` ports `web/debug/index.html`'s proven multipart flow
into a real hook, and the port is close to mechanical because §9's design
already put every hard part on the server: open the upload, ask
`GET /uploads/{id}` which parts already landed, presign and PUT only the
gaps straight to S3 with bounded concurrency (four workers), report each
ETag back, complete once everything is in. `getUploadStatus` being the
first call on every resume — never an assumption about what the client
already sent — is what makes "resume after a pause" and "resume after a
browser reload" the exact same code path as starting fresh.

Resuming across a *reload* needs one more thing a pause doesn't: the
in-flight session state has to outlive the JavaScript that was holding it.
`localStorage` holds `{uploadId, videoId, partSizeBytes, totalParts,
fileName}` — deliberately never the file itself, which the browser has no
way to re-read from disk without the viewer picking it again. `UploadForm`
detects a persisted session on mount and asks for the same file back rather
than silently starting a second, competing upload for the same video.

`ensureMyChannel` lazily provisions the guest viewer's one implicit channel
the first time an upload actually needs one, rather than eagerly alongside
the guest user itself — the same get-or-create shape `ViewerProvider` uses
one layer down. One piece of debug-harness language slipped into the real
UI and was worth catching on review: the pause control was still labelled
"Simulate drop," a name that means something to someone testing resilience
and nothing to someone just trying to pause an upload.

### 11.7 A real home feed and search, closing a loop the backend already had

The home page was a placeholder through phase 5 ("the home feed isn't built
yet"), and `GET /search` had existed and been verified since phase 5 without
the frontend ever calling it. Both close in this phase with the same shared
building block: `VideoCard`/`VideoGrid`, a responsive tile layout neither
page had to invent twice.

Catalog's video-list DTO carries a `channelId`, not a channel name — the
home feed needs the name for every card. The tempting shortcut is one
Identity call per video; `hydrateChannelNames`
(`lib/api/channel-hydration.ts`) instead resolves one call per **distinct**
channel id on the page (`Promise.all` over a deduplicated set, since a real
feed page routinely repeats the same channel across several uploads) — the
same batch-not-per-row instinct §7's `POST /users:batch` and §10.9's
`POST /videos/batch` already apply, just assembled client-side against an
endpoint (`GET /channels/{id}`) that was never designed as a batch endpoint
itself. A channel Identity can't resolve degrades that one card's name to
null, the identical "hydration failure degrades one field, not the whole
page" choice §11.2's `IIdentityReadClient` already makes for the watch page.

Search results have no `channelId` at all on the wire — `SearchHit` was
designed around what the inverted index actually stores, not around what a
grid card would eventually want. A search card renders without a channel
name rather than paying for an extra per-hit fetch of the full video record
just to backfill one field it was never contracted to have.

**A second real bug the redesign surfaced, not introduced.** A
`thumbnailUrl` being non-null on the wire is not a guarantee the image
actually loads — the underlying S3 object can be missing (see the
environment note below about LocalStack's free tier not persisting S3
across a restart) independent of whatever Catalog's own record says. The
grid originally rendered a browser's default broken-image icon in that
case. `VideoThumbnail` is a small client component specifically because
`onError` needs to run in the browser: it falls back to the same
placeholder a genuinely missing URL gets, so a broken image and an absent
one are indistinguishable to the viewer rather than one looking like a bug
and the other looking intentional.

### 11.8 What phase 6 does not do

- **No subscribe button, no channel page.** Identity's schema already tracks
  a `subscriberCount`, but nothing in this phase writes to it or reads a
  per-viewer subscription state — building a button that looks real but does
  nothing would be a worse UI than omitting it.
- **No real avatar images.** No channel in this system has ever had a
  populated `avatarUrl`. `Avatar` renders deterministic-colour initials
  instead — a real placeholder convention, not a broken-image workaround
  like §11.7's thumbnail fix, since there was never a URL to fail loading in
  the first place.
- **No recommendations, no "up next," no channel-owner comment moderation.**
  The last of these is the same cross-service-ownership gap §10.11 already
  names for Catalog and Engagement — a frontend has no data to build a
  feature the backend was never given the authority to serve.

---

## 12. Phase 8 — Real authentication

Every phase so far maps onto one of the design doc's five chapters — see
§7 of [`DESIGN.md`](DESIGN.md) for the literal table. This one doesn't, on
purpose: chapter 2 explicitly puts authentication out of scope, and
`ICurrentUser` was stubbed behind a plain header from phase 2 onward
specifically *because* real identity "would add a lot of code that teaches
nothing about video delivery." Once every video-delivery concept the doc
actually cares about was built, real auth was the next thing worth doing
properly rather than pretending a guest-account stub was good enough
forever.

### 12.1 What exists now

| | Identity (additions) | Gateway (additions) | Catalog (addition) | Frontend |
|---|---|---|---|---|
| New surface | `POST /users` now takes a password; `POST /users/login` | JWT-bearer validation + a header-rewrite middleware | `GET /videos/mine` | `/login`, `/signup`, `/you` |
| Stores | `users.password_hash` (new column) | — (validates, doesn't store) | — (reuses `Video.UploaderId`) | a token, not a raw user id |
| Removed | — | — | — | silent guest-account auto-creation |

### 12.2 Password storage and the login endpoint

`User.PasswordHash` is the only new column — `PasswordHasher<User>`
(ASP.NET Core's standalone hasher class, not the full Identity framework
with its user stores and sign-in managers this project has no use for)
embeds its own salt and cost parameter in the stored string, so there's no
separate salt column and no home-grown iteration count to eventually get
wrong. `POST /users/login` looks the account up by email, verifies the
hash, and — on success — asks a new `TokenService` for a signed JWT.

**One deliberate non-feature:** login returns exactly one error message,
`"Invalid email or password."`, whether the email doesn't exist or the
password is wrong — and it runs a real hash verification against a
throwaway value even on the not-found path, so the two cases also take
the same amount of time. Distinguishing them, in either the message or the
timing, is free reconnaissance for anyone trying to enumerate registered
emails.

### 12.3 The Gateway becomes the one place that validates anything

This is the payoff of a sentence that has sat in `JameX.Gateway/Program.cs`
since phase 2, unimplemented, as a comment about what a real Gateway would
eventually do: *"The Gateway validates the caller once and forwards a
trusted identity, so seven services do not each re-implement token
validation."* Phase 8 makes it true. `AddJameXJwtBearer` wires up
ASP.NET's JWT-bearer authentication against a signing key Identity and the
Gateway share (`Jwt:SigningKey`, identical in both services' config); a
small middleware right after `UseAuthentication()` does the actual work:

```csharp
context.Request.Headers.Remove(HeaderCurrentUser.HeaderName);   // never trust the client's own claim

var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
if (subject is not null)
    context.Request.Headers[HeaderCurrentUser.HeaderName] = subject;  // only ever set from a validated signature
```

**Why the removal has to happen unconditionally, not just "when there's no
valid token":** stripping only in the failure case still lets a caller
with *no* token at all set `X-JameX-User` directly and have it pass
through untouched — which is exactly how every service in this project
worked before this phase. Every downstream service's own code is
unchanged: Catalog, Engagement, and Identity itself still just read
`X-JameX-User` via the same `HeaderCurrentUser` from phase 2. Only what's
*allowed to set it* changed.

**Verified directly, not assumed:** a hand-crafted `X-JameX-User` header
with no `Authorization` token, sent straight at `POST /channels` (an
endpoint that used to trust it outright), now gets a 401. The identical
call with a real bearer token succeeds, and the channel's `ownerUserId` is
the token's own subject — never something the request body or a spoofed
header could choose.

### 12.4 "Your videos" — one new endpoint, no new cross-service coupling

`GET /videos/mine` is the one list endpoint in Catalog that returns every
status and privacy level instead of filtering to public-and-Ready, because
it's the one list endpoint where the caller and the uploader are always
the same person — authorised by `RequireUserId()`, filtered by that exact
id against `Video.UploaderId`. That column already existed for PATCH/
DELETE's ownership check, and a new composite index
(`ix_videos_uploader_id_created_at`, the same shape as the existing
channel-page index) is the only schema change it needed. No call to
Identity required: unlike the long-standing "Catalog can't verify channel
ownership" gap noted since phase 3, this endpoint was never trying to
verify *channel* ownership — uploader identity was always Catalog's own
data.

### 12.5 The frontend: one place to attach a credential, one real "signed out" state

Every authenticated browser call already funnelled through three shared
functions (`browserApiFetch`/`browserApiFetchOrNull`/`browserApiMutate`).
Attaching `Authorization: Bearer <token>` there once meant every call site
above them — reactions, comments, uploads, channel creation — could drop
the `viewerId` parameter it used to thread through by hand entirely. A
request from a signed-out browser simply carries no `Authorization` header
at all, and whatever endpoint required one 401s correctly; there is
nothing else to special-case.

The bigger change is what got *removed*. `ViewerProvider` no longer calls
`POST /users` on first visit to mint an invisible guest account — every
anonymous page view used to be a silent, permanent Identity row. Now
`viewer: null` is a real, intentional, browsable state: the home feed,
search, and watch pages all work with no account at all, and only
reacting, commenting, and uploading ask for a real sign-in. Real `/login`
and `/signup` pages replace the old auto-provisioning, and the watch
page's server-side personalisation (§11.3) now forwards a real
`Authorization: Bearer` header — built from a token mirrored into a
cookie, the same mechanism as before, just carrying a real credential
instead of a bare, spoofable user id.

### 12.6 What phase 8 does not do

- **No rate limiting on login attempts.** Nothing throttles repeated
  guesses against one account — a real deployment needs this before
  anything else in this list.
- **No email verification, no password reset.** Signup trusts whatever
  email is given; there is no flow for proving ownership of it or for
  recovering a forgotten password.
- **No refresh tokens, no revocation.** A token is valid for its full
  lifetime (`Jwt:ExpiryMinutes`) with no way to invalidate it early — a
  compromised token or a "log out everywhere" action can't actually end a
  session before it expires on its own.
- **No OAuth / social login, no multi-factor authentication.** One
  credential type, matching the doc's own framing that real identity was
  never the point of this project — see the opening of this section.

---

## 13. Verification

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

### Phase 4 — Ingest and Encoder

```bash
# --- the whole pipeline, timed, upload to playable ------------------------
USER=<a real user id>                    # POST /users, as in phase 3
CHANNEL=<a real channel id>               # POST /channels

SIZE=$(stat -c%s myvideo.mp4)
R=$(curl -s -X POST localhost:8083/uploads -H 'Content-Type: application/json' \
     -H "X-JameX-User: $USER" \
     -d "{\"channelId\":\"$CHANNEL\",\"title\":\"Test\",\"privacy\":2,
          \"fileName\":\"myvideo.mp4\",\"contentType\":\"video/mp4\",\"sizeBytes\":$SIZE}")
UP=$(echo "$R" | jq -r .uploadId); VID=$(echo "$R" | jq -r .videoId)

URL=$(curl -s -X POST "localhost:8083/uploads/$UP/parts/presign" \
      -H 'Content-Type: application/json' -H "X-JameX-User: $USER" \
      -d '{"partNumbers":[1]}' | jq -r '.parts[0].url')

ETAG=$(curl -s -X PUT --upload-file myvideo.mp4 "$URL" -D- -o /dev/null \
       | grep -i '^etag:' | sed 's/.*: *//')
#      ^ readable ONLY because jamex-raw's CORS ExposeHeaders includes ETag

curl -s -X PUT "localhost:8083/uploads/$UP/parts/1" -H 'Content-Type: application/json' \
     -H "X-JameX-User: $USER" -d "{\"eTag\":$ETAG}"

curl -s -X POST "localhost:8083/uploads/$UP/complete" -H 'Content-Type: application/json' \
     -H "X-JameX-User: $USER" -d "{\"parts\":[{\"partNumber\":1,\"eTag\":$ETAG}]}"
# → status: 1 (Queued). VideoUploaded has just been published.

# poll until Ready — a short clip typically takes single-digit seconds
watch -n1 "curl -s localhost:8082/videos/$VID | jq '{status,masterPlaylistUrl}'"

# --- play it -----------------------------------------------------------
# masterPlaylistUrl from the response above, straight into any HLS player:
ffplay "http://localhost:8090/media/videos/<id>/master.m3u8"

# --- prove the resumability claim ------------------------------------
# stop mid-upload (kill the client after reporting only some parts), then:
curl -s "localhost:8083/uploads/$UP" -H "X-JameX-User: $USER" | jq '.uploadedPartNumbers'
# → only the parts that actually landed. Re-presign and re-send only the rest.
```

**Or skip all of the above** and use the harness at
**[localhost:3100](http://localhost:3100)** — file picker, live per-part
progress, a *Simulate drop* / *Resume* pair that exercises the code above for
real, and an hls.js player with a manual quality selector.

**Verified, 2026-09-03:**

```
upload → Ready                 9s  (690 KB test clip, single part)
upload → Ready                20s  (same clip, fresh user/channel each time)
FFmpeg ladder, 1280×720 source  4 rungs (240p/360p/480p/720p), 4.6s to encode
FFmpeg ladder, 3840×2160 source 5 rungs (+1080p), 10.7s to encode
edge cache                     MISS → HIT confirmed, correct Content-Type
resumability                   3 concurrent part-record calls, all 3 survived
idempotent completion          /complete called twice → identical event id both times
no-upscale fix                 180p source → single 320×180 rendition, not 428×240
```

**Known local-only failure modes hit during verification** — see §9.10 and
`PROGRESS.md` for the full detail and recovery steps: SNS→SQS delivery drops
and consumer wedges under heavy repeated testing, `edge`'s stale upstream DNS
after a LocalStack recreate, and S3 objects not surviving `docker compose
down` on the free LocalStack tier. None of these are application defects —
each was isolated and confirmed by a clean run succeeding immediately after
the fix.

### Phase 5 — Engagement and Search

```bash
# --- Engagement: counters + reactions, direct against the queue -----------
# VideoEncoded initialises counters (see §10.6 for why the write is
# conditioned rather than a plain PutItem)
send() { docker exec jamex-localstack awslocal sqs send-message --queue-url "$1" \
  --message-body "file://$2" --message-attributes "{\"eventType\":{\"DataType\":\"String\",\"StringValue\":\"$3\"}}"; }

Q=$(docker exec jamex-localstack awslocal sqs get-queue-url \
      --queue-name jamex-engagement-events --query QueueUrl --output text)
send "$Q" /tmp/encoded.json VideoEncoded

docker exec jamex-localstack awslocal dynamodb query \
  --table-name jamex-video-counters --key-condition-expression "videoId = :v" \
  --expression-attribute-values "{\":v\":{\"S\":\"$VIDEO\"}}"
# → LIKES=0, DISLIKES=0

# --- Engagement: the REST API ----------------------------------------------
curl -s http://localhost:8084/videos/$VIDEO/counts
# → {"views":0,"likes":0,"dislikes":0,"comments":0}

curl -X PUT -H "X-JameX-User: $USER" -H 'Content-Type: application/json' \
     -d '{"kind":0}' http://localhost:8084/videos/$VIDEO/reactions/me   # Like
curl -X PUT -H "X-JameX-User: $USER" -H 'Content-Type: application/json' \
     -d '{"kind":1}' http://localhost:8084/videos/$VIDEO/reactions/me   # switch to Dislike

# fire 10 concurrent identical likes from the SAME user
for i in $(seq 1 10); do
  curl -s -o /dev/null -X PUT -H "X-JameX-User: $USER" -H 'Content-Type: application/json' \
       -d '{"kind":0}' http://localhost:8084/videos/$VIDEO/reactions/me &
done; wait
curl -s http://localhost:8084/videos/$VIDEO/counts   # → likes:1, not 10

# --- Engagement: comments, the tombstone path ------------------------------
TOP=$(curl -s -X POST -H "X-JameX-User: $USER" -H 'Content-Type: application/json' \
      -d '{"text":"first!"}' http://localhost:8084/videos/$VIDEO/comments | jq -r .commentId)
curl -X POST -H "X-JameX-User: $OTHER" -H 'Content-Type: application/json' \
     -d "{\"text\":\"welcome\",\"parentCommentId\":\"$TOP\"}" \
     http://localhost:8084/videos/$VIDEO/comments

curl -X DELETE -H "X-JameX-User: $USER" http://localhost:8084/videos/$VIDEO/comments/$TOP
curl -s http://localhost:8084/videos/$VIDEO/comments
# → text: "[deleted]" — the row survives because the reply still points at it

# --- Search: the inverted index, direct against the queue ------------------
SQ=$(docker exec jamex-localstack awslocal sqs get-queue-url \
       --queue-name jamex-search-events --query QueueUrl --output text)
send "$SQ" /tmp/encoded_with_metadata.json VideoEncoded

curl -s "http://localhost:8085/search?q=jazz+piano"
curl -s "http://localhost:8085/search?q=jazz+guitar"   # one non-matching term → []

# --- Catalog: the other search engine --------------------------------------
curl -s "http://localhost:8082/videos/search?q=jazz+piano"
curl -s "http://localhost:8082/videos/search?q=improvisaton"   # typo, missing a letter
```

**Verified, 2026-09-12:**

```
counter init                   VideoEncoded → likes=0, dislikes=0; redelivery is a no-op
race condition                 10 concurrent identical PUT-Like calls, one user → likes=1
reaction switch                Like→Dislike in one call: likes 1→0, dislikes 0→1
comment tombstone               deleting a comment WITH a live reply → text="[deleted]", reply
                                and count both survive; deleting a leaf reply hard-deletes it
comment nesting                reply-to-a-reply rejected: 400 "cannot themselves be replied to"
inverted index                 two videos sharing "guitar" (freq 3 each) indexed correctly;
                                VideoDeleted removed only the deleted video's postings
inverted index, AND semantics  "jazz guitar" (one non-matching term) → [] even with a partial hit
cross-service hydration        GET /search returned real title/thumbnail/duration from Catalog
                                via a live HTTP call, not just a videoId
full pipeline                  real VideoUploaded → VideoEncoded reached Catalog AND Search;
                                deleting through Catalog's API relayed VideoDeleted through the
                                outbox and cleared Search's index
trigram typo tolerance         "improvisaton" (misspelled) matched "...Improvisation Lesson" —
                                the DynamoDB inverted index cannot do this at all
word_similarity fix            plain similarity() missed a real single-word title match;
                                word_similarity() found it, same GIN index
```

```bash
# --- Phase 6: the Gateway BFF -----------------------------------------------
curl -s "http://localhost:8080/api/watch/<videoId>"                    # anonymous
curl -s "http://localhost:8080/api/watch/<videoId>" -H "X-JameX-User: <userId>"
# → same video, but viewerReaction reflects that user's own reaction

# --- Phase 6: the CORS bug, before/after ------------------------------------
curl -s -D - -o /dev/null "http://localhost:8090/media/videos/<id>/360p/seg_000.ts" \
  -H "Origin: http://localhost:3000" | grep -i access-control-allow-origin
# → before the fix: TWO "Access-Control-Allow-Origin: *" lines (a real browser
#   rejects this outright; curl does not, which is why this needed a real
#   browser's network tab to ever surface)
# → after the fix (infra/edge/nginx.conf's proxy_hide_headers): exactly one

# --- Phase 6: resumable upload, exercised through the real UI ---------------
# web/upload — pick a file, watch the per-part grid fill in, click "Pause"
# mid-upload, then "Resume": the parts already landed stay green and only the
# gap re-sends — the identical claim §9's debug-harness verification made,
# now proven through the real Next.js form instead of the harness.

npm --prefix web run lint
npm --prefix web run build   # production build, full type-checking
```

**Verified, 2026-09-16:**

```
BFF aggregation      anonymous GET /api/watch/{id} → channel name, zeroed counts,
                      viewerReaction: null in one call; with X-JameX-User after a real
                      like/view, same endpoint reflects updated counts AND the
                      caller's own reaction, anonymous call for the same video unaffected
viewer identity       first browser visit made a genuine cross-origin POST /users
                      (CORS preflight → 204 → 201); reload made ZERO new /users calls
reactions             optimistic Like/Dislike/switch/un-react all confirmed against
                      Engagement directly, not just the UI; reload after liking now
                      shows the active highlight (previously did not — the cookie fix)
comments UI           tombstoned comment reloaded shows no stale Edit/Delete controls
                      (previously did, before isCommentDeleted() checked the text itself)
hls.js playback       real FFmpeg-encoded stream: manifest + real levels parsed;
                      video.readyState stayed 0 in every automated (backgrounded) tab;
                      played end to end once tested in the user's own foregrounded tab
CORS duplicate header .ts segments carried two Access-Control-Allow-Origin values;
                      curl and PowerShell's own HTTP client never saw a problem;
                      every real browser rejected it outright until proxy_hide_header
                      made nginx the sole source of the response's CORS headers
resumable upload UI   real file uploaded end to end through /upload; Pause mid-upload
                      then Resume continued from exactly the parts still missing
home feed + search    responsive grid from real GET /videos and GET /search; a video
                      missing its S3 thumbnail object rendered the placeholder tile,
                      not a broken-image icon, after VideoThumbnail's onError fallback
```

```bash
# --- Phase 8: signup, login, and the spoofing gap it closes ---------------
curl -s -X POST "http://localhost:8080/api/users" -H "Content-Type: application/json" \
  -d '{"email":"alice@example.com","displayName":"Alice","password":"correct horse battery"}'
# → 201, real UserDto — no token, this is registration, not login

curl -s -X POST "http://localhost:8080/api/users/login" -H "Content-Type: application/json" \
  -d '{"email":"alice@example.com","password":"wrong password"}'
# → 401 {"error":"Invalid email or password."}

TOKEN=$(curl -s -X POST "http://localhost:8080/api/users/login" -H "Content-Type: application/json" \
  -d '{"email":"alice@example.com","password":"correct horse battery"}' | jq -r .token)

USERID="<alice's real id, from the signup response>"

# The header used to be trusted outright. Prove it no longer is:
curl -s -X POST "http://localhost:8080/api/channels" -H "Content-Type: application/json" \
  -H "X-JameX-User: $USERID" -d '{"name":"Spoofed channel","handle":"spoofed"}'
# → 401 — the Gateway strips a client-supplied X-JameX-User unconditionally

curl -s -X POST "http://localhost:8080/api/channels" -H "Content-Type: application/json" \
  -H "Authorization: Bearer $TOKEN" -d '{"name":"Alice channel","handle":"alicechannel"}'
# → 201, ownerUserId is the token's own subject — never something the caller chose

curl -s "http://localhost:8080/api/videos/mine" -H "Authorization: Bearer $TOKEN"
# → 200, paged, every status/privacy level — this account's own uploads only

curl -s "http://localhost:8080/api/videos?page=1&pageSize=1"
# → 200, no Authorization header at all — anonymous browsing is unaffected
```

**Verified, 2026-09-17:**

```
signup + login        real account created, hashed password stored; wrong password → 401
                       with a generic message; correct password → a real JWT
spoofing closed        a hand-crafted X-JameX-User header with no token, sent at an
                       endpoint that used to trust it outright, now 401s; the identical
                       call with a real bearer token succeeds as that token's own subject
GET /videos/mine       401 with no token; 200 with one, scoped correctly to the caller
anonymous browsing     the public feed, search, and watch pages all still work with
                       zero Authorization header — unaffected by any of the above
frontend, live in a real browser:
  signed up             real account created, auto-signed-in, header updated live
  reload persistence    liked a video, reloaded — the reaction survived (JWT-cookie
                         successor to the old raw-user-id-cookie trick from phase 6)
  "Your videos"         server-rendered, forwarding the cookie token as a real bearer
                         header; correct empty state for a brand-new account
  sign out              header reverted to Sign in/Sign up; the like count stayed
                         visible but the button was correctly un-highlighted and
                         disabled for the now-anonymous viewer
```

---

## 14. Design talking points

A quick-reference pass. Each is answerable from what is actually built.

> **[`DESIGN.md`](DESIGN.md)** holds the fuller version — the decision register,
> a failure-mode table, and the Q&A bank grouped by theme. Use this section
> for a quick pass and `DESIGN.md` for the deeper one.

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

**"How do you make a large upload resumable?"**
One DynamoDB item per upload, holding every confirmed part's ETag in a nested
map. On reconnect the client asks which parts already landed and re-sends only
the gaps — it never assumes; it asks. The tricky part is recording a part
safely under concurrency: `UpdateExpression = "SET parts.#n = :etag"` targets
one key inside the map, so parts uploaded in parallel can never race each
other into a lost update the way a read-modify-write would.

**"You don't have a database in this service. How do you make completion
idempotent without one?"**
Store the event id *with* the state transition, guarded by a condition that
only the first caller can satisfy. A retry fails that condition, reads back
the id that already won, and republishes the identical event — which the
downstream service's inbox then recognises as a duplicate. It's weaker than a
true transactional outbox (the window narrows rather than closes), but it's
the strongest guarantee available with no relational store to enrol a claim
in.

**"How does adaptive bitrate switching actually work under the hood?"**
Two files types: one master playlist listing every quality with its bandwidth
and resolution, and one playlist per quality listing that quality's own
segments. The player reads the master once, picks a quality, and can swap to a
different quality's playlist at any segment boundary — but only if every
quality's segments are cut at the *same* timestamps. That alignment is not
automatic; it requires forcing an identical keyframe interval across every
encode (`-g`, `-keyint_min`, `-sc_threshold 0`), or the qualities drift apart
and switching produces a visible stutter.

**"Would you ever upscale a low-resolution source to fill out your quality
ladder?"**
No — a rendition taller than the source invents pixels it doesn't have,
producing a file that is simultaneously larger and blurrier than the original.
The ladder generation skips any configured rung above the source's own
resolution, and if the source is smaller than every configured rung, it
produces exactly one rendition at the source's native size rather than
upscaling to the smallest configured one.

**"A user double-clicks Like. How do you stop the counter incrementing
twice?"**
The reaction write itself reports what it replaced, atomically —
`PutItem`/`DeleteItem` with `ReturnValues=ALL_OLD` — so the counter
adjustment is computed *after* the write commits, not decided from a
separate read beforehand. A plain "read the current reaction, then decide"
races: two concurrent writes can both read "no reaction" and both increment.
Verified with ten truly concurrent identical requests landing at exactly one.

**"You have an at-least-once consumer with no relational database to put an
inbox in. How do you stay safe?"**
Two layers. Redis narrows the redelivery window the same way it does for
Encoder, but the real guarantee is making the write itself idempotent: a
DynamoDB `PutItem` conditioned on `attribute_not_exists` turns "initialise
this counter" into a genuine no-op on redelivery, rather than resetting real
activity back to zero. Deletes need no such guard — removing an
already-removed row is naturally safe.

**"A comment has replies. What happens when the author deletes it?"**
It's tombstoned, not removed: the text is blanked and a flag is set, but the
row stays so the reply chain doesn't point at nothing. The alternative —
cascading the delete to every reply — silently destroys other people's
comments to satisfy one person's delete. The schema itself enforces the
boundary: the self-referencing foreign key is `Restrict`, not `Cascade`,
which is what makes "just delete it" fail loudly instead of quietly
cascading.

**"Why does one event carry data a downstream consumer 'shouldn't' need,
like a video's title landing in an encoding-completion event?"**
Because the alternative is a synchronous call from an async queue consumer
to fetch it, which couples that consumer's success to a second service being
up — for one message. Denormalising the field into the event is cheap and
keeps every consumer able to act alone. The same system also makes a real
synchronous call elsewhere (search-result hydration) — the difference is
whether a human is already synchronously waiting on the response. If yes,
one more HTTP hop costs nothing new; if it's a background handler, it does.

**"Walk me through building an inverted index by hand."**
Key on the term, not the document: `term → videoId → frequency`. Indexing
tokenizes every field and writes one row per distinct term. Searching one
word is one partition read. Searching several words has no single query that
answers it — you query each term's partition independently and intersect the
result sets in application memory, keeping only documents that matched
*every* term. Deleting a document needs a secondary index keyed the other
way around, because the base table can't be queried by document id alone.

**"When would you pick full-text search over an inverted index you built
yourself, or the other way round?"**
If the source data already lives in a relational database you own outright,
FTS there is nearly free and always consistent — no event lag, no second
store. Reach for a purpose-built inverted index (or, in production, a real
search engine like OpenSearch) when write volume would overwhelm that
database, or a service other than the data's owner needs to serve the
queries. In this build the two sit side by side deliberately, to make the
trade-off demonstrable: exact-token AND-matching with no typo tolerance on
one side, typo-tolerant substring matching with no multi-term boolean logic
on the other.

**"Where does aggregation belong — in the client, or behind an API?"**
Behind an API, and specifically behind a BFF built for the page that needs
it, not a generic one. A browser fetching from Catalog, Engagement, and
Identity separately means three round trips, three failure points the
client has to reconcile, and every one of those services' internal
addresses exposed to the public internet. `GET /api/watch/{id}` is the
Gateway making those three calls itself — two of them concurrently — and
handing back one response. The client gets one request to reason about; the
services stay unreachable from outside the cluster.

**"A page needs a foreign key's display name for every row in a list —
video → channel name, order → customer name, that shape. How do you avoid
N+1?"**
Batch by the *distinct* foreign key, not by row. A feed of 24 videos from 6
channels needs 6 lookups, not 24, because several videos share a channel.
Collect the unique ids, fire them in parallel, build a map, then merge —
the same instinct that motivates a dedicated batch endpoint (this system
already has two: user lookup and video hydration), except here it's
assembled client-side against an endpoint that's just a plain by-id GET,
because the caller — not the resource itself — is the one that knows the
requests are going to repeat.

**"Why would a proxy sitting in front of a third-party origin need to strip
headers the origin sends, rather than just adding its own on top?"**
Because two conflicting values for the same header is often *worse* than
one wrong value, not better — a response with two
`Access-Control-Allow-Origin` headers isn't ambiguous to a spec-compliant
browser, it's rejected outright, even when both values are identical
wildcards. If a proxy owns the public contract for a header, it has to
`hide` whatever the origin sends and set its own, not just append — "the
origin probably doesn't set this" is an assumption a proxy in front of
someone else's system doesn't get to make, and the failure mode when it's
wrong is invisible to any tool that doesn't itself enforce that same
policy, which is most of them.

**"How do you make a player library's error recovery actually robust, not
just handle the cases you've seen?"**
Match the library's own documented recovery contract for its fatal error
categories exactly — that part is usually well specified. Then separately
ask what happens on the errors it reports as *non-fatal*: a library that
decides an error isn't fatal is making a claim that the system can keep
running, not a guarantee that it will keep making progress on its own. Here,
a non-fatal error on the level a player picked to start with could stall
playback forever with zero further attempts, because the library's own
retry policy for that specific case had already been exhausted before the
error ever reached application code. The fix isn't a library-specific
workaround; it's the general lesson — "non-fatal" and "will recover
unattended" are two different claims, and treating them as the same one is
exactly how a transient blip becomes a permanent, silent failure.

**"Every service trusts a header a caller sets. How do you make that
trustworthy without changing every service?"**
Move validation to one boundary everything already passes through, and
make that boundary the *only* thing allowed to set the header downstream
services read. The Gateway strips whatever identity header the client
sent — unconditionally, on every request, not just when it's obviously
wrong — then sets it again only from a signature it just validated.
Catalog, Engagement, and Identity itself never change: they still just
read the header, but now it can only ever hold what a real credential
vouched for. The mistake that leaves the hole half-closed is stripping
*conditionally* — removing it only when a token is invalid still lets a
caller with no token at all sail through untouched.

**"Symmetric or asymmetric signing for a JWT?"**
Whichever matches your actual trust boundary. Asymmetric keys let a party
verify a signature without being able to produce one — worth it when the
issuer and the verifiers are different parties. When they're the same
system, as here (one service issues, one service verifies, both internal),
a shared symmetric key costs nothing extra in security and is one fewer
moving part.

**"How do you hash a password without rolling your own crypto?"**
Use a maintained primitive, not a maintained framework, unless you need
the framework. `PasswordHasher<TUser>` embeds its own salt and cost
parameter in the stored value — no separate salt column, no home-grown
iteration count. The full identity-framework it comes from also offers
user stores, sign-in managers, and multi-provider login; none of that
applies to one email/password pair per account, so none of it got pulled
in.

---

## 15. Roadmap

| Phase | Scope | Status |
|---|---|---|
| **1** | Local AWS substrate: S3, SQS, DynamoDB, Postgres split, Redis, edge cache | ✅ **Done, verified** |
| **2** | Service decomposition, contracts, SNS/SQS event bus, gateway, shared plumbing | ✅ **Done, verified** |
| **3** | Identity + Catalog: EF Core models, migrations, REST APIs, event handlers, inbox + outbox, cache-aside | ✅ **Done, verified** |
| **4** | Ingest + Encoder: resumable multipart upload, FFmpeg ABR ladder, thumbnails | ✅ **Done, verified** |
| **5** | Engagement + Search: sharded counters, idempotent reactions, comments, DynamoDB inverted index, Postgres trigram FTS comparison | ✅ **Done, verified** |
| **6** | Gateway BFF aggregation, Next.js frontend, hls.js adaptive player, resumable upload UI, home feed, search | ✅ **Done, verified** |
| **7** | `DESIGN.md` — doc-to-code mapping and design Q&A | ✅ **Done** |
| **8** | Real authentication (outside the design doc's own scope): password login, JWT, Gateway-only validation, "Your videos" | ✅ **Done, verified** |

Stretch goals once the pipeline is end to end: per-shot encoding (chapter 5),
duplicate detection via perceptual hashing / LSH (chapter 4), optimistic
concurrency via Postgres `xmin`, and the two-stage
candidate-generation-plus-ranking recommender.

`PROGRESS.md` holds the live build state and is updated every session.
