"""Destructive fault/concurrency exercise, restricted to the isolated alteva-audit stack.

Start compose.yaml + compose.fixture.yaml + compose.audit.yaml with project alteva-audit.
This script sends real HTTP/AMQP requests, inspects PostgreSQL, and stops/restarts services.
"""
import base64
import concurrent.futures
import json
import os
import subprocess
import time
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

if os.environ.get("COMPOSE_PROJECT_NAME") != "alteva-audit":
    raise SystemExit("Set COMPOSE_PROJECT_NAME=alteva-audit and COMPOSE_FILE to the three compose files.")
API = "http://localhost:5180"
FIXTURE = "http://fixtures:8090"
FIXTURE_ADMIN = "http://localhost:5190"
SECRET = "audit-secret-query-must-not-appear-in-logs"
created = []


def http(path, body=None, headers=None, base=API):
    """Returns status, headers, and decoded JSON/text, including HTTP error responses. Raw
    bytes can exercise invalid JSON; transport failures still raise.
    """
    payload = body if isinstance(body, bytes) else None if body is None else json.dumps(body).encode()
    req = Request(base + path, data=payload, headers={"Content-Type": "application/json", **(headers or {})})
    try:
        response = urlopen(req, timeout=12)
    except HTTPError as error:
        response = error
    with response:
        raw = response.read()
        try:
            data = json.loads(raw)
        except ValueError:
            data = raw.decode(errors="replace")
        return response.status, dict(response.headers), data


def compose(*args):
    """Runs Docker Compose in the audit project selected by environment variables; raises on
    CLI failure. Used to stop/restart only the isolated test services.
    """
    return subprocess.run(["docker", "compose", *args], text=True, capture_output=True, check=True, timeout=60).stdout.strip()


def sql(query):
    """Runs SQL directly in audit PostgreSQL and returns plain text, providing storage/lock
    checks independent of API responses.
    """
    return subprocess.run(["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler", "-d", "crawler", "-Atq", "-v", "ON_ERROR_STOP=1"],
                          input=query, text=True, capture_output=True, check=True, timeout=30).stdout.strip()


def get(path):
    """Requires HTTP 200 and returns the response body so later assertions cannot accidentally
    inspect an error page.
    """
    status, _, data = http(path)
    assert status == 200, (status, data)
    return data


def create(path, depth=0, host=FIXTURE):
    """Creates a real fixture job, verifies its UUID and Location header, and records its ID
    for the exercise.
    """
    status, headers, data = http("/api/jobs", {"url": host + path, "maxDepth": depth})
    assert status == 201, (status, data)
    job_id = str(uuid.UUID(data["jobId"]))
    assert headers["Location"] == "/api/jobs/" + job_id
    created.append(job_id)
    return job_id


def wait(job_id, predicate=None, seconds=90):
    """Polls until a requested/terminal state while checking that progress categories always
    sum to the same total snapshot.
    """
    predicate = predicate or (lambda item: item["status"] not in ("Pending", "Running"))
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        data = get("/api/jobs/" + job_id)
        assert sum(data["progress"][key] for key in ("pending", "processing", "completed", "failed", "skipped", "canceled")) == data["progress"]["total"]
        if predicate(data):
            return data
        time.sleep(0.15)
    raise AssertionError(("Unfinished job", data))


def tree(job_id):
    """Reads the nested page-result response for one public UUID."""
    return get(f"/api/jobs/{job_id}/tree")


def broker(path, body=None):
    """Calls the real RabbitMQ management API with local test credentials to inspect or publish
    broker messages.
    """
    auth = base64.b64encode(b"crawler:crawler-local").decode()
    status, _, data = http("/api/" + path, body, {"Authorization": "Basic " + auth}, "http://localhost:15674")
    assert status == 200, (status, data)
    return data


def publish(envelope):
    """Injects plain task JSON or deliberately broken text with persistent delivery mode.
    Asserts routing succeeded so deduplication tests cannot pass because messages were
    dropped.
    """
    payload = envelope if isinstance(envelope, str) else json.dumps(envelope["message"])
    result = broker("exchanges/%2F/amq.default/publish", {
        "properties": {"content_type": "application/json", "delivery_mode": 2, "message_id": str(uuid.uuid4()) if isinstance(envelope, str) else envelope["messageId"]},
        "routing_key": "crawl-page-v2", "payload": payload, "payload_encoding": "string",
    })
    assert result["routed"]


def healthy():
    """Waits for API dependency readiness after service recovery; connection failures during
    startup are expected until the deadline.
    """
    for attempt in range(80):
        try:
            if http("/health/ready")[0] == 200:
                return
        except (OSError, URLError):
            pass
        time.sleep(.25)
    raise AssertionError("API did not become ready")


