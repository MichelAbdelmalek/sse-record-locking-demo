# Record locking over SSE

A small, complete example of **per-tab record locking with live takeover**, built the way I would build it in production:
.NET 10 minimal API, EF Core 10 on PostgreSQL, Angular 18 with Tailwind CSS 3 and PrimeNG 17, Docker, and GitHub Actions.

Open the same record in two browser tabs. The first tab gets the lock. The second can take it over, and the first is
warned at once, switches to read-only, and any save it still sends is rejected by the server.

> All code and data here are written for this demo. It is a public reconstruction of a pattern, not code from any employer.

## What it shows

| Problem | How it is solved here |
| --- | --- |
| Two people editing the same record | One lock row per record; taking it is a single atomic `INSERT … ON CONFLICT DO UPDATE … WHERE` |
| Locks left behind by closed or crashed tabs | A lock is valid only while its holder's SSE stream is alive (heartbeat in `tab_sessions`) |
| Waiting on someone who walked away | Anyone can take over; the displaced tab gets a `lock-lost` event within milliseconds |
| A displaced tab saving anyway | Every save re-checks the lock holder inside a transaction (`SELECT … FOR UPDATE`) |
| Two saves racing | Optimistic concurrency on Postgres `xmin`, so a stale version returns `409` with the latest record |
| "Give me the next record nobody is editing" | One keyset query over `(priority, id)` that filters out live locks in the database, no paging in memory |
| Several API instances | Optional Postgres `LISTEN/NOTIFY` fan-out, so a takeover on instance A reaches a tab connected to instance B |

## How a lock works

```
Tab A ──GET /api/events (SSE)──► API ──heartbeat every 10 s──► tab_sessions.last_seen
Tab A ──POST /records/7/lock───► API ──INSERT … ON CONFLICT … WHERE holder is gone──► record_locks
Tab B ──POST /records/7/lock {takeover}──► API ──FOR UPDATE, replace holder──► record_locks
                                           └──► event bus ──"lock-lost"──► Tab A (read-only)
Tab A ──PUT /records/7──► API ──holder check in the same transaction──► 423 Locked
```

- **Identity.** Each tab makes a random `tabId` on load and sends it as `X-Tab-Id`. The API hands back a separate
  `publicId`, which is the only id other tabs ever see, so nobody can impersonate another tab. A real product would put
  user authentication in front of this; the per-tab id is still what keeps two tabs of the same user apart.
- **Liveness.** A lock counts only while its holder's session was seen in the last 30 s (`Locks:TtlSeconds`). The
  SSE endpoint refreshes that every 10 s. A closed tab frees its locks about 5 s later (a short grace period lets a
  flaky connection reconnect without losing its lock); a crashed instance's locks expire after the TTL and the sweeper
  tells every tab.

### Design choices worth discussing

- **SSE instead of WebSockets.** Traffic is one-way (server to browser); writes are plain HTTP requests that go through
  the normal validation, rate limiting and status codes. SSE is plain HTTP, reconnects on its own, and passes through
  proxies and load balancers without special setup. The trade-off: `EventSource` cannot send headers, so the tab id
  rides in the query string.
- **The database is the source of truth.** Lock state lives in Postgres, not in API memory, so any instance can answer
  any request. Only the delivery of events is per-instance, and `LISTEN/NOTIFY` closes that gap.
- **Lock plus version.** The lock stops concurrent editing; the `xmin` version still protects against anything that
  slips past it (for example a save that was in flight during a takeover).

## Run it locally

Requirements: Docker. For development without containers: .NET 10 SDK and Node 24 (`.nvmrc`).

```bash
docker compose up --build
```

- App: http://localhost:8081
- API health: http://localhost:8080/api/health

The compose stack runs the API with `Events__Mode=Postgres`, the multi-instance path.

Development mode:

```bash
docker compose up -d db
cd backend && dotnet run --project src/LockDemo.Api      # http://localhost:5xxx, see launchSettings
cd frontend && npm ci && npx ng serve                     # http://localhost:4200 (Node 18.19+, 20 or 22+)
```

