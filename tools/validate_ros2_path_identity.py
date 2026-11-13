#!/usr/bin/env python3
"""Validate actual patched UDP ingress/callback identity without sockets or ROS."""
import argparse
import hashlib
import importlib
import json
from pathlib import Path
import sys
import types
import unittest

MODULES = None
OWNER = "a92e1842-7ef1-4d1f-bc14-2a04dcd0da87"


class ExecutionIdentityTests(unittest.TestCase):
    def setUp(self):
        follower, protocol, server = MODULES
        self.now = 1.0
        self.follower = follower.PathFollower()
        self.receipts, self.callbacks, self.events = [], [], []
        self.server = server.V2UdpServer(protocol.V2Session(25.0, 5.0, .28, .4, 1.0), lambda *_: None,
                                        clock=lambda: self.now, on_path_request=self.on_path)
        self.server._send = self.on_ack  # Replace only I/O; actual _handle is executed.
        self.packet("command", 1, control_active=False, forward=0, turn=0)

    def on_ack(self, address, ack):
        self.receipts.append(ack)
        self.events.append(("ack", ack["sequence"], self.follower.request_sequence))

    def on_path(self, points, owner, sequence):
        self.callbacks.append((points, owner, sequence))
        if points == ():
            self.follower.cancel("VR cancel")
        else:
            self.follower.set_path(points, owner, self.now, request_sequence=sequence)
        self.events.append(("callback", sequence, self.follower.request_sequence))

    def packet(self, kind, sequence, client=OWNER, **fields):
        payload = dict(protocol=2, type=kind, client_id=client, sequence=sequence, **fields)
        self.server._handle(json.dumps(payload).encode(), ("127.0.0.1", 54321))
        return self.receipts[-1]

    def test_identity_is_committed_only_after_ack_and_actual_path_callback(self):
        self.assertTrue(self.packet("path", 2, points=[[.12, 0]])["accepted"])
        self.assertEqual(self.events[-2:], [("ack", 2, None), ("callback", 2, 2)])
        self.assertEqual(self.follower.status(self.now)["request_client_id"], OWNER)

    def test_previous_identical_done_retains_old_identity_at_next_ingress_ack(self):
        self.packet("path", 2, points=[[.12, 0]])
        self.follower.index, self.follower.state = 1, "done"
        self.now += .3
        self.packet("path", 3, points=[[.12, 0]])
        self.assertEqual(self.events[-2:], [("ack", 3, 2), ("callback", 3, 3)])

    def test_rejected_owner_and_duplicate_requests_cannot_relabel_execution(self):
        self.packet("path", 2, points=[[.3, 0]])
        self.assertFalse(self.packet("path", 2, points=[[.3, 0]])["accepted"])
        self.assertFalse(self.packet("path", 3, client="b92e1842-7ef1-4d1f-bc14-2a04dcd0da87",
                                     points=[[.3, 0]])["accepted"])
        self.assertEqual(len(self.callbacks), 1)
        self.assertEqual(self.follower.request_sequence, 2)

    def test_heartbeats_do_not_relabel_current_execution(self):
        self.packet("path", 2, points=[[.3, 0]])
        self.packet("command", 3, control_active=False, forward=0, turn=0)
        self.assertEqual(self.follower.request_sequence, 2)
        self.assertEqual(len(self.callbacks), 1)

    def test_cancel_preserves_the_cancelled_execution_identity(self):
        self.packet("path", 2, points=[[.3, 0]])
        self.packet("path_cancel", 3)
        self.assertEqual(self.follower.state, "cancelled")
        self.assertEqual(self.follower.status(self.now)["request_sequence"], 2)

    def test_unidentified_direct_legacy_calls_report_both_identity_fields_null(self):
        self.follower.set_path([(.3, 0)], OWNER, self.now)
        status = self.follower.status(self.now)
        self.assertIsNone(status["request_client_id"])
        self.assertIsNone(status["request_sequence"])

    def test_old_two_argument_server_callback_remains_supported(self):
        old = []
        self.server._on_path_request = None
        self.server._on_path = lambda points, owner: old.append((points, owner))
        self.packet("path", 2, points=[[.3, 0]])
        self.assertEqual(old, [(((.3, 0.0),), OWNER)])


