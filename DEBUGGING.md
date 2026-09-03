# Debugging in Visual Studio

Run any service under the debugger without editing configuration and without
stopping anything else. The Gateway finds whichever instance is alive.

---

## The idea

Every service has **two homes**:

| | Where | Port |
|---|---|---|
| **Container** | `docker compose` | 8080–8086 |
| **Local** | F5 in Visual Studio | 5080–5086 |

The Gateway lists **both** as destinations for every cluster and health-checks
them every 5 seconds. It only routes to destinations that answer, so:

```
docker compose stop catalog     →  traffic moves to localhost:5082  (your debugger)
docker compose start catalog    →  traffic moves back to the container
```

Nothing to reconfigure, nothing to remember to switch back.

When **both** are running the container wins, because the load-balancing policy
is `FirstAlphabetical` and `"container"` sorts before `"local"`. A local process
you forgot about cannot silently start serving traffic.

---

## Port map

| Service | Container | Local (F5) |
|---|---|---|
| Gateway | 8080 | 5080 |
| Identity | 8081 | 5081 |
| Catalog | 8082 | 5082 |
| Ingest | 8083 | 5083 |
| Engagement | 8084 | 5084 |
| Search | 8085 | 5085 |
| Encoder | 8086 | 5086 |

Same last two digits in both columns, so the mapping is obvious at a glance.

Infrastructure is always the container: Postgres `5432`, Redis `6379`,
LocalStack `4566`, edge/CDN `8090`, upload harness `3100`.

---

## To debug one service

```bash
# 1. Bring up everything
docker compose up -d

# 2. Stop only the service you want to step through
docker compose stop catalog

# 3. In Visual Studio, set that project as startup and press F5
#    (profile: "Catalog (local)")
```

Within about five seconds the Gateway starts routing `/api/videos/...` to your
debugger. Breakpoints hit on requests from the harness at
`http://localhost:3100`, from Scalar, or from curl.

When you are done:

```bash
docker compose start catalog
```

---

## Why no configuration switching is needed

Each service has host-friendly defaults in `appsettings.Development.json`:

```jsonc
"ConnectionStrings": {
  "Catalog": "Host=localhost;Database=jamex_catalog;...",
  "Redis":   "localhost:6379"
},
"Aws": { "ServiceUrl": "http://localhost:4566" }
```

Compose sets the *same keys* as environment variables with container addresses:

```yaml
ConnectionStrings__Catalog: "Host=postgres;Database=jamex_catalog;..."
Aws__ServiceUrl: "http://localstack:4566"
```

**Environment variables beat appsettings** in ASP.NET's configuration order, so
the container uses container addresses and the host uses localhost — from one
file, with nothing to toggle.

---

## Calling services

**Through the Gateway** — exercises routing, and follows container/local
automatically:

```
GET http://localhost:8080/api/videos/{id}
GET http://localhost:8080/api/users/{id}
GET http://localhost:8080/api/channels/by-handle/jamex
```

**Directly** — bypasses the Gateway, useful for isolating a problem:

```
GET http://localhost:8082/videos/{id}     container
GET http://localhost:5082/videos/{id}     your debugger
```

Every service also serves interactive API docs at `/scalar` in Development —
the launch profiles open it automatically.

---

## Caveats

**Encoder needs FFmpeg on PATH.** It is baked into the Encoder image but is not
installed on this machine, so the "Encoder (local)" profile only works after
installing FFmpeg. Until then, debug it in its container:

```bash
docker compose logs -f encoder
```

Its `POST /debug/encode` endpoint runs the ladder over a generated clip, which
covers most of what you would otherwise step through.

**Consumers compete for messages.** If a service's container *and* your local
instance are both running, both poll the same SQS queue and each message goes to
whichever asks first — so a breakpoint may never hit. Stop the container when
debugging an event handler; HTTP endpoints do not have this problem, because
the Gateway prefers the container deterministically.

**Local instances write to the same databases** as the containers. That is
usually what you want — the seed data is right there — but a destructive change
under the debugger is a destructive change everywhere.

---

## How the failover works

In `JameX.Gateway/appsettings.json`:

```jsonc
"catalog": {
  "LoadBalancingPolicy": "FirstAlphabetical",
  "HealthCheck": {
    "Active": {
      "Enabled": true, "Interval": "00:00:05",
      "Timeout": "00:00:02", "Policy": "ConsecutiveFailures",
      "Path": "/health/live"
    }
  },
  "Metadata": { "ConsecutiveFailuresHealthPolicy.Threshold": "1" },
  "Destinations": {
    "container": { "Address": "http://localhost:8082/" },
    "local":     { "Address": "http://localhost:5082/" }
  }
}
```

That is the config for a Gateway running on the **host**. When the Gateway runs
in compose, environment variables replace those addresses with `http://catalog:8080/`
and `http://jamex-host:5082/`.

`/health/live` is the right probe: it reports only whether the process is up and
never touches a dependency, so a database blip does not make the Gateway think
the service has vanished.

### One Docker Desktop gotcha

Docker Desktop publishes **both** an IPv4 (`192.168.65.254`) and an IPv6
(`fdc4:…::254`) record for host aliases. .NET tries IPv6 first, there is no IPv6
route to the host, and every probe fails with `Network is unreachable` — which
reads like the service is down rather than like a networking problem.

The Gateway container therefore sets:

```yaml
DOTNET_SYSTEM_NET_DISABLEIPV6: "1"
```

---

## Verified

```
container running,  local stopped   → Gateway serves from container      ✅
container stopped,  local running   → Gateway serves from local in ~3s   ✅
both running                        → 5/5 requests hit the container     ✅
```
