"""Real crash-boundary probes. Refuses to run outside the isolated alteva-audit stack."""
import concurrent.futures
import json
import os
import socket
import subprocess
import time
from urllib.error import URLError
from urllib.request import Request, urlopen

if os.environ.get("COMPOSE_PROJECT_NAME") != "alteva-audit":
    raise SystemExit("Crash probes require COMPOSE_PROJECT_NAME=alteva-audit.")


def compose(*args):
    """Runs Docker Compose in the audit project selected by environment variables; raises on
    CLI failure. Used to stop/restart only the isolated test services.
    """
    return subprocess.run(
        ["docker", "compose", *args], capture_output=True, text=True,
        check=True, timeout=45,
    ).stdout.strip()


def sql(query):
    """Runs SQL directly in audit PostgreSQL and returns plain text, providing storage/lock
    checks independent of API responses.
    """
    return subprocess.run(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler",
         "-d", "crawler", "-Atq", "-v", "ON_ERROR_STOP=1"],
        input=query, capture_output=True, text=True, check=True, timeout=30,
    ).stdout.strip()


def http(path, body=None):
    """Calls the audit API, returning status and JSON for job routes or text for health routes.
    Failures raise so process-kill tests can observe the disconnect.
    """
    request = Request(
        "http://localhost:5180" + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urlopen(request, timeout=12) as response:
        data = json.load(response) if path.startswith("/api/") else response.read().decode()
        return response.status, data


def wait_until(predicate, description, seconds=30):
    """Polls an observable condition with a deadline; raises with a description of the boundary
    that was never reached.
    """
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.1)
    raise AssertionError("Timed out: " + description)


def api_ready():
    """Performs one readiness probe and returns false during expected transport/service
    outages.
    """
    try:
        return http("/health/ready")[0] == 200
    except (OSError, URLError):
        return False


def lock_pages(seconds):
    """Starts a temporary SQL transaction holding the pages table lock, waits until the lock
    exists, and returns the process that will release it after the requested delay.
    """
    process = subprocess.Popen(
        ["docker", "compose", "exec", "-T", "postgres", "psql", "-U", "crawler",
         "-d", "crawler", "-Atq", "-v", "ON_ERROR_STOP=1"],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    process.stdin.write(
        f"BEGIN; LOCK TABLE pages IN ACCESS EXCLUSIVE MODE; SELECT pg_sleep({seconds}); ROLLBACK;\n"
    )
    process.stdin.close()
    wait_until(lambda: int(sql(
        "SELECT count(*) FROM pg_locks WHERE relation='pages'::regclass "
        "AND mode='AccessExclusiveLock' AND granted;"
    )) > 0, "exclusive pages lock")
    return process


def blocked_root_inserts():
    """Counts API transactions actually waiting on the blocked root-page insert, so the kill
    occurs between job/root writes rather than at a guessed time.
    """
    return int(sql(
        "SELECT count(*) FROM pg_stat_activity WHERE wait_event_type='Lock' "
        "AND query LIKE 'INSERT INTO pages%';"
    ))


def create(path, depth=0):
    """Creates a real fixture crawl through the API and returns its public job UUID; this is
    not an in-memory/mock job.
    """
    return http("/api/jobs", {"url": "http://fixtures:8090" + path, "maxDepth": depth})[1]["jobId"]


def summary(job):
    """Fetches the current job summary from the API for state assertions."""
    return http("/api/jobs/" + job)[1]


def interrupted_create(_):
    """Attempts creation during the kill window and returns true only if the client connection
    is interrupted instead of receiving a successful response.
    """
    try:
        create("/kill-before-commit")
    except (OSError, URLError):
        return True
    return False


try:
    # A real table lock stops the API between job insert and root insert.
    table_lock = lock_pages(7)
    with concurrent.futures.ThreadPoolExecutor(max_workers=5) as pool:
        requests = [pool.submit(interrupted_create, index) for index in range(5)]
        wait_until(lambda: blocked_root_inserts() >= 5, "five blocked API transactions", 5)
        compose("kill", "-s", "SIGKILL", "api")
        assert all(request.result() for request in requests)
    table_lock.wait(timeout=15)
    compose("start", "api")
    wait_until(api_ready, "API recovery")
    assert sql("SELECT count(*) FROM jobs WHERE url LIKE '%kill-before-commit%';") == "0"
    print("Killed API between job/root inserts: all five transactions rolled back; no orphan jobs.", flush=True)

    table_lock = lock_pages(5)
    body = json.dumps({"url": "http://fixtures:8090/client-abort"}).encode()
    with socket.create_connection(("localhost", 5180)) as client:
        client.sendall(
            b"POST /api/jobs HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: "
            + str(len(body)).encode() + b"\r\n\r\n" + body
        )
        wait_until(lambda: blocked_root_inserts() > 0, "client request blocked on root insert", 4)
    table_lock.wait(timeout=15)
    time.sleep(.3)
    assert sql("SELECT count(*) FROM jobs WHERE url LIKE '%client-abort%';") == "0"
    print("Client disconnected during root insert: job and publish intent rolled back together.", flush=True)

    job = create("/slow", 1)
    wait_until(lambda: summary(job)["progress"]["processing"] == 1, "worker fetch")
    previous_limit = compose("exec", "-T", "rabbitmq", "rabbitmqctl", "eval",
                             "rabbit_disk_monitor:get_disk_free_limit().").rstrip(".")
    try:
        # The disk alarm blocks publishing while PostgreSQL can still commit child tasks.
        compose("exec", "-T", "rabbitmq", "rabbitmqctl", "set_disk_free_limit", "100000000000000")

        def committed_child_waiting_for_publish():
            """Checks in one SQL snapshot that a completed root, pending child, and durable
            child outbox row exist before the worker is killed.
            """
            state = json.loads(sql(f"""
                SELECT json_build_object(
                    'rootCompleted', count(*) FILTER(WHERE p.parent_page_id IS NULL AND p.status='Completed'),
                    'pendingChildren', count(*) FILTER(WHERE p.parent_page_id IS NOT NULL AND p.status='Pending'),
                    'outbox', (SELECT count(*) FROM crawl_outbox WHERE job_id='{job}')
                ) FROM pages p JOIN jobs j ON j.id=p.job_id WHERE j.ext_id='{job}';
            """))
            return state["rootCompleted"] == 1 and state["pendingChildren"] > 0 and state["outbox"] > 0

        wait_until(committed_child_waiting_for_publish, "committed child and outbox row", 15)
        compose("kill", "-s", "SIGKILL", "worker")
        print("Killed worker after root/child/outbox commit while broker publication was blocked.", flush=True)
    finally:
        compose("exec", "-T", "rabbitmq", "rabbitmqctl", "set_disk_free_limit", previous_limit)
        compose("start", "worker")

    wait_until(lambda: summary(job)["status"] not in ("Pending", "Running"), "child delivery", 45)
    result = summary(job)
    assert result["status"] == "Completed" and result["progress"]["total"] == 2, result
    wait_until(lambda: sql(f"SELECT count(*) FROM crawl_outbox WHERE job_id='{job}';") == "0", "outbox drain")
    print("After restart: child delivered, crawl completed, outbox drained, no duplicate pages.", flush=True)
    print("Crash-window probes passed.", flush=True)
finally:
    for service in ("api", "worker"):
        try:
            compose("start", service)
        except subprocess.SubprocessError:
            pass
