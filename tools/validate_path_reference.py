#!/usr/bin/env python3
"""Exercise downloaded robot logic in memory; never opens a socket or sends motion."""
import argparse
import hashlib
import importlib.util
import io
import json
import math
from pathlib import Path
import random
import sys
import time
import unittest

sys.dont_write_bytecode = True
REVISION = "3d29ef16cebd12bfe3edeeed6c6b7d064a4e8a80"
FOLLOWER = PROTOCOL = None
METRICS = {}


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


class Rig:
    """Receiver callback/timer ordering plus a planar, drift-free virtual plant.

    The imported robot implementations own protocol validation and following.
    This adapter mirrors receiver_node callbacks without importing ROS or I/O.
    """
    owner = "a92e1842-7ef1-4d1f-bc14-2a04dcd0da87"
    address = ("127.0.0.1", 54321)

    def __init__(self, yaw=0.0, hold=0.0, linear_accel=0.0, yaw_accel=0.0):
        self.now, self.sequence = 1.0, 0
        self.session = PROTOCOL.V2Session(25.0, 5.0, .28, .4, 1.0)
        self.follower = FOLLOWER.PathFollower()
        self.hold = PROTOCOL.StartHold(hold)
        self.slew = PROTOCOL.SlewLimiter(linear_accel, yaw_accel)
        self.pose = [0.0, 0.0, yaw]
        self.target = self.output = (0.0, 0.0)
        self.scan = []
        self.trace, self.acks, self.events = [], [], []
        self.heartbeat_gaps, self.dropped = [], 0
        self._last_heartbeat = self.now
        self._last_path_key = None
        if not self.packet("command")["accepted"]:
            raise AssertionError("The real protocol rejected the initial neutral handshake")

    def _emit(self, velocity, reason):
        x, _, yaw = velocity
        if self.follower.active:
            if x == 0 and yaw == 0 and reason == "command":
                return  # ordinary neutral maintenance must retain the path
            self.follower.cancel("manual" if x or yaw else reason)
        self.target = (x, yaw)
        if x == 0 and yaw == 0:
            self.hold.reset()
            self.slew.reset()
            self.output = (0.0, 0.0)

    def packet(self, kind, deliver=True, client=None, address=None, **fields):
        self.sequence += 1
        payload = dict(protocol=2, type=kind, client_id=client or self.owner,
                       sequence=self.sequence)
        if kind == "command":
            payload.update(control_active=False, forward=0.0, turn=0.0)
        payload.update(fields)
        if not deliver:
            self.dropped += 1
            return None
        result = self.session.handle_datagram(json.dumps(payload).encode(),
                                              address or self.address, self.now)
        if result.velocity is not None:
            self._emit(result.velocity, result.event or "command")
        self.acks.append(result.ack)
        self.events.append("ack:" + kind)  # v2_server sends ACK before path callback
        if result.path is not None:
            self.events.append("callback:" + kind)
            if result.path == ():
                self.follower.cancel("VR cancel")
            else:
                self.follower.set_path(result.path, self.session.owner, self.now)
                self.target = (0.0, 0.0)
        if kind == "command" and result.ack["accepted"]:
            self.heartbeat_gaps.append(self.now - self._last_heartbeat)
            self._last_heartbeat = self.now
        return result.ack

    def start(self, points):
        ack = self.packet("path", points=[list(point) for point in points])
        if not ack["accepted"]:
            raise AssertionError(ack)

    def step(self, dt=.02, pose_fresh=True, scan_fresh=True):
        self.now += dt
        expired = self.session.check_timeouts(self.now)
        if expired:
            self._emit(PROTOCOL.STOP, "; ".join(expired))
        if self.follower.active:
            if self.session.state not in ("idle", "active"):
                self.follower.cancel("session " + self.session.state)
            elif self.session.owner != self.follower.owner:
                self.follower.cancel("owner changed")
            if self.follower.active:
                self.target = self.follower.update(
                    self.now, tuple(self.pose), self.now if pose_fresh else self.now - .61,
                    self.scan, self.now if scan_fresh else self.now - .61)
            else:
                self.target = (0.0, 0.0)
                self.hold.reset()
                self.slew.reset()
        key = (self.follower.state, self.follower.index, self.follower.waiting)
        if key != self._last_path_key:
            self._last_path_key = key
            if not self.follower.active and self.follower.state in ("done", "cancelled"):
                self.target = (0.0, 0.0)
        self.output = self.slew.update(*self.hold.update(*self.target, self.now), self.now)
        x, yaw = self.output
        self.pose[0] += math.cos(self.pose[2]) * x / 100.0 * dt
        self.pose[1] += math.sin(self.pose[2]) * x / 100.0 * dt
        self.pose[2] = FOLLOWER.wrap(self.pose[2] + yaw * dt)
        self.trace.append((self.now, self.follower.state, x, yaw))
        return self.output

    def advance(self, seconds, heartbeat=True, **freshness):
        stop, next_heartbeat = self.now + seconds, self.now
        while self.now < stop - 1e-9:
            if heartbeat and self.now >= next_heartbeat - 1e-9:
                self.packet("command")
                next_heartbeat += .05
            self.step(min(.02, stop - self.now), **freshness)

    def finish(self, seconds=120, seed=None, dropout=False):
        rng, next_heartbeat, deadline = random.Random(seed), self.now, self.now + seconds
        while self.follower.active and self.now < deadline:
            if self.now >= next_heartbeat - 1e-9:
                drop = dropout and rng.random() < .15 and self.now - self._last_heartbeat < .15
                self.packet("command", deliver=not drop)
                next_heartbeat += .05
            self.step(rng.uniform(.012, .028) if seed is not None else .02)
        if self.follower.state != "done":
            raise AssertionError(self.follower.status(self.now, self.pose))


