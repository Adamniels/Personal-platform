"""The stand in bucket backup.test.sh uploads to: an HTTP server, Python's standard library only.

It stores each PUT's body under its path, in OBJECTS, and refuses, with 403, a request whose
Authorization is not AWS4-HMAC-SHA256 or whose Content-MD5 does not match its body, the two things
the backup job must send. It never verifies the signature: that is curl's and the provider's, see
"Backups" in infra/hosted/README.md. While the file REFUSE exists it refuses everything, so a test
can make an upload fail. Every request is a line of REQUESTS, as JSON.

check.sh runs it as the service s3stub of its project, from tests/compose.yaml, on port 9000.
"""

import base64
import hashlib
import json
import os
import posixpath
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = "/tmp/s3"
OBJECTS = os.path.join(ROOT, "objects")
REQUESTS = os.path.join(ROOT, "requests.jsonl")
REFUSE = os.path.join(ROOT, "refuse")


class Handler(BaseHTTPRequestHandler):
    # HTTP/1.1, so curl's "Expect: 100-continue" on a large upload is answered.
    protocol_version = "HTTP/1.1"

    # The name http.server calls for a PUT.
    def do_PUT(self) -> None:
        path = posixpath.normpath(
            urllib.parse.unquote(urllib.parse.urlsplit(self.path).path)
        )
        authorization = self.headers.get("Authorization", "")
        sent_md5 = self.headers.get("Content-MD5", "")
        length = self.headers.get("Content-Length")
        entry: dict[str, object] = {
            "method": "PUT",
            "path": path,
            "auth_scheme": authorization.split(" ", 1)[0],
            "content_md5": sent_md5,
        }
        if length is None or not length.isdigit() or ".." in path.split("/"):
            self.answer(411 if length is None else 400, entry)
            return
        body = self.rfile.read(int(length))
        entry["md5_ok"] = (
            base64.b64encode(hashlib.md5(body).digest()).decode() == sent_md5
        )
        signed = entry["auth_scheme"] == "AWS4-HMAC-SHA256"
        if os.path.exists(REFUSE) or not signed or not entry["md5_ok"]:
            self.answer(403, entry)
        else:
            target = os.path.join(OBJECTS, path.lstrip("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as file:
                file.write(body)
            self.answer(200, entry)

    def answer(self, status: int, entry: dict[str, object]) -> None:
        entry["status"] = status
        with open(REQUESTS, "a") as log:
            log.write(json.dumps(entry) + "\n")
        self.send_response(status)
        self.send_header("Content-Length", "0")
        if status != 200:
            self.send_header("Connection", "close")
        self.end_headers()


if __name__ == "__main__":
    os.makedirs(OBJECTS, exist_ok=True)
    ThreadingHTTPServer(("0.0.0.0", 9000), Handler).serve_forever()
