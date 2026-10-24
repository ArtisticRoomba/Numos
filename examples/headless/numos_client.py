#!/usr/bin/env python3
"""Client for the Numos.Headless Unix socket server.

Start the server with `dotnet run --project src/Numos.Headless -- --socket /tmp/numos.sock`.
Then call operations from Python or from the command line. See docs/headless_runner.md.

Library use:

    with NumosClient("/tmp/numos.sock") as client:
        client.call("createProject", chunkDimensions={"x": 2, "y": 1, "z": 1})
        print(client.call("tick", count=10)["state"]["tick"])

Command line:

    python3 numos_client.py --socket /tmp/numos.sock tick '{"count": 10}'
    python3 numos_client.py --socket /tmp/numos.sock --script study.jsonl
"""

from __future__ import annotations

import argparse
import json
import socket
import sys
import time
from typing import Any

PROTOCOL_VERSION = 2


class NumosError(Exception):
    """The server answered a request with ok=false."""

    def __init__(self, code: str, message: str, response: dict[str, Any]):
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.response = response


class NumosClient:
    """One connection to a Numos.Headless socket server.

    The server serves one client at a time. A client that connects while another is active waits until the
    other disconnects, so close the client when you finish. `connect_timeout` is how long to retry while the
    server starts and creates the socket file.
    """

    def __init__(self, socket_path: str, connect_timeout: float = 30.0):
        self._next_id = 0
        self._socket = self._connect(socket_path, connect_timeout)
        self._file = self._socket.makefile("rwb", buffering=0)
        self._reader = self._socket.makefile("rb")

    @staticmethod
    def _connect(path: str, timeout: float) -> socket.socket:
        deadline = time.monotonic() + timeout
        while True:
            sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            try:
                sock.connect(path)
                return sock
            except (FileNotFoundError, ConnectionRefusedError):
                sock.close()
                if time.monotonic() >= deadline:
                    raise
                time.sleep(0.1)

    def __enter__(self) -> "NumosClient":
        return self

    def __exit__(self, *_exc: object) -> None:
        self.close()

    def close(self) -> None:
        self._reader.close()
        self._file.close()
        self._socket.close()

    def send_line(self, line: str) -> dict[str, Any]:
        """Send one raw request line and return the parsed response, ok or not."""
        self._file.write(line.rstrip("\n").encode("utf-8") + b"\n")
        raw = self._reader.readline()
        if not raw:
            raise ConnectionError("The server closed the connection.")
        return json.loads(raw)

    def call(self, op: str, **fields: Any) -> dict[str, Any]:
        """Run one operation and return the full response. Raises NumosError when ok is false."""
        self._next_id += 1
        request = {"protocolVersion": PROTOCOL_VERSION, "id": str(self._next_id), "op": op, **fields}
        response = self.send_line(json.dumps(request, separators=(",", ":")))
        if not response.get("ok"):
            error = response.get("error") or {}
            raise NumosError(error.get("code", "unknown"), error.get("message", ""), response)
        return response


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Talk to a Numos.Headless socket server.")
    parser.add_argument("--socket", required=True, help="path of the server's Unix socket")
    parser.add_argument("--script", help="stream a JSONL file and print one response per line")
    parser.add_argument("--timeout", type=float, default=30.0, help="seconds to wait for the socket (default 30)")
    parser.add_argument("op", nargs="?", help="operation name")
    parser.add_argument("fields", nargs="?", default="{}", help="JSON object with the operation's fields")
    args = parser.parse_args(argv)

    if (args.script is None) == (args.op is None):
        parser.error("give either an OP or --script")

    failed = False
    with NumosClient(args.socket, args.timeout) as client:
        if args.script is not None:
            with open(args.script, encoding="utf-8") as script:
                for line in script:
                    if not line.strip():
                        continue
                    response = client.send_line(line)
                    failed |= not response.get("ok")
                    print(json.dumps(response, separators=(",", ":")))
        else:
            try:
                fields = json.loads(args.fields)
                if not isinstance(fields, dict):
                    parser.error("fields must be a JSON object")
                print(json.dumps(client.call(args.op, **fields), separators=(",", ":")))
            except NumosError as error:
                print(json.dumps(error.response, separators=(",", ":")))
                failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