For `ng serve`, point `frontend/public/config.json` at the port `dotnet run` prints.

### Why Angular 18

The UI uses **PrimeNG 17.18.15, the last PrimeNG release under the MIT license**. Later releases (v18 and up) use
PrimeTek's PrimeUI license, which needs a license key. PrimeNG 17 supports Angular 17 and 18 only, so the web app is
pinned to **Angular 18.2**.

Angular 18 is past its support window, and `npm audit` reports high-severity advisories for it that will not be fixed
on that line (XSS sanitization bypasses in the template compiler, XSRF token leakage in the HTTP client). CI runs the
audit on every build and shows the result without blocking. This is a deliberate trade-off for a demo with no sign-in
and no real data; a production app should move to a supported Angular release (and a licensed PrimeNG or another
component library).

The theme is PrimeNG's Aura light theme with the blue accent swapped for sky, generated on `npm install` by
`frontend/scripts/build-theme.mjs` (sky-700 passes WCAG AA with white text; the stock blue does not).

## Tests

```bash
cd backend && dotnet test     # integration tests against a real Postgres via Testcontainers (needs Docker)
cd frontend && npm run test:ci      # Jasmine + Karma in headless Chrome
```

The backend tests cover exclusive locks, takeover with the warning and rejected save, stale locks, the keyset
"next unlocked" query, version conflicts, saving without a lock, the SSE greeting and keyset paging.

## Deploy for free

| Part | Service | Notes |
| --- | --- | --- |
| Database | [Neon](https://neon.tech) free plan | Use the **direct** connection string (not `-pooler`) if you switch to `Events__Mode=Postgres` |
| API | [Render](https://render.com) free web service, Docker | Sleeps after ~15 min idle; the first request then takes about a minute |
| Web | GitHub Pages | Built and published by `.github/workflows/deploy-web.yml` |

1. **Neon:** create a project and copy the connection details into Npgsql form:
   `Host=ep-xxx.eu-central-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=…;SSL Mode=Require`
2. **Render:** New > Blueprint > this repository (it reads `render.yaml`). When asked, set
   `ConnectionStrings__Default` to the Neon string and `Cors__AllowedOrigins__0` to your Pages origin, for example
   `https://<user>.github.io`. Render deploys after the CI checks on `main` pass. The API creates its tables and demo
   data on first start.
3. **GitHub:** Settings > Pages > Source: GitHub Actions. Then Settings > Secrets and variables > Actions:
   - variable `API_URL` = your Render URL, for example `https://sse-lock-demo-api.onrender.com`
4. Push to `main`. CI runs the tests and builds both images; then Pages publishes the web app.

## Security notes

- No secrets in the repository: the connection string comes from the host's environment.
- API: CORS limited to configured origins and the `X-Tab-Id` header; per-IP rate limits on writes and open streams;
  input length and enum validation; security headers; the containers run as non-root users.
- Web: strict Content Security Policy in the nginx image (no inline scripts), runtime `config.json` served with `no-store`.
- It is a public demo without sign-in: anyone can edit the sample records. Do not put real data in it.
- The web app runs on Angular 18, which has known unpatched advisories; see "Why Angular 18".

## Project layout

```
backend/
  src/LockDemo.Api/
    Data/        EF Core model, migrations, seed data
    Locking/     LockService (acquire, takeover, save), tab sessions, heartbeat and sweeper
    Events/      SSE endpoint, tab registry, in-memory and Postgres LISTEN/NOTIFY buses
    Records/     HTTP endpoints and validation
  tests/LockDemo.Api.Tests/   Testcontainers integration tests
frontend/
  src/app/core/  runtime config, SSE tab session, typed API client
  src/app/queue/ the review queue page (PrimeNG table, editor, takeover dialog)
  scripts/       build-theme.mjs: sky-accent PrimeNG theme, generated on install
docker-compose.yml   local stack
render.yaml          API on Render
.github/workflows/   CI and Pages deployment
```