class ReferenceTests(unittest.TestCase):
    def route(self, name, points, yaw=0, seed=None, dropout=False):
        rig = Rig(yaw)
        rig.start(points)
        rig.finish(seed=seed, dropout=dropout)
        error = math.dist(rig.pose[:2], points[-1])
        self.assertLessEqual(error, .101)
        self.assertTrue(all(x == 0 for _, state, x, _ in rig.trace if state == "rotate"))
        self.assertTrue(all(0 <= x <= 12.5 and abs(yaw_rate) <= .14
                            for _, _, x, yaw_rate in rig.trace))
        self.assertTrue(all(ack["accepted"] for ack in rig.acks))
        self.assertLess(max(rig.heartbeat_gaps), .4)
        METRICS[name] = dict(simulated_seconds=round(rig.now - 1, 3), final_error_m=error,
                             ticks=len(rig.trace), dropped_heartbeats=rig.dropped,
                             maximum_received_heartbeat_gap_s=max(rig.heartbeat_gaps))
        return rig

    def test_straight_l_and_z_routes(self):
        routes = {"straight": [(0, 0), (1, 0)],
                  "L": [(0, 0), (.5, 0), (.5, .5)],
                  "Z": [(0, 0), (.5, 0), (.5, .5), (1, .5)]}
        for name, points in routes.items():
            with self.subTest(route=name):
                self.route(name, points)

    def test_twenty_repeated_runs_keep_same_owner_and_sequence(self):
        rig = Rig()
        for _ in range(20):
            start = tuple(rig.pose[:2])
            rig.start([start, (start[0] + .3, start[1])])
            rig.finish(seconds=10)
            self.assertEqual(rig.session.owner, rig.owner)
        self.assertEqual(sum(event == "callback:path" for event in rig.events), 20)
        self.assertTrue(all(ack["accepted"] for ack in rig.acks))
        METRICS["repeated_runs"] = dict(runs=20, final_x_m=rig.pose[0], packets=rig.sequence)

    def test_rotated_starts_and_angle_wrap_with_jitter_and_drops(self):
        for seed, yaw in enumerate([math.pi - .02, -math.pi + .02, math.pi / 2, -.8]):
            with self.subTest(seed=seed):
                self.route("rotated_seed_" + str(seed), [(0, 0), (-.5, -.04)], yaw, seed, True)

    def test_persistent_nan_front_sector_does_not_make_robot_guard_a_wall(self):
        rig = Rig()
        ranges = [float("nan") if 435 <= i <= 447 else 3.0 for i in range(450)]
        rig.scan = FOLLOWER.scan_to_points(ranges, 0, 2 * math.pi / 450, .12, 11.5)
        self.assertEqual(len(rig.scan), 437)
        rig.start([(0, 0), (.6, 0)])
        rig.finish()
        self.assertNotIn("blocked", [row[1] for row in rig.trace])

    def test_one_point_noise_and_two_point_pause_resume_original(self):
        rig = Rig()
        rig.start([(0, 0), (1, 0)])
        rig.scan = [(.30, 0)]
        rig.advance(.2)
        self.assertEqual(rig.follower.state, "drive")
        rig.scan.append((.31, .01))
        rig.advance(.04)
        self.assertEqual(rig.output, (0, 0))
        self.assertEqual(rig.follower.state, "blocked")
        original = list(rig.follower.points)
        rig.scan = []
        rig.advance(.98)
        self.assertEqual(rig.output, (0, 0))
        rig.advance(.08)
        self.assertGreater(rig.output[0], 0)
        self.assertEqual(rig.follower.points, original)
        self.assertEqual(rig.follower.blocked_count, 1)
        rig.finish()

    def test_persistent_obstacle_stays_stopped(self):
        rig = Rig()
        rig.start([(0, 0), (1, 0)])
        rig.scan = [(.25, -.02), (.26, .02)]
        rig.advance(2)
        self.assertEqual(rig.pose[:2], [0, 0])
        self.assertEqual(rig.output, (0, 0))
        self.assertEqual(rig.follower.state, "blocked")

    def test_watchdog_and_lease_cancel_without_automatic_restart(self):
        rig = Rig()
        rig.start([(0, 0), (1, 0)])
        rig.advance(.1)
        rig.advance(.45, heartbeat=False)
        self.assertEqual(rig.follower.state, "cancelled")
        self.assertEqual(rig.output, (0, 0))
        rig.advance(.6, heartbeat=False)
        self.assertIsNone(rig.session.owner)
        self.assertTrue(rig.packet("command")["accepted"])
        self.assertEqual(rig.follower.state, "cancelled")

    def test_manual_takeover_and_emergency_stop(self):
        for action in ["manual", "emergency_stop", "path_cancel"]:
            with self.subTest(action=action):
                rig = Rig()
                rig.start([(0, 0), (1, 0)])
                rig.advance(.1)
                ack = rig.packet("command", control_active=True, forward=.5) if action == "manual" else rig.packet(action)
                self.assertTrue(ack["accepted"])
                rig.step()
                self.assertEqual(rig.follower.state, "cancelled")
                if action != "manual":
                    self.assertEqual(rig.output, (0, 0))
                if action == "emergency_stop":
                    self.assertFalse(rig.packet("path", points=[[0, 0], [1, 0]])["accepted"])
                    self.assertTrue(rig.packet("clear_emergency")["accepted"])
                    rejected = rig.packet("path", points=[[0, 0], [1, 0]])
                    self.assertEqual(rejected["reason"], "neutral_required")
                    self.assertTrue(rig.packet("command")["accepted"])
                    self.assertEqual(rig.follower.state, "cancelled", "neutral recovery is not a new path")

    def test_pose_jumps_cancel_and_stale_data_pause(self):
        for change in ["position", "yaw", "pose_stale", "scan_stale"]:
            with self.subTest(change=change):
                rig = Rig()
                rig.start([(0, 0), (1, 0)])
                rig.advance(.1)
                if change == "position": rig.pose[0] += .3
                if change == "yaw": rig.pose[2] += 1
                rig.step(pose_fresh=change != "pose_stale", scan_fresh=change != "scan_stale")
                self.assertEqual(rig.output, (0, 0))
                if change.endswith("stale"):
                    self.assertEqual(rig.follower.waiting, change.split("_")[0])
                    rig.finish()
                else:
                    self.assertEqual(rig.follower.state, "cancelled")

    def test_actual_protocol_rejects_other_owner_and_reordered_packet(self):
        unarmed = PROTOCOL.V2Session(25.0, 5.0, .28, .4, 1.0)
        path = dict(protocol=2, type="path", client_id=Rig.owner, sequence=1,
                    points=[[0, 0], [1, 0]])
        self.assertEqual(unarmed.handle_datagram(json.dumps(path).encode(), Rig.address, 1).ack["reason"],
                         "neutral_required")
        rig = Rig()
        ack = rig.packet("path", client="b71ee100-2819-4a9b-b060-c54cdd58a83f", points=[[0, 0], [1, 0]])
        self.assertEqual(ack["reason"], "not_owner")
        packet = dict(protocol=2, type="command", client_id=rig.owner, sequence=1,
                      control_active=False, forward=0, turn=0)
        ack = rig.session.handle_datagram(json.dumps(packet).encode(), rig.address, rig.now).ack
        self.assertEqual(ack["reason"], "out_of_order")

    def test_start_hold_and_slew_use_actual_limiters(self):
        rig = Rig(hold=.2, linear_accel=25, yaw_accel=.28)
        rig.start([(0, 0), (.5, 0)])
        rig.advance(.18)
        self.assertEqual(rig.output, (0, 0))
        rig.advance(.12)
        self.assertGreater(rig.output[0], 0)
        self.assertLess(rig.output[0], 12.5)
        rig.finish()

    def test_short_routes_can_complete_before_first_http_poll(self):
        results = []
        for distance in [.05, .10]:
            rig = Rig()
            rig.start([(0, 0), (distance, 0)])
            rig.step()
            self.assertEqual(rig.follower.state, "done")
            self.assertEqual(rig.output, (0, 0))
            self.assertEqual(rig.pose[:2], [0, 0])
            results.append(rig.follower.status(rig.now, rig.pose))
        METRICS["instant_done_oracle"] = results


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot-src", type=Path, default=Path(".local/robot-reference"))
    parser.add_argument("--report", type=Path, default=Path("TestResults/robot-path-reference.json"))
    args = parser.parse_args()
    sources = {name: args.robot_src / (name + ".py") for name in ["path_follower", "protocol_v2"]}
    for path in sources.values():
        if not path.is_file(): parser.error("Missing downloaded robot source: " + str(path))
    global FOLLOWER, PROTOCOL
    FOLLOWER = load("reference_path_follower", sources["path_follower"])
    PROTOCOL = load("reference_protocol_v2", sources["protocol_v2"])
    output = io.StringIO()
    started = time.monotonic()
    result = unittest.TextTestRunner(stream=output, verbosity=2).run(
        unittest.defaultTestLoader.loadTestsFromTestCase(ReferenceTests))
    probe = FOLLOWER.PathFollower()
    probe.set_path([(0, 0), (0, 1)], Rig.owner, 1)
    rotating = probe.update(1.02, (0, 0, 0), 1.02, [], 0)
    permission_probe = Rig()
    stop_requested = permission_probe.session.set_motion_permitted(False, "reference audit")
    path_ack = permission_probe.packet("path", points=[[0, 0], [1, 0]])
    permission_probe.step()
    external = []
    if rotating != (0, 0):
        external.append(dict(name="rotation_before_scan_timeout", stale_scan_rotation=rotating[1],
                             stale_scan_translation=rotating[0], maintainer_follow_up=True,
                             scope="Robot follower reference; MR lifecycle/map gates are not represented by this probe."))
    if path_ack["accepted"] and permission_probe.output != (0, 0):
        external.append(dict(name="motion_permission_requires_preemption_or_outer_arbiter",
                             session_motion_permitted=False, setter_requested_stop=stop_requested,
                             path_accepted=path_ack["accepted"], follower_state=permission_probe.follower.state,
                             reference_output_cm_s=permission_probe.output[0],
                             reference_output_yaw_rad_s=permission_probe.output[1],
                             mr_runtime_gates_included=False, outer_engine_arbiter_modeled=False,
                             maintainer_follow_up=True,
                             scope="No preempt-sequence update or outer engine arbitration supplied; not proof of physical motion."))
    report = dict(robot_revision=REVISION, sources={str(p): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources.values()},
                  receiver_defaults=dict(command_timeout_s=.4, lease_timeout_s=1.0,
                                         neutral_heartbeat_hz=20, tick_hz=50,
                                         start_hold_s=0, linear_accel=0, yaw_accel=0),
                  follower_defaults=vars(FOLLOWER.FollowerConfig()),
                  tests=result.testsRun, failures=len(result.failures), errors=len(result.errors),
                  passed=result.testsRun - len(result.failures) - len(result.errors),
                  wall_seconds=time.monotonic() - started, metrics=METRICS,
                  network_used=False, motor_commands_sent=False, physical_driving_tested=False,
                  external_findings=external,
                  limits=["Planar ideal kinematics do not establish physical tracking, stopping distance, floor support or corner clearance.",
                          "Receiver callbacks/timer ordering are adapted; the actual pure protocol, follower and limiters are imported unchanged.",
                          "The backend arrival radius is 0.10 m; exact MR polyline corner clearance is not physical corner clearance."],
                  unittest_output=output.getvalue())
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(output.getvalue(), end="")
    print("Report:", args.report)
    print("External reference findings:", len(external), "(separate from test pass/fail)")
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
