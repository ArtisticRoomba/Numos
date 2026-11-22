"""Tests for numos_client.py against a small fake server. Run: python3 -m unittest discover -s examples/headless"""

import json
import os
import socket
import tempfile
import threading
import unittest

import numos_client


class FakeServer:
    """Answers each request line with ok=true, or ok=false for the op `fail`."""

    def __init__(self, path: str):
        self.requests: list[dict] = []
        self._listener = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self._listener.bind(path)
        self._listener.listen(1)
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    def _serve(self) -> None:
        try:
            while True:
                connection, _ = self._listener.accept()
                with connection, connection.makefile("rwb") as stream:
                    for line in stream:
                        request = json.loads(line)
                        self.requests.append(request)
                        if request["op"] == "fail":
                            reply = {"id": request["id"], "ok": False,
                                     "error": {"code": "operationRejected", "message": "nope"}}
                        elif request["op"] == "getSolverState":
                            members = [{"name": "Rate", "value": 2.5}, {"name": "Log", "value": "3 items"}]
                            reply = {"id": request["id"], "ok": True, "result": {"members": members}}
                        elif request["op"] == "setSolverState":
                            member = {"name": request["member"], "value": request["value"]}
                            reply = {"id": request["id"], "ok": True, "result": {"changed": True, "member": member}}
                        else:
                            reply = {"id": request["id"], "ok": True, "result": {"echo": request["op"]}}
                        stream.write(json.dumps(reply).encode() + b"\n")
                        stream.flush()
        except OSError:
            pass

    def close(self) -> None:
        self._listener.close()


class NumosClientTests(unittest.TestCase):
    def setUp(self) -> None:
        self._directory = tempfile.TemporaryDirectory()
        self.path = os.path.join(self._directory.name, "s.sock")
        self.server = FakeServer(self.path)

    def tearDown(self) -> None:
        self.server.close()
        self._directory.cleanup()

    def test_call_adds_protocol_version_and_increasing_ids(self) -> None:
        with numos_client.NumosClient(self.path) as client:
            client.call("hello")
            response = client.call("tick", count=3)
        self.assertEqual(response["result"]["echo"], "tick")
        self.assertEqual([r["id"] for r in self.server.requests], ["1", "2"])
        self.assertEqual(self.server.requests[1]["protocolVersion"], 2)
        self.assertEqual(self.server.requests[1]["count"], 3)

    def test_call_raises_numos_error_with_code(self) -> None:
        with numos_client.NumosClient(self.path) as client:
            with self.assertRaises(numos_client.NumosError) as caught:
                client.call("fail")
        self.assertEqual(caught.exception.code, "operationRejected")
        self.assertEqual(caught.exception.message, "nope")

    def test_solver_state_maps_member_names_to_values(self) -> None:
        with numos_client.NumosClient(self.path) as client:
            state = client.solver_state("my-mod/fire")
        self.assertEqual(state, {"Rate": 2.5, "Log": "3 items"})
        self.assertEqual(self.server.requests[0]["op"], "getSolverState")
        self.assertEqual(self.server.requests[0]["solver"], "my-mod/fire")

    def test_set_solver_state_sends_the_value_and_returns_the_member(self) -> None:
        with numos_client.NumosClient(self.path) as client:
            member = client.set_solver_state("my-mod/fire", "Note", None)
        request = self.server.requests[0]
        self.assertEqual(request["op"], "setSolverState")
        self.assertEqual((request["solver"], request["member"]), ("my-mod/fire", "Note"))
        self.assertIn("value", request)
        self.assertIsNone(request["value"])
        self.assertEqual(member, {"name": "Note", "value": None})

    def test_cli_runs_a_script_and_reports_failures(self) -> None:
        script = os.path.join(self._directory.name, "s.jsonl")
        with open(script, "w", encoding="utf-8") as handle:
            handle.write('{"protocolVersion":2,"id":"a","op":"hello"}\n\n{"protocolVersion":2,"id":"b","op":"fail"}\n')
        self.assertEqual(numos_client.main(["--socket", self.path, "--script", script]), 1)
        self.assertEqual([r["op"] for r in self.server.requests], ["hello", "fail"])


if __name__ == "__main__":
    unittest.main()