class MinimalPathBehaviorTests(unittest.TestCase):
    def follower(self, goal=(.6, 0)):
        follower = MODULES[0].PathFollower()
        follower.set_path([(0, 0), goal], OWNER, 1.0, request_sequence=2)
        return follower

    def test_periodic_two_point_ghosts_do_not_create_robot_block_or_clear_hold(self):
        follower = self.follower()
        pose = [0.0, 0.0, 0.0]
        for tick in range(300):
            now = 1.0 + tick * .02
            points = [(3., 0.)] * 40
            if tick % 40 < 5:
                points += [(.30, .005), (.30, -.005)]
            v, w = follower.update(now, tuple(pose), now, points, now)
            self.assertNotEqual(follower.state, "blocked")
            pose[0] += v / 100 * .02
        self.assertEqual(follower.state, "done")
        self.assertGreaterEqual(pose[0], .50)

    def test_raw_obstacle_points_do_not_stop_robot_but_mr_cancel_stops_it(self):
        follower = self.follower()
        points = [(.20, .005), (.20, -.005)]
        self.assertGreater(follower.update(1.02, (0, 0, 0), 1.02, points, 1.02)[0], 0)
        self.assertEqual(follower.state, "drive")
        follower.cancel("MR confirmed obstacle")
        self.assertEqual(follower.update(1.04, (0, 0, 0), 1.04, points, 1.04), (0, 0))

    def test_existing_scan_timeout_stops_initial_turn(self):
        follower = self.follower((0, 1))
        self.assertEqual(follower.update(2., (0, 0, 0), 2., [], 1.), (0, 0))
        self.assertEqual(follower.waiting, "scan")
        self.assertNotEqual(follower.state, "cancelled")

    def test_existing_pose_timeout_stops_translation(self):
        follower = self.follower()
        self.assertEqual(follower.update(2., (0, 0, 0), 1., [], 2.), (0, 0))
        self.assertEqual(follower.waiting, "pose")

    def test_pose_jump_still_cancels_current_path(self):
        follower = self.follower((2, 0))
        follower.update(1., (0, 0, 0), 1., [], 1.)
        self.assertEqual(follower.update(1.1, (.5, 0, 0), 1.1, [], 1.1), (0, 0))
        self.assertEqual(follower.state, "cancelled")

    def test_short_route_done_keeps_current_identity_without_an_active_sample(self):
        follower = self.follower((.12, 0))
        x = 0.
        for tick in range(15):
            now = 1. + tick * .02
            v, _ = follower.update(now, (x, 0, 0), now, [], now)
            x += v / 100 * .02
        status = follower.status(1.3)
        self.assertEqual(status["state"], "done")
        self.assertEqual(status["index"], status["count"])
        self.assertEqual((status["request_client_id"], status["request_sequence"]), (OWNER, 2))

    def test_source_does_not_add_new_ray_count_or_measured_clear_start_lock(self):
        follower = self.follower()
        self.assertGreater(follower.update(1.02, (0, 0, 0), 1.02, [], 1.02)[0], 0)
        self.assertEqual(follower.state, "drive")


def main():
    global MODULES
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot-src", type=Path, required=True)
    parser.add_argument("--report", type=Path, default=Path("TestResults/robot-path-identity.json"))
    args = parser.parse_args()
    source = args.robot_src.resolve()
    package = types.ModuleType("review_robot_identity")
    package.__path__ = [str(source)]
    sys.modules[package.__name__] = package
    names = ("path_follower", "protocol_v2", "v2_server")
    MODULES = tuple(importlib.import_module(package.__name__ + "." + name) for name in names)
    suite = unittest.TestSuite(unittest.defaultTestLoader.loadTestsFromTestCase(case)
                               for case in (ExecutionIdentityTests, MinimalPathBehaviorTests))
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    report = dict(tests=result.testsRun, failures=len(result.failures), errors=len(result.errors),
                  sockets=False, ros=False, motor_commands=False,
                  source_sha256={name + ".py": hashlib.sha256((source / (name + ".py")).read_bytes()).hexdigest() for name in names})
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
