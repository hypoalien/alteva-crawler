"""Five full-system cancellation scenarios using real HTTP, RabbitMQ, SQL, and processes.

Run sequentially with the other probes, only in the isolated alteva-audit stack.
Assertions check persisted results, fixture request/disconnect counts, and transaction races.
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
    raise SystemExit("Cancellation probes require COMPOSE_PROJECT_NAME=alteva-audit.")

API = "http://localhost:5180"
FIXTURE = "http://fixtures:8090"
ADMIN = "http://localhost:5190"
extra_worker = None


def http(path, body=None, base=API, headers=None):
    """Calls the audit API/fixture/broker and returns status plus JSON/text, even for HTTP
    errors. Connection failures still raise and can be asserted by crash scenarios.
    """
    request = Request(base + path, data=None if body is None else json.dumps(body).encode(),
                      headers={"Content-Type": "application/json", **(headers or {})})
    try:
        response = urlopen(request, timeout=12)
    except HTTPError as error:
        response = error
    with response:
        raw = response.read()
        try:
            return response.status, json.loads(raw)
        except ValueError:
            return response.status, raw.decode()


def compose(*args):
    """Runs Docker Compose in the audit project selected by environment variables; raises on
    CLI failure. Used to stop/restart only the isolated test services.
    """
    return subprocess.run(["docker", "compose", *args], capture_output=True, text=True,
                          check=True, timeout=60).stdout.strip()


def sql(query):
    """Runs SQL directly in audit PostgreSQL and returns plain text, providing storage/lock
    checks independent of API responses.
    """
    return subprocess.run(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler",
         "-d", "crawler", "-Atq", "-v", "ON_ERROR_STOP=1"],
        input=query, capture_output=True, text=True, check=True, timeout=30,
    ).stdout.strip()


def until(predicate, description, seconds=40):
    """Polls until a condition returns a truthy result and returns that result; a descriptive
    timeout fails the scenario.
    """
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        result = predicate()
        if result:
            return result
        time.sleep(.1)
    raise AssertionError("Timed out: " + description)


def summary(job):
    """Loads a real job and asserts its status-category counts add up to the total before
    returning it.
    """
    status, result = http("/api/jobs/" + job)
    assert status == 200, (status, result)
    assert sum(result["progress"][key] for key in ("pending", "processing", "completed", "failed", "skipped", "canceled")) == result["progress"]["total"]
    return result


def create(path, depth=2):
    """Creates a real fixture crawl through the API and returns its public job UUID; this is
    not an in-memory/mock job.
    """
    status, result = http("/api/jobs", {"url": FIXTURE + path, "maxDepth": depth})
    assert status == 201, (status, result)
    return result["jobId"]


def cancel(job):
    """Posts cancellation for an existing public UUID and returns status/body, letting
    scenarios distinguish success, conflict, and infrastructure failure.
    """
    return http("/api/jobs/" + job + "/cancel", {})


def counts():
    """Reads fixture HTTP and TCP-disconnect counters, which independently prove whether
    network work really started/stopped.
    """
    return http("/counts", base=ADMIN)[1]


def ready():
    """Performs one API readiness check, returning false while expected
    dependencies/connections are unavailable.
    """
    try:
        return http("/health/ready")[0] == 200
    except (OSError, URLError):
        return False


def workers(running):
    """Starts or stops both worker processes together so queued-task and restart assertions
    cannot be bypassed by the second worker still running.
    """
    compose("start", "worker") if running else compose("stop", "-t", "2", "worker")
    subprocess.run(["docker", "start" if running else "stop", extra_worker], capture_output=True, check=True)


def broker(path, body):
    """Calls the real RabbitMQ management API with local test credentials to inspect or publish
    broker messages.
    """
    credentials = base64.b64encode(b"crawler:crawler-local").decode()
    status, result = http("/api/" + path, body, base="http://localhost:15674",
                          headers={"Authorization": "Basic " + credentials})
    assert status == 200, (status, result)
    return result


def assert_canceled(job):
    """Verifies durable Canceled status/end time and zero active pages in both API progress and
    a direct SQL check; returns the summary for timestamp comparisons.
    """
    result = summary(job)
    assert result["status"] == "Canceled" and result["completedAt"], result
    assert result["progress"]["pending"] == result["progress"]["processing"] == 0, result
    assert sql(f"SELECT count(*) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}' AND p.status IN ('Pending','Processing');") == "0"
    return result


def queued_cancel_is_idempotent():
    """Sends twenty concurrent cancels before workers run; verifies one stable cancellation
    timestamp and zero HTTP requests when queued work later drains.
    """
    workers(False)
    baseline = counts().get("/empty", 0)
    job = create("/empty")
    assert summary(job)["status"] == "Pending"
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        responses = list(pool.map(lambda _: cancel(job), range(20)))
    assert all(status == 200 and result["status"] == "Canceled" for status, result in responses), responses
    assert len({result["completedAt"] for _, result in responses}) == 1
    result = assert_canceled(job)
    assert result["startedAt"] is None and result["progress"]["canceled"] == 1
    workers(True)
    until(lambda: sql("SELECT count(*) FROM crawl_outbox;") == "0", "canceled queued task drain")
    time.sleep(.5)
    assert counts().get("/empty", 0) == baseline
    assert cancel(job)[1]["completedAt"] == result["completedAt"]
    print("1. Pending cancellation: 20 simultaneous cancels returned one stable timestamp; queued/redelivered task caused zero HTTP requests.", flush=True)


def in_flight_cancel_preserves_partial_results():
    """Cancels active response streams across two workers, checks real disconnects/preserved
    results, replays canceled tasks, and interrupts a long HTTP retry delay.
    """
    job = create("/cancel-frontier")
    other = create("/", 2)
    until(lambda: summary(job)["progress"]["completed"] >= 2 and summary(job)["progress"]["processing"] >= 2,
          "partial results and multiple active HTTP requests")
    completed = sql(f"SELECT json_agg(row_to_json(p) ORDER BY p.id) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}' AND p.status='Completed';")
    started = counts().get("/cancel-slow", 0)
    before = time.monotonic()
    status, _ = cancel(job)
    assert status == 200 and time.monotonic() - before < 3
    result = assert_canceled(job)
    assert result["progress"]["completed"] >= 2 and result["progress"]["canceled"] >= 2
    until(lambda: counts().get("/cancel-slow:disconnected", 0) >= counts().get("/cancel-slow", 0),
          "actual TCP disconnects for every started response stream", 4)
    started = counts().get("/cancel-slow", 0)
    after = sql(f"SELECT json_agg(row_to_json(p) ORDER BY p.id) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}' AND p.status='Completed';")
    assert after == completed, "Cancellation changed already collected results"
    page = json.loads(sql(f"SELECT json_build_object('pageId',p.id,'url',p.url,'depth',p.depth) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}' AND p.status='Canceled' LIMIT 1;"))
    for index in range(10):
        body = {**page, "jobId": job, "occurredAt": "2026-11-16T20:00:00Z", "schemaVersion": 1}
        broker("exchanges/%2F/amq.default/publish", {
            "properties": {"delivery_mode": 2, "content_type": "application/json", "message_id": str(uuid.uuid4())},
            "routing_key": "crawl-page-v2", "payload": json.dumps(body), "payload_encoding": "string",
        })
    until(lambda: summary(other)["status"] == "Completed", "unrelated job continues")
    time.sleep(1)
    assert counts().get("/cancel-slow", 0) == started and counts().get("/must-not-follow", 0) == 0
    assert assert_canceled(job)["completedAt"] == result["completedAt"]
    retry_before = counts().get("/cancel-retry", 0)
    retry_job = create("/cancel-retry", 0)
    until(lambda: counts().get("/cancel-retry", 0) > retry_before, "Retry-After response")
    assert cancel(retry_job)[0] == 200
    until(lambda: sql(f"SELECT pg_try_advisory_xact_lock(p.id) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{retry_job}';") == "t",
          "HTTP retry delay interrupted", 3)
    assert assert_canceled(retry_job)["progress"]["canceled"] == 1
    assert counts().get("/cancel-retry", 0) == retry_before + 1
    print("2. Two-worker in-flight cancellation: HTTP streams disconnected, collected results preserved, ten replayed tasks were no-ops, unrelated job completed; 30-second Retry-After wait interrupted without another request.", flush=True)


def cancellation_races_with_completion():
    """Races cancel against completion for thirty jobs, checking each valid winner, immutable
    terminal jobs, and the missing-job response.
    """
    def race(index):
        """Creates one job, varies cancellation timing, and validates either a successful
        cancellation or an unchanged completion winner.
        """
        job = create("/empty", 0)
        time.sleep((index % 5) * .1)
        status, response = cancel(job)
        final = summary(job)
        if status == 200:
            assert final["status"] == response["status"] == "Canceled"
            assert_canceled(job)
        else:
            assert status == 409 and final["status"] == "Completed", (status, response, final)
            assert final["progress"]["canceled"] == 0
        return status
    with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
        results = list(pool.map(race, range(30)))
    completed = create("/empty", 0)
    until(lambda: summary(completed)["status"] == "Completed", "terminal success")
    failed = create("/missing", 0)
    until(lambda: summary(failed)["status"] == "Failed", "terminal failure")
    for job in (completed, failed):
        before = summary(job)
        assert cancel(job)[0] == 409 and summary(job) == before
    assert cancel(str(uuid.uuid4()))[0] == 404
    print(f"3. Thirty cancellation/completion races: {results.count(200)} canceled, {results.count(409)} completion winners; finished/failed jobs unchanged, unknown ID returned 404.", flush=True)


def cancellation_survives_outages_and_restarts():
    """Exercises atomic rollback during API death, cancellation with the broker offline, failed
    DB cancellation, and durable cancellation before worker redelivery.
    """
    workers(False)
    # Interrupt cancellation between the job update and page updates; both must roll back together.
    interrupted_job = create("/empty", 0)
    holder = subprocess.Popen(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler", "-d", "crawler", "-Atq"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    holder.stdin.write("BEGIN; LOCK TABLE pages IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep(7); ROLLBACK;\n")
    holder.stdin.close()
    until(lambda: int(sql("SELECT count(*) FROM pg_locks WHERE relation='pages'::regclass AND mode='AccessExclusiveLock' AND granted;")) > 0,
          "page table locked before cancellation")

    def interrupted_cancel():
        """Returns true only when killing the API breaks the pending cancellation connection; a
        normal HTTP response is not a successful crash-boundary observation.
        """
        try:
            cancel(interrupted_job)
        except (OSError, URLError):
            return True
        return False

    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        attempt = pool.submit(interrupted_cancel)
        until(lambda: int(sql("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%UPDATE pages%';")) > 0,
              "cancellation blocked between job and page writes", 5)
        compose("kill", "-s", "SIGKILL", "api")
        assert attempt.result(), "API did not disconnect the in-flight cancellation"
    holder.wait(timeout=15)
    compose("start", "api")
    until(ready, "API recovery after interrupted cancellation")
    rolled_back = summary(interrupted_job)
    assert rolled_back["status"] == "Pending" and rolled_back["completedAt"] is None
    assert rolled_back["progress"]["pending"] == 1 and rolled_back["progress"]["canceled"] == 0
    assert cancel(interrupted_job)[0] == 200
    assert_canceled(interrupted_job)

    baseline = counts().get("/empty", 0)
    compose("stop", "-t", "3", "rabbitmq")
    job = create("/empty", 0)
    assert cancel(job)[0] == 200
    original = assert_canceled(job)
    compose("restart", "api")
    until(lambda: safe_live(), "API restart without broker")
    assert assert_canceled(job)["completedAt"] == original["completedAt"]
    compose("start", "rabbitmq")
    until(ready, "broker recovery")
    workers(True)
    until(lambda: sql("SELECT count(*) FROM crawl_outbox;") == "0", "outbox drain")
    time.sleep(.5)
    assert counts().get("/empty", 0) == baseline

    workers(False)
    database_job = create("/empty", 0)
    compose("stop", "-t", "2", "postgres")
    status, problem = cancel(database_job)
    assert status == 500 and "Npgsql" not in json.dumps(problem), (status, problem)
    compose("start", "postgres")
    until(ready, "database recovery")
    assert summary(database_job)["status"] == "Pending", "Failed cancel was reported as successful"
    assert cancel(database_job)[0] == 200
    workers(True)
    assert_canceled(database_job)

    previous_fetches = counts().get("/cancel-slow", 0)
    crash_job = create("/cancel-slow", 0)
    until(lambda: summary(crash_job)["progress"]["processing"] == 1 and counts().get("/cancel-slow", 0) > previous_fetches,
          "fetch before worker kill")
    subprocess.run(["docker", "kill", extra_worker], capture_output=True, check=True)
    compose("kill", "-s", "SIGKILL", "worker")
    before = counts().get("/cancel-slow", 0)
    assert cancel(crash_job)[0] == 200
    workers(True)
    time.sleep(1)
    assert assert_canceled(crash_job)["progress"]["canceled"] == 1
    assert counts().get("/cancel-slow", 0) == before
    print("4. API SIGKILL between cancel writes rolled back atomically; broker offline/API restart, failed DB cancel, and worker SIGKILL: durable cancellation survived; no canceled task restarted HTTP work.", flush=True)


def safe_live():
    """Checks process liveness independently of dependency readiness, which is useful while
    RabbitMQ is intentionally offline.
    """
    try:
        return http("/health/live")[0] == 200
    except (OSError, URLError):
        return False


def cancel_wins_after_http_before_result_commit():
    """Uses an external SQL lock to queue cancellation before a fetched result, then verifies
    the late writer adds no result, links, or children and does not deadlock.
    """
    before = counts().get("/slow", 0)
    job = create("/slow", 1)
    until(lambda: counts().get("/slow", 0) > before, "slow fetch started")
    holder = subprocess.Popen(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler", "-d", "crawler", "-Atq"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    holder.stdin.write(f"BEGIN; SELECT id FROM jobs WHERE ext_id='{job}' FOR UPDATE; SELECT pg_sleep(7); ROLLBACK;\n")
    holder.stdin.close()
    until(lambda: int(sql("SELECT count(*) FROM pg_stat_activity WHERE query LIKE '%pg_sleep(7)%' AND wait_event='PgSleep';")) > 0,
          "external job lock")
    with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
        cancellation = pool.submit(cancel, job)
        # Queue cancellation first; the fetch finishes later and its result writer then waits behind it.
        until(lambda: int(sql("SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' AND query LIKE '%FROM jobs%FOR UPDATE%';")) >= 2,
              "cancel and fetched result both waiting at the job lock", 6)
        holder.wait(timeout=10)
        assert cancellation.result()[0] == 200
    until(lambda: sql(f"SELECT pg_try_advisory_xact_lock(p.id) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}';") == "t",
          "late writer finished")
    result = assert_canceled(job)
    assert result["progress"]["total"] == result["progress"]["canceled"] == 1, result
    assert sql(f"SELECT count(*) FROM links l JOIN pages p ON p.id=l.source_page_id JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}';") == "0"
    print("5. Forced commit race: cancellation queued before the fetched result; late result/links/children discarded without deadlock.", flush=True)


try:
    until(ready, "initial API readiness")
    http("/reset", {}, base=ADMIN)
    extra_worker = compose("run", "--no-deps", "-d", "--name", "alteva-audit-worker-extra", "worker").splitlines()[-1]
    queued_cancel_is_idempotent()
    in_flight_cancel_preserves_partial_results()
    cancellation_races_with_completion()
    cancellation_survives_outages_and_restarts()
    cancel_wins_after_http_before_result_commit()
    until(lambda: sql("SELECT count(*) FROM crawl_outbox;") == "0", "final outbox drain")
    assert sql("SELECT count(*) FROM jobs WHERE status IN ('Pending','Running');") == "0"
    assert sql("SELECT count(*) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.status='Canceled' AND p.status IN ('Pending','Processing');") == "0"
    assert sql("SELECT count(*) FROM (SELECT job_id,url FROM pages GROUP BY job_id,url HAVING count(*)>1) duplicate_pages;") == "0"
    print("All five cancellation scenarios passed; no unfinished jobs, active canceled pages, pending outbox rows, or duplicate URLs.", flush=True)
finally:
    for service in ("postgres", "rabbitmq", "api", "worker"):
        try:
            compose("start", service)
        except subprocess.SubprocessError:
            pass
    if extra_worker:
        subprocess.run(["docker", "rm", "-f", extra_worker], capture_output=True)