extra_worker = None
try:
    # Fail immediately on worker startup/configuration errors instead of queuing jobs to an absent consumer.
    with urlopen("http://localhost:5181/health/ready", timeout=5) as response:
        assert response.status == 200
    http("/reset", {}, base=FIXTURE_ADMIN)
    extra_worker = compose("run", "--no-deps", "-d", "--name", "alteva-audit-worker-extra", "worker").splitlines()[-1]
    timeout_job = create("/timeout")

    # Same POST URL intentionally creates separate jobs; idempotence applies to their page tasks.
    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=16) as pool:
        concurrent_jobs = list(pool.map(lambda _: create("/?token=" + SECRET, 2), range(40)))
    assert len(set(concurrent_jobs)) == 40
    elapsed = time.monotonic() - started
    with concurrent.futures.ThreadPoolExecutor(max_workers=20) as pool:
        list(pool.map(lambda index: get("/api/jobs/" + concurrent_jobs[index % 40]), range(200)))
    for job_id in concurrent_jobs:
        data = wait(job_id)
        assert data["status"] == "Completed" and data["progress"]["total"] == 6, data
    print(f"40 concurrent create requests: 40 unique jobs; 200 concurrent summary reads; all crawls completed ({elapsed:.2f}s to create).", flush=True)

    before = int(sql("SELECT count(*) FROM jobs;"))
    invalid = [b"{", b"null", [], {}, {"url": None}, {"url": True}, {"url": 42}, {"url": ""}, {"url": FIXTURE + "/" + "a" * 2001}, {"url": "/relative"}, {"url": "file:///etc/passwd"},
               {"url": "javascript:alert(1)"}, {"url": "https://u:password@example.com/"}]
    invalid += [{"url": "https://example.com", "maxDepth": value} for value in (-1, 6, 1.5, "2", True, 2147483648)]
    for body in invalid:
        status, headers, _ = http("/api/jobs", body)
        assert status == 400 and "application/problem+json" in headers["Content-Type"], (body, status, headers)
    status, headers, _ = http("/api/jobs", b"x" * (17 * 1024))
    assert status == 413 and "application/problem+json" in headers["Content-Type"], (status, headers)
    assert http("/api/jobs", b"{}", {"Content-Type": "text/plain"})[0] == 415
    assert int(sql("SELECT count(*) FROM jobs;")) == before
    for path, status in [("/api/jobs?page=0", 400), ("/api/jobs?pageSize=101", 400), ("/api/jobs?page=abc", 400),
                         ("/api/jobs/not-a-uuid", 404), ("/api/jobs/" + str(uuid.uuid4()), 404)]:
        assert http(path)[0] == status, path
    print("Invalid JSON, types, bounds, schemes, credentials, oversized bodies, media types, and pagination rejected without inserting jobs.", flush=True)

    expected_failures = []
    for path in ("/loop", "/bad-redirect", "/stream-large", "/gzip-large", "/truncated", "/poison?token=" + SECRET):
        job_id = create(path)
        assert wait(job_id)["status"] == "Failed", path
        expected_failures.append(job_id)
        root = tree(job_id)[0]
        assert root["httpStatus"] == (302 if path in ("/loop", "/bad-redirect") else 503 if path.startswith("/poison") else 200), root
        assert root["finalUrl"] and root["contentType"] == "text/html", root
    for host in ("http://127.0.0.1", "http://[::1]", "http://169.254.169.254", "http://172.17.0.1"):
        job_id = create("/", host=host)
        assert wait(job_id)["status"] == "Failed", host
    job_id = create("/", host="http://fixtures:1")
    assert wait(job_id)["status"] == "Failed" and tree(job_id)[0]["httpStatus"] is None
    rate_started = time.monotonic()
    assert wait(create("/rate-limit"))["status"] == "Completed"
    assert time.monotonic() - rate_started >= 1
    assert wait(timeout_job)["status"] == "Completed"
    for path in ("/xhtml", "/latin1", "/dense", "/trap", "/cookies"):
        job_id = create(path, 1 if path in ("/cookies", "/trap") else 0)
        assert wait(job_id)["status"] == "Completed", path
        root = tree(job_id)[0]
        if path == "/dense": assert root["totalLinkCount"] == 1
        if path == "/trap": assert get("/api/jobs/" + job_id)["progress"]["total"] == 2
        if path == "/latin1":
            assert sql(f"SELECT l.url FROM links l JOIN pages p ON p.id=l.source_page_id JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job_id}';").endswith("/caf%C3%A9")
        if path == "/cookies": assert all(child["totalLinkCount"] == 0 for child in root["children"])
    assert wait(create("/no-type"))["progress"]["skipped"] == 1
    fetch_counts = http("/counts", base=FIXTURE_ADMIN)[2]
    assert fetch_counts["/poison"] == 9 and fetch_counts["/truncated"] == 9, fetch_counts
    assert fetch_counts["/timeout"] == 3 and fetch_counts["/rate-limit"] == 2, fetch_counts
    print("Verified actual HTTP budgets: exhausted tasks made 3 x 3 fetch attempts; timeout recovery used 3; Retry-After recovery used 2.", flush=True)
    print("Redirect loops/invalid schemes, streaming and compressed oversize, truncated responses, HTTP 503/429, timeouts, private IPs, refused connections, legacy encoding, XHTML, dense duplicates, cycles, and cookie isolation verified.", flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        wide = list(pool.map(lambda _: create("/wide", 2), range(3)))
    for job_id in wide:
        data = wait(job_id)
        assert data["status"] == "Completed" and data["progress"]["total"] == 200 and data["progress"]["failed"] == 0, data
    assert sql("SELECT count(*) FROM (SELECT job_id,url FROM pages GROUP BY job_id,url HAVING count(*)>1) d;") == "0"
    assert sql("SELECT count(*) FROM pages c JOIN pages p ON p.id=c.parent_page_id WHERE c.job_id<>p.job_id OR c.depth<>p.depth+1;") == "0"
    print("Two worker processes, three simultaneous overlapping 200-page crawls: no overshoot, duplicate URLs, cross-job parents, or depth errors.", flush=True)

    compose("stop", "-t", "2", "worker")
    subprocess.run(["docker", "stop", "-t", "2", extra_worker], check=True, capture_output=True)
    duplicate_job = create("/slow?token=" + SECRET)
    time.sleep(1.2)  # Allow the outbox dispatcher to send while workers are stopped.
    messages = broker("queues/%2F/crawl-page-v2/get", {"count": 100, "ackmode": "ack_requeue_true", "encoding": "auto"})
    original_item = next(item for item in messages if json.loads(item["payload"])["jobId"] == duplicate_job)
    original = {"messageId": original_item["properties"]["message_id"], "message": json.loads(original_item["payload"])}
    baseline = http("/counts", base=FIXTURE_ADMIN)[2].get("/slow", 0)
    copies = [dict(original, messageId=original["messageId"] if index < 5 else str(uuid.uuid4())) for index in range(30)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        list(pool.map(publish, copies))
    # Invalid tasks arrive while the legitimate page is still Pending; they must not fail or mutate it.
    invalid_messages = [
        {**original["message"], "jobId": str(uuid.uuid4())},
        {**original["message"], "url": FIXTURE + "/tampered"},
        {**original["message"], "depth": 5},
    ]
    invalid_ids = [str(uuid.uuid4()) for _ in invalid_messages]
    for invalid_id, invalid_message in zip(invalid_ids, invalid_messages):
        publish({"messageId": invalid_id, "message": invalid_message})
    compose("start", "worker")
    subprocess.run(["docker", "start", extra_worker], check=True, capture_output=True)
    assert wait(duplicate_job)["status"] == "Completed"
    time.sleep(2)
    assert http("/counts", base=FIXTURE_ADMIN)[2]["/slow"] - baseline == 1
    assert get("/api/jobs/" + duplicate_job)["progress"]["total"] == 1
    publish(dict(original, messageId=str(uuid.uuid4()), message={**original["message"], "schemaVersion": 999}))
    publish("{broken-json")
    for attempt in range(60):
        dead_letters = broker("queues/%2F/crawl-page-v2-dead/get", {
            "count": 100, "ackmode": "ack_requeue_true", "encoding": "auto",
        })
        retained_ids = {item["properties"].get("headers", {}).get("x-original-message-id") for item in dead_letters}
        if set(invalid_ids) <= retained_ids and any(item["payload"] == "{broken-json" for item in dead_letters):
            break
        time.sleep(.1)
    else:
        raise AssertionError("Invalid tasks were not retained in the dead-letter queue")
    assert get("/api/jobs/" + duplicate_job)["status"] == "Completed"
    print("30 simultaneous duplicates (same and distinct envelope IDs) across two workers: one HTTP fetch, one page result; mismatched tasks and malformed JSON retained in DLQ without corrupting the valid job.", flush=True)

    compose("stop", "-t", "3", "rabbitmq")
    offline_jobs = [create("/empty") for _ in range(5)]
    assert int(sql('SELECT count(*) FROM crawl_outbox;')) >= 5
    compose("restart", "api")
    for attempt in range(60):
        try:
            if http("/health/live")[0] == 200: break
        except (OSError, URLError): pass
        time.sleep(.2)
    offline_jobs.append(create("/empty"))
    assert http("/health/ready")[0] == 503
    compose("start", "rabbitmq")
    healthy()
    for job_id in offline_jobs: assert wait(job_id)["status"] == "Completed"
    assert sql('SELECT count(*) FROM crawl_outbox;') == "0"
    print("Broker outage + API restart: requests still accepted, readiness unhealthy, committed outbox tasks delivered and completed after recovery.", flush=True)

    database_job = create("/slow")
    wait(database_job, lambda data: data["progress"]["processing"] == 1)
    compose("stop", "-t", "2", "postgres")
    status, headers, body = http("/api/jobs/" + database_job)
    assert status == 500 and "application/problem+json" in headers["Content-Type"], (status, headers, body)
    assert "Npgsql" not in json.dumps(body) and "Password" not in json.dumps(body)
    assert http("/health/live")[0] == 200 and http("/health/ready")[0] == 503
    assert http("/api/jobs", {"url": FIXTURE + "/should-not-exist"})[0] == 500
    compose("start", "postgres")
    healthy()
    assert wait(database_job)["status"] in ("Completed", "Failed")
    assert sql("SELECT count(*) FROM jobs WHERE url LIKE '%should-not-exist%';") == "0"
    print("Database outage: safe 500 Problem Details, liveness/readiness separation, failed create rolled back; in-flight job reached a terminal state after recovery.", flush=True)

    # Exceed the normal task retry budget: a database outage must retain the original delivery.
    long_outage_job = create("/slow")
    wait(long_outage_job, lambda data: data["progress"]["processing"] == 1)
    compose("stop", "-t", "2", "postgres")
    print("Holding database offline for 35 seconds to verify durable dependency recovery…", flush=True)
    time.sleep(35)
    compose("start", "postgres")
    healthy()
    recovered = wait(long_outage_job)
    assert recovered["status"] == "Completed" and recovered["progress"]["total"] == 1, recovered
    print("Extended DB outage recovered automatically; the original task was not lost or stranded in Processing.", flush=True)

    # Recovery must not depend on a new POST producing an outbox row.
    assert sql("SELECT count(*) FROM crawl_outbox;") == "0"
    compose("stop", "-t", "3", "rabbitmq")
    assert http("/health/ready")[0] == 503
    compose("start", "rabbitmq")
    healthy()
    for attempt in range(80):
        if http("/health/ready", base="http://localhost:5181")[0] == 200:
            break
        time.sleep(.25)
    else:
        raise AssertionError("Idle worker did not reconnect")
    assert sql("SELECT count(*) FROM crawl_outbox;") == "0"
    print("Idle broker restart: API and all worker consumers recovered readiness with an empty outbox and no new job submission.", flush=True)

    # Inspect actual logs rather than accepting green response assertions alone.
    logs = compose("logs", "--no-log-prefix", "api", "worker")
    logs += subprocess.run(["docker", "logs", extra_worker], capture_output=True, text=True, check=True).stdout
    assert SECRET not in logs, "A query-string secret leaked into application logs"
    records = []
    for line in logs.splitlines():
        try: records.append(json.loads(line))
        except ValueError: pass
    requests = [row for row in records if row.get("Category", "").endswith("RequestLoggingMiddleware")]
    assert requests and all("DurationMs" in row["State"] and "StatusCode" in row["State"] for row in requests)
    assert any(row["State"]["StatusCode"] == 500 for row in requests)
    processing = [row for row in records if row.get("Message") == "Processing page"]
    assert processing
    delivery_errors = [row for row in records if row.get("Message", "").startswith("Outbox delivery unavailable")]
    assert len(delivery_errors) < 100, "Outbox delivery errors are spinning rather than backing off"
    assert not any(marker in str(row) for row in records for marker in ("ObjectDisposedException", "OutOfMemoryException", "40P01", "23505"))
    assert all("Timestamp" in row for row in records)
    assert all(all(key in {key: value for scope in row["Scopes"] if isinstance(scope, dict) for key, value in scope.items()}
                   for key in ("jobId", "pageId", "correlationId", "messageId", "attempt")) for row in processing)
    assert sql("SELECT count(*) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.status IN ('Completed','Failed') AND p.status IN ('Pending','Processing');") == "0"
    assert sql("SELECT count(*) FROM jobs WHERE status IN ('Pending','Running');") == "0"
    print(f"Inspected {len(records)} structured log records: request duration/status, worker correlation/attempt scopes, expected failures, no query-secret leakage, and no stranded jobs.", flush=True)
    print("All adversarial runtime checks passed.", flush=True)
finally:
    # Even a failed assertion must leave this isolated stack recoverable.
    for service in ("postgres", "rabbitmq", "api", "worker"):
        try: compose("start", service)
        except subprocess.SubprocessError: pass
    if extra_worker:
        subprocess.run(["docker", "rm", "-f", extra_worker], capture_output=True)
