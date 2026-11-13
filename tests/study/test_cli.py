import contextlib
import io
import unittest
from unittest.mock import patch

from puppypi_study.__main__ import main, source_identity


class OperatorCliTests(unittest.TestCase):
    def invoke(self, argv, replies):
        calls = []
        def request(endpoint, route, body=None):
            calls.append((route, body))
            return replies[len(calls) - 1]
        with patch("puppypi_study.__main__.api_request", side_effect=request), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(0, main(argv))
        return calls

    def test_session_seed_and_order_are_explicit(self):
        args = ["session", "--participant-id", "P001", "--interface-order", "smartphone_touch,mr_ballgrip",
                "--randomize-order", "--assignment-seed", "42"]
        first = self.invoke(args, [{}])[0][1]
        second = self.invoke(args, [{}])[0][1]
        self.assertEqual(first["interface_order"], second["interface_order"])
        self.assertEqual(42, first["metadata"]["assignment_seed"])
        self.assertEqual("seeded_shuffle", first["metadata"]["assignment_method"])

    def test_start_discovers_one_ready_client_and_mr_period(self):
        status = {"experiment_session_id": "session", "interface_order": ["smartphone_touch", "mr_ballgrip"],
                  "clients": [{"client_id": "actual-v2-id", "ready": True}]}
        calls = self.invoke(["start", "--difficulty", "demo-narrow", "--practice"], [status, {}])
        body = calls[1][1]
        self.assertEqual("/v1/trials/start", calls[1][0])
        self.assertEqual("actual-v2-id", body["control_client_id"])
        self.assertEqual(2, body["period_index"])
        self.assertTrue(body["practice"])

    def test_next_retry_keeps_previous_trial(self):
        status = {"experiment_session_id": "session", "interface_order": ["mr_ballgrip"],
                  "trial_id": "previous", "state": "aborted",
                  "clients": [{"client_id": "client", "ready": True}]}
        body = self.invoke(["next", "--retry"], [status, {}])[1][1]
        self.assertEqual("previous", body["retry_of_trial_id"])
        self.assertNotEqual("previous", body["trial_id"])

    def test_abort_uses_active_session_trial_and_client(self):
        status = {"experiment_session_id": "session", "trial_id": "trial", "control_client_id": "client"}
        body = self.invoke(["abort", "--reason", "operator_abort"], [status, {}])[1][1]
        self.assertEqual("trial", body["trial_id"])
        self.assertEqual("client", body["control_client_id"])

    def test_source_fingerprint_contains_runtime_and_journal(self):
        identity = source_identity()
        self.assertIn("puppypi_study/runtime.py", identity["source_sha256"])
        self.assertIn("puppypi_study/journal.py", identity["source_sha256"])
        self.assertEqual(64, len(identity["source_sha256"]["puppypi_study/runtime.py"]))


if __name__ == "__main__":
    unittest.main()
