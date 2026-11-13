import json
from pathlib import Path
import tempfile
import unittest

from puppypi_study.journal import AsyncJournal


class JournalTests(unittest.TestCase):
    def test_flush_close_and_serialized_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "record.jsonl"
            journal = AsyncJournal(path)
            record = {"schema_version": 1, "event_id": "one", "event_type": "sample",
                      "clock_id": "test", "payload": {"value": 1}}
            self.assertTrue(journal.emit(record))
            record["payload"]["value"] = 99
            self.assertTrue(journal.flush())
            self.assertTrue(journal.close())
            values = [json.loads(line) for line in path.read_text().splitlines()]
            self.assertEqual(1, values[0]["payload"]["value"])
            self.assertEqual("logging_closed", values[-1]["event_type"])
            self.assertTrue(values[-1]["payload"]["complete"])
            with self.assertRaises(FileExistsError):
                AsyncJournal(path)

    def test_invalid_and_oversize_records_are_explicit_loss(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "record.jsonl"
            journal = AsyncJournal(path, max_record_bytes=1024)
            self.assertFalse(journal.emit({"payload": float("nan")}))
            self.assertFalse(journal.emit({"payload": "a" * 2000}))
            self.assertFalse(journal.healthy)
            journal.close()
            records = [json.loads(line) for line in path.read_text().splitlines()]
            loss = [r for r in records if r["event_type"] == "logging_overflow"]
            self.assertEqual(2, loss[0]["payload"]["dropped_total"])
            self.assertFalse(records[-1]["payload"]["complete"])

    def test_queue_overflow_is_bounded_and_recorded(self):
        with tempfile.TemporaryDirectory() as folder:
            journal = AsyncJournal(Path(folder) / "record.jsonl", capacity=1)
            # Hold the accounting lock so worker cannot drain more than one item.
            # The worker never holds this lock during disk I/O.
            journal._queue.put(("record", "{}"))
            for _ in range(1000):
                journal.emit({"event_type": "sample", "payload": {}})
            self.assertGreater(journal.status()["dropped"], 0)
            self.assertLessEqual(journal.status()["queued"], 1)
            journal.close()


if __name__ == "__main__":
    unittest.main()
