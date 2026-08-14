# JameX

A working YouTube clone built to internalise the system design in
`../1..5.*.pdf` (Grokking Modern System Design Interview — YouTube chapters).
The goal is **interview preparation through implementation**: every component in
the design doc has a real, runnable counterpart here.

## Read this first

- **`PROGRESS.md`** — live build state. What is done, what is next, where the
  last session stopped. **Update it at the end of every working session.**
- **`README.md`** — end-to-end teaching documentation. **Update it at the end of
  every phase**, explaining what was built and why, with verification steps and
  interview talking points. It is revision material, not a change log.

## Owner context

The author is a C#/.NET developer with AWS, PostgreSQL and DynamoDB experience,
preparing for a system design interview. Prefer idiomatic .NET and real AWS
service APIs over bespoke abstractions — the code should double as an answer to
"how would you actually build this on AWS?".

## Architecture

**Service-oriented**, seven services, each owning its data exclusively. Sync
over HTTP through the Gateway; async over one SNS topic fanned out to
per-service SQS queues with subscription filter policies.

| Service | Owns exclusively | Consumes |
|---|---|---|
| Gateway | — (YARP routing + BFF aggregation) | — |
| Identity | `jamex_users` (Postgres) | — |
| Catalog | `jamex_catalog` (Postgres), `jamex-thumbnails` (Dynamo) | VideoUploaded, VideoEncoded, VideoEncodingFailed |
| Ingest | `jamex-upload-sessions` (Dynamo), `jamex-raw` (S3) | — |
| Encoder | `jamex-media` (S3) | VideoUploaded |
| Engagement | `jamex_engagement` (Postgres), counters + reactions (Dynamo) | VideoEncoded, VideoDeleted |
| Search | `jamex-search-index` (Dynamo) | VideoEncoded, VideoDeleted |

## Stack

| Layer | Choice |
|---|---|
| Services | .NET 10, ASP.NET Core minimal APIs |
| Gateway | YARP |
| Relational | PostgreSQL 17 — one database per owning service |
| Wide-column / KV | DynamoDB (stands in for the doc's Bigtable) |
| Object storage | S3 (`jamex-raw`, `jamex-media`) |
| Events | SNS topic → 4 SQS queues, each with a DLQ |
| Cache | Redis (stands in for the doc's Memcached) |
| CDN / PoP | nginx `proxy_cache` in front of S3 |
| Transcoding | FFmpeg behind `IEncodingJobRunner`; MediaConvert adapter later |
| Frontend | Next.js App Router, shadcn/ui, hls.js |
| Local runtime | Docker Compose + LocalStack (S3, SQS, SNS, DynamoDB) |

Everything AWS runs on LocalStack locally, through the genuine AWS SDK for .NET.
Repointing at a real account is a config change, not a code change.

## Layout

```
App/
├── CLAUDE.md            # this file
├── PROGRESS.md          # session state — read and update every session
├── README.md            # end-to-end teaching docs, updated every phase
├── docker-compose.yml
├── infra/
│   ├── docker/          # Service.Dockerfile (parameterised), Encoder.Dockerfile
│   ├── localstack/init/ # buckets, SNS topic, queues + filter policies, tables
│   ├── postgres/init/   # one database per owning service
│   └── edge/            # nginx CDN cache config
├── src/
│   ├── shared/
│   │   ├── JameX.Contracts/       # events + DTOs; no infrastructure deps
│   │   └── JameX.ServiceDefaults/ # AWS clients, publisher, consumer, health
│   └── services/
│       ├── JameX.Gateway/   JameX.Identity/   JameX.Catalog/
│       ├── JameX.Ingest/    JameX.Encoder/    JameX.Engagement/
│       └── JameX.Search/
└── web/                 # Next.js frontend
```

## Conventions

- **One service owns a store.** No service reads another's database. If it needs
  data it does not own, it calls the owner's API or reacts to its events. This
  is the rule that stops this becoming a distributed monolith.
- `JameX.Contracts` holds only what crosses a boundary — events and public DTOs.
  Entities stay internal to their owning service.
- Event handlers must be idempotent: SNS→SQS delivery is at-least-once.
- AWS resources are named `jamex-*`; databases `jamex_*`.
- Anything that exists purely to demonstrate a design-doc concept (sharded
  counters, popularity tiering, per-shot encoding) carries a comment naming the
  chapter it comes from, so the code reads as revision material.

## Commands

```bash
docker compose up -d --build           # full stack
dotnet build JameX.slnx                # compile
docker compose logs -f encoder         # watch transcoding
docker compose logs -f catalog search  # watch event fan-out
```

Ports: web `3000`, gateway `8080`, identity `8081`, catalog `8082`,
ingest `8083`, engagement `8084`, search `8085`, encoder `8086`,
edge/CDN `8090`, LocalStack `4566`, Postgres `5432`, Redis `6379`.
