"""Full API/broker/worker/DB smoke check against the isolated alteva-audit stack.

Creates fixture jobs; deliberately restarts the worker to verify recovery.
Run from the repository root after starting the fixture stack.
"""

import base64
import json
import os
import subprocess
import time
import uuid
from urllib.error import URLError
from urllib.request import Request, urlopen

if os.environ.get("COMPOSE_PROJECT_NAME") != "alteva-audit":
    raise SystemExit("Smoke checks restart the worker; use COMPOSE_PROJECT_NAME=alteva-audit.")

API = os.environ.get("CRAWLER_API_URL", "http://localhost:5180") + "/api/jobs"
FIXTURE_SERVER = os.environ.get("CRAWLER_FIXTURE_URL", "http://localhost:5190")
BROKER = os.environ.get("CRAWLER_BROKER_URL", "http://localhost:15674")
WORKER = os.environ.get("CRAWLER_WORKER_URL", "http://localhost:5181")
WEB = os.environ.get("CRAWLER_WEB_URL", "http://localhost:3100")
FIXTURE = "http://fixtures:8090"


def request(url, body=None, headers=None):
    """Sends JSON to an HTTP endpoint and returns decoded JSON. HTTP/connection errors raise
    instead of silently making an assertion look successful.
    """
    data = None if body is None else json.dumps(body).encode()
    with urlopen(Request(url, data=data, headers={"Content-Type": "application/json", **(headers or {})}), timeout=10) as response:
        return json.load(response)


def create(path, depth=2):
    """Creates a real fixture crawl through the API and returns its public job UUID; this is
    not an in-memory/mock job.
    """
    return request(API, {"url": FIXTURE + path, "maxDepth": depth})["jobId"]


def wait_for(job_id, predicate, seconds=60):
    """Polls a job until the supplied condition is true and returns that summary; a timeout
    fails with the last observed state.
    """
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        summary = request(f"{API}/{job_id}")
        if predicate(summary):
            return summary
        time.sleep(0.2)
    raise AssertionError(f"Timed out: {summary}")


def terminal(job_id):
    """Waits until the job is Completed, Failed, or Canceled rather than assuming asynchronous
    work finished after a fixed sleep.
    """
    return wait_for(job_id, lambda job: job["status"] in ("Completed", "Failed", "Canceled"))


def sql(query):
    """Runs SQL directly in audit PostgreSQL and returns plain text, providing storage/lock
    checks independent of API responses.
    """
    result = subprocess.run(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler", "-d", "crawler", "-At"],
        input=query, text=True, capture_output=True, check=True,
    )
    return result.stdout.strip()


def broker(path, body=None):
    """Calls the real RabbitMQ management API with local test credentials to inspect or publish
    broker messages.
    """
    auth = base64.b64encode(b"crawler:crawler-local").decode()
    return request(BROKER + "/api/" + path, body, {"Authorization": "Basic " + auth})


def matches_job(item, job_id):
    # A dead-letter queue can also contain deliberately malformed JSON from the adversarial exercise.
    """Finds a job in a plain-JSON dead letter while tolerating intentionally malformed bodies
    from other adversarial checks.
    """
    try:
        envelope = json.loads(item["payload"])
    except ValueError:
        return False
    return isinstance(envelope, dict) and envelope.get("jobId") == job_id


def wait_until_ready(url):
    # Compose's detached start can return before ASP.NET is accepting requests. Retry only this read,
    # never a failed POST whose transaction might already have committed without its response arriving.
    """Waits for a health/read endpoint before any mutation. Retries only safe reads, since
    blindly retrying a POST could create a second job after a lost response.
    """
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        try:
            with urlopen(url, timeout=3) as response:
                if response.status == 200:
                    return
        except (OSError, URLError):
            pass
        time.sleep(.2)
    raise AssertionError("Service did not become ready: " + url)


wait_until_ready(API.removesuffix("/api/jobs") + "/health/ready")
wait_until_ready(WORKER + "/health/ready")
wait_until_ready(WEB)
assert isinstance(request(WEB + "/api/jobs?pageSize=1")["items"], list)
print("Frontend readiness and same-origin API proxy passed.", flush=True)
request(FIXTURE_SERVER + "/reset", {})
# Omit maxDepth on the wire: a helper that always supplies 2 would not test the API's default.
job_id = request(API, {"url": FIXTURE + "/"})["jobId"]
summary = terminal(job_id)
assert summary["maxDepth"] == 2, summary
assert summary["status"] == "Completed" and summary["progress"]["completed"] == 5, summary
tree = request(f"{API}/{job_id}/tree")
assert tree[0]["linkRatio"] == 0.666667 and len(tree[0]["children"]) == 2, tree
zero_depth = create("/", 0)
one_depth = create("/", 1)
assert terminal(zero_depth)["progress"]["total"] == 1
assert terminal(one_depth)["progress"]["total"] == 3
print("Traversal, exact ratio, duplicate links, default depth, and depth limits passed.", flush=True)

