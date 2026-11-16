# Alteva Crawler

A website crawler with a .NET 8 API and worker, RabbitMQ, PostgreSQL, and a Next.js/React UI using shadcn. Submit a public URL, follow progress, inspect page/link ratios, cancel unfinished work, and reopen jobs from history.

## Run locally

Requires Docker with Compose 2.24.4+ (the isolated test override uses `!override`).

```sh
docker compose up --build -d
```

- Website: http://localhost:3000
- API: http://localhost:5080
- RabbitMQ management: http://localhost:15673
- PostgreSQL: `localhost:55432`, database `crawler`

Local PostgreSQL/RabbitMQ credentials are `crawler` / `crawler-local`. Ports bind to loopback. Migrations run automatically before API/worker startup; named volumes retain data across restarts. Stop with `docker compose down`.

## Architecture and API

```text
web/                  Next.js UI, same-origin API proxy, query polling
src/Crawler.Api/      Create/cancel jobs; serve summaries, trees, history
src/Crawler.Worker/   Consume tasks, fetch HTML, parse links, store results
src/Crawler.Shared/  Entities, URL/ratio rules, SQL and messaging infrastructure
tests/                Focused .NET unit tests
fixtures/, scripts/  Deterministic full-system and failure checks
```

API and Worker are separate processes; Shared is a referenced library. PostgreSQL stores `jobs`, `pages`, outgoing `links`, and a small `crawl_outbox`. `parent_page_id` records the first-discovery tree; unique page/link keys and indexed job/status lookups support deduplication and progress queries.

| Method | Route | Purpose |
| --- | --- | --- |
| POST | `/api/jobs` | Create; returns `201`, `{ jobId }`, and Location |
| GET | `/api/jobs/{jobId}` | Status, timestamps, limits, failure reason, progress |
| GET | `/api/jobs/{jobId}/tree` | Nested pages, ratios, redirects, HTTP metadata/errors |
| GET | `/api/jobs?page=1&pageSize=20` | Newest-first history; page size 1–100 |
| POST | `/api/jobs/{jobId}/cancel` | Cancel active work; preserve collected results |

```sh
curl -i http://localhost:5080/api/jobs \
  -H 'Content-Type: application/json' \
  -d '{"url":"https://www.python.org/","maxDepth":2}'
```

`maxDepth` defaults to 2 (allowed 0–5); the root is depth 0. The default page cap is 200, configurable with API setting `Crawl__MaxPages` (1–1000). Each create request intentionally starts a new job. Invalid input returns 400 Problem Details; unknown jobs return 404.

## Crawling and lifecycle

- Process HTML/XHTML only; resolve relative/base/redirect URLs, remove fragments, and deduplicate normalized URLs. Preserve query strings and trailing slashes; store final redirect URL separately from requested identity.
- Schedule only the exact initial host, case-insensitively. Subdomains/`www` variants are external. Ratio = distinct internal HTTP(S) outgoing links / all distinct HTTP(S) outgoing links; zero links gives 0. Non-HTTP schemes are excluded.
- Enforce depth/page budgets while reserving children under a job-row lock. HTTP attempts allow 10 seconds, five redirects, and a 2 MiB decompressed body; destinations are checked against the worker's IP policy.
- Root failure fails the job. Child failures remain visible while other work continues; Completed means the frontier drained, with failed/skipped counts retained.
- Cancellation atomically marks the job/unfinished pages Canceled. Workers check every 500 ms during slow HTTP/retry waits and reject late writes under the job lock. Repeated cancellation preserves the original timestamp; already Completed/Failed jobs return 409. Requests already sent to a website cannot be undone.

Both services expose `/health/live` and `/health/ready` (worker on port 5081). UTC JSON logs include request/job/page/correlation IDs, attempts, timing, and outcomes; query strings and response bodies are omitted.

## Messaging and recovery

Work queue: `crawl-page-v2`. Dead-letter queue: `crawl-page-v2-dead`. Both are durable; tasks use persistent delivery through RabbitMQ's default exchange. Plain JSON schema:

```json
{
  "jobId": "<public UUID>",
  "pageId": 1,
  "url": "https://www.python.org/",
  "depth": 0,
  "occurredAt": "2026-11-16T18:00:00Z",
  "schemaVersion": 1
}
```

AMQP `message_id` is the outbox UUID; `correlation_id` is the public job UUID.

- **Reliable publication:** job/page changes and task intents commit together. Dispatchers claim outbox rows with `FOR UPDATE SKIP LOCKED`, require routing and publisher confirmation, then delete delivered rows. Broker failures retain rows for retry after five seconds; blocked publishing has a ten-second deadline.
- **Idempotency:** a per-page transaction advisory lock serializes duplicate deliveries, including different message IDs. Terminal pages are no-ops; unique `(job_id, url)` and `(source_page_id, url)` keys are final guards. Results, links, children, and child task intents commit together; acknowledge only afterward. HTTP may repeat after an uncommitted crash, while stored results remain idempotent.
- **Retries:** HTTP has two retries for network/stream errors, timeouts, and 408/429/5xx; jitter and Retry-After (capped at 30 seconds) apply. Task processing has three attempts with 1/3-second waits. Most 4xx, unsafe destinations, and oversize responses fail without HTTP retry; unsupported content is Skipped. Transient DB outages retain unacknowledged work and retry every five seconds without consuming the page retry budget.
- **DLQ:** malformed/mismatched tasks retain their original bytes without mutating a legitimate page. Exhausted valid tasks atomically record failure and a dead-letter intent. Headers include `x-failure-reason` and `x-original-message-id`. Inspect in RabbitMQ management; fix the cause before replay. Terminal pages stay no-ops, so recrawling a failed URL requires a new job.

## Tests and CI

For local quality checks, install .NET 8 and Node 22:

```sh
dotnet restore Crawler.sln --locked-mode
dotnet test Crawler.sln
cd web
npm ci
npm run lint
npm run typecheck
npm run build
```

Run the real API/broker/worker/database/frontend smoke check in an isolated project:

```sh
export COMPOSE_PROJECT_NAME=alteva-audit
export COMPOSE_FILE=compose.yaml:compose.fixture.yaml:compose.audit.yaml
docker compose up --build -d
python3 scripts/smoke.py
docker compose down --volumes --remove-orphans
unset COMPOSE_PROJECT_NAME COMPOSE_FILE
```

The smoke script waits for readiness and checks the web proxy, default depth, pagination, ratios, failures/skips, redirects, DLQ, duplicate tasks, page cap, and worker crash recovery. Additional local probes in the same audit stack: `scripts/exercise.py` (concurrency/outages), `scripts/cancellation.py`, and `scripts/crash_windows.py`. All restart services and require `alteva-audit`; the private fixture host is allowed only in Testing.

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs on pushes, pull requests, and manual dispatch. `quality` builds/tests .NET and installs/lints/type-checks/builds the frontend. Dependent job `smoke` starts the complete Compose stack and runs the fixture smoke script. Failure logs are saved as `smoke-logs` for seven days; test containers and volumes are always torn down.

## Priorities and limitations

Prioritized durable job creation, SQL constraints, and reliable worker processing before read APIs and UI. Included cancellation; kept polling instead of SSE/SignalR to keep the flow explainable. Cut JavaScript rendering, authentication/bot-protection bypass, sitemap ingestion, and an automatic DLQ replay UI. The tree uses first discovery rather than guaranteed shortest paths; redirect aliases remain separate identities, and there is no cross-job deduplication.

With more time: per-host throttling/robots.txt, operational DLQ replay, and deployment/security hardening. This is a local assessment application, not an authenticated public crawler service.
