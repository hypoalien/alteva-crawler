"""Deterministic crawl fixtures. Used only by compose.fixture.yaml."""

import json
import gzip
import time
import select
import socket
from collections import Counter
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from threading import Lock
from urllib.parse import urlsplit

counts = Counter()
lock = Lock()
pages = {
    "/": '<a href="/about#team">About</a><a href="/about">Duplicate</a>'
         '<a href="/products">Products</a><a href="https://external.test/x">External</a>'
         '<a href="mailto:test@example.com">Mail</a>',
    "/about": '<a href="/">Home</a><a href="#team">Team</a>',
    "/products": '<a href="/products/1">One</a><a href="/products/2">Two</a>',
    "/products/1": "<p>No outgoing links</p>",
    "/products/2": '<a href="/about">About</a>',
    "/partial": '<a href="/missing">Missing</a><a href="/file">File</a><a href="/retry">Retry</a>',
    "/retry": "<p>Recovered</p>",
    "/slow": '<a href="/about">About</a>',
    "/redirected/": '<a href="relative">Relative</a>',
    "/redirected/relative": '<base href="/products/"><a href="1#top">One</a>',
    "/many": "".join(f'<a href="/item/{i}">Item</a>' for i in range(250)),
    "/wide": '<a href="/branch/a">A</a><a href="/branch/b">B</a>',
    "/branch/a": "".join(f'<a href="/item/{i}">Item</a>' for i in range(125)),
    "/branch/b": "".join(f'<a href="/item/{i}">Item</a>' for i in range(100, 225)),
    "/dense": "".join('<a href="/about#different">Same page</a>' for _ in range(10000)),
    "/trap": '<a href="/trap">Self</a><a href="/trap?x=1">Query</a><a href="javascript:alert(1)">JS</a>',
    "/cancel-frontier": '<a href="/empty">Fast</a>' + "".join(
        f'<a href="/cancel-slow?child={i}">Slow {i}</a>' for i in range(8)
    ),
    "/empty": "<p>No links</p>",
    "/cookies": '<a href="/check-cookie">Next</a>',
}


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        """Implements the test-only /reset endpoint, clearing request counters under a lock so
        each sequential exercise gets a clean baseline.
        """
        if self.path != "/reset":
            return self.respond(404, "application/json", b"{}")
        with lock:
            counts.clear()
        self.respond(200, "application/json", b"{}")

    def do_GET(self):
        """Serves deterministic HTML and failure routes, counting requests and observing
        canceled TCP streams. The threaded server lets tests produce real concurrent crawler
        requests.
        """
        path = urlsplit(self.path).path
        if path == "/counts":
            with lock:
                body = json.dumps(dict(counts)).encode()
            return self.respond(200, "application/json", body)
        with lock:
            counts[path] += 1
            attempt = counts[path]
        if path == "/cancel-retry":
            return self.respond(503, "text/html", b"Retry later", {"Retry-After": "30"})
        if path == "/cancel-slow":
            body = b'<html><a href="/must-not-follow">Never schedule</a></html>'
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body[:6])
            self.wfile.flush()
            deadline = time.monotonic() + 20
            while time.monotonic() < deadline:
                # Observe the actual TCP disconnect, rather than merely trusting a Canceled API response.
                readable, _, _ = select.select([self.connection], [], [], .1)
                if readable:
                    try:
                        if not self.connection.recv(1, socket.MSG_PEEK):
                            with lock:
                                counts["/cancel-slow:disconnected"] += 1
                            return
                    except ConnectionResetError:
                        with lock:
                            counts["/cancel-slow:disconnected"] += 1
                        return
            try:
                self.wfile.write(body[6:])
            except (BrokenPipeError, ConnectionResetError):
                pass
            return
        if path == "/timeout" and attempt <= 2:
            time.sleep(11)
        if path == "/timeout":
            return self.respond(200, "text/html", b"Recovered after timeout")
        if path == "/loop":
            return self.respond(302, "text/html", b"", {"Location": "/loop"})
        if path == "/bad-redirect":
            return self.respond(302, "text/html", b"", {"Location": "file:///etc/passwd"})
        if path == "/rate-limit" and attempt == 1:
            return self.respond(429, "text/html", b"Slow down", {"Retry-After": "1"})
        if path == "/rate-limit":
            return self.respond(200, "text/html", b"Recovered")
        if path == "/gzip-large":
            return self.respond(200, "text/html", gzip.compress(b"x" * (2 * 1024 * 1024 + 1)), {"Content-Encoding": "gzip"})
        if path in ("/stream-large", "/truncated"):
            self.send_response(200)
            self.send_header("Content-Type", "text/html")
            self.send_header("Connection", "close")
            if path == "/truncated":
                self.send_header("Content-Length", "100")
            self.end_headers()
            self.close_connection = True
            try:
                self.wfile.write(b"short" if path == "/truncated" else b"x" * (2 * 1024 * 1024 + 1))
            except (BrokenPipeError, ConnectionResetError):
                pass
            return
        if path == "/latin1":
            return self.respond(200, "text/html; charset=windows-1252", b'<a href="/caf\xe9">Cafe</a>')
        if path == "/xhtml":
            return self.respond(200, "application/xhtml+xml", b'<html><a href="/about">About</a></html>')
        if path == "/no-type":
            return self.respond(200, None, b"No content type")
        if path == "/cookies":
            return self.respond(200, "text/html", pages[path].encode(), {"Set-Cookie": "session=must-not-be-replayed; Path=/"})
        if path == "/check-cookie":
            return self.respond(200, "text/html", b'<a href="/cookie-leak">Leaked</a>' if self.headers.get("Cookie") else b"No cookie")
        if path == "/slow":
            time.sleep(4)
        if path == "/redirect":
            return self.respond(302, "text/html", b"", {"Location": "/redirected/"})
        if path == "/unsafe-redirect":
            return self.respond(302, "text/html", b"", {"Location": "http://127.0.0.1:8090/"})
        if path == "/retry" and attempt <= 2:
            return self.respond(503, "text/html", b"Try again", {"Retry-After": "0"})
        if path == "/poison":
            return self.respond(503, "text/html", b"Unavailable", {"Retry-After": "0"})
        if path == "/file":
            return self.respond(200, "application/pdf", b"fixture")
        if path == "/large":
            return self.respond(200, "text/html", b"x" * (2 * 1024 * 1024 + 1))
        if path.startswith("/item/"):
            return self.respond(200, "text/html", b"<p>Item</p>")
        html = pages.get(path)
        self.respond(200 if html is not None else 404, "text/html", (html or "Not found").encode())

    def respond(self, status, content_type, body, headers=None):
        """Writes status, headers, and body for a fixture response; expected client disconnects
        during cancellation/timeouts do not crash the fixture server.
        """
        self.send_response(status)
        if content_type is not None:
            self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        for name, value in (headers or {}).items():
            self.send_header(name, value)
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass


ThreadingHTTPServer(("0.0.0.0", 8090), Handler).serve_forever()