# Expected order comes from our known creation sequence, independently of the query implementation.
first_page = request(API + "?page=1&pageSize=2")
second_page = request(API + "?page=2&pageSize=2")
first_ids = [item["jobId"] for item in first_page["items"]]
second_ids = [item["jobId"] for item in second_page["items"]]
assert first_ids == [one_depth, zero_depth] and second_ids[0] == job_id, (first_page, second_page)
assert not set(first_ids) & set(second_ids), (first_page, second_page)
assert first_page["page"] == 1 and second_page["page"] == 2
assert first_page["pageSize"] == second_page["pageSize"] == 2
assert first_page["total"] == second_page["total"] == int(sql("SELECT count(*) FROM jobs;"))
print("Newest-first history ordering, page boundaries, totals, and non-overlapping results passed.", flush=True)

partial = terminal(create("/partial", 1))
assert partial["status"] == "Completed", partial
assert partial["progress"] == {"total": 4, "pending": 0, "processing": 0, "completed": 2, "failed": 1, "skipped": 1, "canceled": 0}, partial
for path in ("/missing", "/large", "/unsafe-redirect"):
    failed = terminal(create(path))
    assert failed["status"] == "Failed" and failed["failureReason"], failed
assert terminal(create("/file"))["progress"]["skipped"] == 1
redirected = create("/redirect")
assert terminal(redirected)["progress"]["completed"] == 3
redirect_tree = request(f"{API}/{redirected}/tree")
assert redirect_tree[0]["children"][0]["url"].endswith("/redirected/relative")
assert redirect_tree[0]["children"][0]["children"][0]["url"].endswith("/products/1")
assert redirect_tree[0]["url"].endswith("/redirect")
assert redirect_tree[0]["finalUrl"].endswith("/redirected/")
print("Partial failure, retry recovery, non-HTML skip, redirect/base URL, size, and destination checks passed.", flush=True)

poison = terminal(create("/poison", 0))
assert poison["status"] == "Failed" and "exhausted retries" in poison["failureReason"], poison
poison_tree = request(f"{API}/{poison['jobId']}/tree")
assert poison_tree[0]["httpStatus"] == 503 and poison_tree[0]["contentType"] == "text/html", poison_tree
assert poison_tree[0]["finalUrl"] == FIXTURE + "/poison", poison_tree
for attempt in range(30):
    dead_letters = broker("queues/%2F/crawl-page-v2-dead/get", {
        "count": 100, "ackmode": "ack_requeue_true", "encoding": "auto",
    })
    if any(matches_job(item, poison["jobId"]) for item in dead_letters):
        break
    time.sleep(0.1)
else:
    raise AssertionError("Poison message was not retained in the dead-letter queue")
wide = terminal(create("/wide"))
assert wide["status"] == "Completed" and wide["progress"]["total"] == 200, wide
print("Poison task reached the dead-letter queue and became Failed; concurrent discoveries respected the 200-page limit.", flush=True)

before_counts = request(FIXTURE_SERVER + "/counts")
page_id = int(sql(f"SELECT p.id FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{uuid.UUID(job_id)}' AND p.parent_page_id IS NULL;"))
duplicate_id = str(uuid.uuid4())
for message_id in (duplicate_id, duplicate_id, str(uuid.uuid4())):
    envelope = {
        "messageId": message_id, "correlationId": job_id,
        "message": {"jobId": job_id, "pageId": page_id, "url": FIXTURE + "/", "depth": 0,
                    "occurredAt": "2026-11-16T18:00:00Z", "schemaVersion": 1},
    }
    result = broker("exchanges/%2F/amq.default/publish", {
        "properties": {"content_type": "application/json", "delivery_mode": 2, "message_id": message_id, "correlation_id": job_id},
        "routing_key": "crawl-page-v2", "payload": json.dumps(envelope["message"]), "payload_encoding": "string",
    })
    assert result["routed"]
time.sleep(2)
assert request(FIXTURE_SERVER + "/counts") == before_counts
assert request(f"{API}/{job_id}")["progress"]["total"] == 5
assert sql(f"SELECT count(*) FROM links l JOIN pages p ON p.id=l.source_page_id JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{uuid.UUID(job_id)}';") == "8"
print("Repeated MessageId and different-ID duplicate page tasks caused no refetches or duplicate rows.", flush=True)

recovery = create("/slow", 0)
wait_for(recovery, lambda job: job["progress"]["processing"] == 1)
subprocess.run(["docker", "compose", "kill", "-s", "SIGKILL", "worker"], check=True, capture_output=True)
subprocess.run(["docker", "compose", "start", "worker"], check=True, capture_output=True)
recovered = terminal(recovery)
assert recovered["status"] == "Completed" and recovered["progress"]["total"] == 1, recovered
print("Worker crash during fetch recovered through broker redelivery.", flush=True)
print("All integration smoke checks passed.", flush=True)
