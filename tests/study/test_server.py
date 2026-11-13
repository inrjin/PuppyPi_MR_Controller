import http.client
import json
import threading
import unittest

from puppypi_study.server import StudyHTTPServer
from test_runtime import RuntimeFixture


class HttpTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RuntimeFixture()
        self.server = StudyHTTPServer(("127.0.0.1", 0), self.fixture.engine)
        self.worker = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.worker.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.worker.join(timeout=1)

    def request(self, path, body=None, raw=None, headers=None):
        conn = http.client.HTTPConnection(*self.server.server_address, timeout=2)
        try:
            if body is None and raw is None:
                conn.request("GET", path)
            else:
                conn.request("POST", path, body=raw if raw is not None else json.dumps(body),
                             headers=headers or {"Content-Type": "application/json"})
            response = conn.getresponse()
            return response.status, json.loads(response.read())
        finally:
            conn.close()

    def test_heartbeat_status_clock_and_abort_roundtrip(self):
        status, payload = self.request("/v1/status?client_id=" + self.fixture.client)
        self.assertEqual(200, status)
        self.assertTrue(payload["mock_mode"])
        prepared = self.fixture.prepare()
        self.fixture.now += 100000
        self.fixture.events(self.fixture.event("clock_sync", {"offset_us": 100, "uncertainty_us": 50,
                            "valid": True, "reference_clock_id": "test-service"}))
        status, running = self.request("/v1/events", dict(self.fixture.base,
            request_id="shown-http", events=[self.fixture.event("signal_shown", {"signal_id": prepared["signal_id"]})]))
        self.assertEqual(200, status)
        self.assertTrue(running["motion_permitted"])
        status, clock = self.request("/v1/clock", {"client_id": self.fixture.client,
            "source_clock_id": "client", "source_t_mono_us": 123})
        self.assertEqual(200, status)
        self.assertEqual("test-service", clock["clock_id"])
        status, aborted = self.request("/v1/trials/abort", dict(self.fixture.base,
            request_id="abort-http", reason="operator_abort"))
        self.assertEqual("aborted", aborted["state"])
        self.assertFalse(aborted["motion_permitted"])

    def test_reject_nonfinite_duplicate_keys_unknown_version_and_large_request(self):
        for raw in ('{"value":NaN}', '{"schema_version":1,"schema_version":2}',
                    '{"schema_version":2}', '[]'):
            status, _ = self.request("/v1/session", raw=raw)
            self.assertEqual(400, status)
        status, _ = self.request("/v1/session", raw='"' + "a" * 66000 + '"')
        self.assertEqual(413, status)

    def test_robot_ingress_is_not_public_http(self):
        status, _ = self.request("/v1/robot_status", body={})
        self.assertEqual(404, status)


if __name__ == "__main__":
    unittest.main()
