#!/usr/bin/env python3
"""Local test peer using the robot's actual UDP server and path follower.

Only the downloaded pure Python modules are imported. This process never imports
ROS or a motor adapter, and its only network listener is an ephemeral localhost
UDP socket. JSON-lines on stdin/stdout configure an ideal planar plant and expose
coherent telemetry for the Unity test peer's HTTP/WebSocket listeners.
"""
import argparse
import hashlib
import importlib
import inspect
import json
import logging
import math
from pathlib import Path
import sys
import threading
import time
import types

sys.dont_write_bytecode = True
logging.basicConfig(level=logging.ERROR, stream=sys.stderr)


class ReferencePeer:
    DEFAULTS = dict(pauseSensors=False, allBeamsMissing=False,
                    ignoreFollowerStart=False, dropNextPathAck=False,
                    holdCancelAck=False, pathAckDelayMilliseconds=0,
                    motionPermitted=True, obstacle=False, obstacleX=0.0,
                    obstacleY=0.0, obstacleRadius=.07, periodicGhosts=False,
                    ghostPeriodSeconds=.8, ghostDurationSeconds=.1,
                    ghostRangeMeters=.3, callbackDelayMilliseconds=0,
                    startHoldSeconds=.2, resetPose=False,
                    resetPoseX=0.0, resetPoseY=0.0, resetPoseYaw=0.0,
                    secondObstacle=False, secondObstacleX=0.0,
                    secondObstacleY=0.0, secondObstacleRadius=.07,
                    heartbeatDropEvery=0, heartbeatBlackoutSeconds=0.0,
                    pathCancelAckDelayMilliseconds=0, dropPathCancelCount=0)

    def __init__(self, source):
        self.source = source.resolve()
        for name in ("protocol_v2.py", "v2_server.py", "path_follower.py"):
            if not (self.source / name).is_file():
                raise ValueError("Missing robot reference module: " + name)
        package = types.ModuleType("puppy_vr_bridge")
        package.__path__ = [str(self.source)]
        sys.modules[package.__name__] = package
        self.protocol = importlib.import_module("puppy_vr_bridge.protocol_v2")
        self.transport = importlib.import_module("puppy_vr_bridge.v2_server")
        self.following = importlib.import_module("puppy_vr_bridge.path_follower")
        self.lock = threading.RLock()
        self.stopping = threading.Event()
        self.local = threading.local()
        self.config = self.DEFAULTS.copy()
        self.follower = self.following.PathFollower()
        self.hold = self.protocol.StartHold(self.config["startHoldSeconds"])
        self.slew = self.protocol.SlewLimiter(0, 0)
        self.pose = [0.0, 0.0, 0.0]
        self.sensor_pose = tuple(self.pose)
        self.sensor_at = 0.0
        self.scan_at = 0.0
        self.scan_number = 0
        self.ranges = [None] * 450
        self.scan_points = None
        self.target = self.output = (0.0, 0.0)
        self.paths = self.cancels = self.heartbeats = self.non_neutral = 0
        self.last_path_sequence = 0
        self.collision_samples = self.ghost_scans = 0
        self.dropped_heartbeats = self.dropped_cancels = self.emergency_commands = 0
        self.path_datagrams = self.cancel_datagrams = self.emergency_datagrams = 0
        self._heartbeat_attempts = self._remaining_cancel_drops = 0
        self._heartbeat_blackout_until = 0.0
        self.error = ""
        self.events = []
        self.receipts = []
        self.timers = []
        self.cancel_ack_at = math.inf
        self.started = 0.0
        self.last_type = ""
        self.ports = {}
        self.sequences = {}
        self.last_tick = self.next_sensor = time.monotonic()
        self.session = self.protocol.V2Session(25.0, 5.0, .28, .4, 1.0)
        if "on_path_request" not in inspect.signature(self.transport.V2UdpServer).parameters:
            raise ValueError("The staged server lacks the execution-identity patch")
        self.server = self.transport.V2UdpServer(
            self.session, self.on_output, bind_address="127.0.0.1", port=0,
            on_path_request=self.on_path)
        original_handle, original_send = self.server._handle, self.server._send

        def handle(data, address):
            packet = json.loads(data)
            kind = packet.get("type", "")
            with self.lock:
                # Count actual ingress independently of parser acceptance and
                # simulated loss, so an unsent-operation regression proves that
                # no path/cancel/emergency datagram crossed the test socket.
                if kind == "path":
                    self.path_datagrams += 1
                elif kind == "path_cancel":
                    self.cancel_datagrams += 1
                elif kind == "emergency_stop":
                    self.emergency_datagrams += 1
                # Simulated ingress loss happens before the real parser/server.
                # Explicit active/manual commands are not selected by this
                # neutral-heartbeat test injector.
                if kind == "command" and packet.get("control_active") is False:
                    self._heartbeat_attempts += 1
                    every = self.config["heartbeatDropEvery"]
                    if (time.monotonic() < self._heartbeat_blackout_until or
                            every > 0 and self._heartbeat_attempts % every == 0):
                        self.dropped_heartbeats += 1
                        return
                elif kind == "path_cancel" and self._remaining_cancel_drops > 0:
                    self._remaining_cancel_drops -= 1
                    self.dropped_cancels += 1
                    self.events.append("drop:path_cancel:" + str(packet.get("sequence", -1)))
                    return
            self.local.packet = packet
            try:
                original_handle(data, address)
            finally:
                self.local.packet = None

        def send(address, ack):
            packet = getattr(self.local, "packet", None) or {}
            kind = packet.get("type", "")
            cid, sequence = packet.get("client_id", ""), packet.get("sequence", -1)
            delay, hold, drop = 0.0, False, False
            with self.lock:
                if ack.get("accepted"):
                    if cid in self.ports and self.ports[cid] != address[1]:
                        self.error = "Path and heartbeat used different UDP source ports"
                    if cid in self.sequences and sequence <= self.sequences[cid]:
                        self.error = "Accepted sequence was not monotonic"
                    self.ports[cid], self.sequences[cid] = address[1], sequence
                    if kind == "path":
                        self.paths += 1
                        self.last_path_sequence = sequence
                        self.events.append("path:" + str(self.paths))
                        self.events.append("path_ingress:" + str(sequence))
                        drop = self.config["dropNextPathAck"]
                        self.config["dropNextPathAck"] = False
                        delay = self.config["pathAckDelayMilliseconds"] / 1000
                    elif kind == "path_cancel":
                        self.cancels += 1
                        self.events.append("cancel:" + str(self.cancels))
                        self.cancel_ack_at = math.inf
                        hold = self.config["holdCancelAck"]
                        delay = self.config["pathCancelAckDelayMilliseconds"] / 1000
                    elif kind == "command":
                        self.heartbeats += 1
                        if self.follower.active and packet.get("control_active"):
                            self.non_neutral += 1
                    elif kind == "emergency_stop":
                        self.emergency_commands += 1
                        self.events.append("emergency_stop")

            def deliver():
                if self.stopping.is_set():
                    return
                with self.lock:
                    if kind == "path":
                        self.events.append("ack:path:" + str(sequence))
                    if kind == "path_cancel":
                        self.cancel_ack_at = time.monotonic()
                        self.events.append("cancel_ack")
                original_send(address, ack)

            if hold:
                with self.lock:
                    self.receipts.append(deliver)
            elif not drop:
                if delay:
                    timer = threading.Timer(delay, deliver)
                    timer.daemon = True
                    self.timers.append(timer)
                    timer.start()
                else:
                    deliver()

        self.server._handle, self.server._send = handle, send
        self.server.start()
        self.thread = threading.Thread(target=self.run, name="reference-plant", daemon=True)
        self.thread.start()

    def on_output(self, velocity, reason):
        x, _, yaw = velocity
        with self.lock:
            if self.follower.active:
                if x == 0 and yaw == 0 and reason == "command":
                    return
                self.follower.cancel("manual" if x or yaw else reason)
            self.target = (x, yaw)
            if x == 0 and yaw == 0:
                self.hold.reset()
                self.slew.reset()
                self.output = (0.0, 0.0)

    def on_path(self, points, owner, request_sequence):
        with self.lock:
            delay = self.config["callbackDelayMilliseconds"] / 1000
        if delay:
            self.stopping.wait(delay)
        if self.stopping.is_set():
            return
        with self.lock:
            if points == ():
                self.follower.cancel("VR cancel")
            elif not self.config["ignoreFollowerStart"]:
                self.started = time.monotonic()
                self.follower.set_path(points, owner, self.started,
                                       request_sequence=request_sequence)
                self.events.append("callback:path:" + str(request_sequence))
                self.target = (0.0, 0.0)

    def configure(self, values):
        if not isinstance(values, dict) or set(values) - set(self.DEFAULTS):
            raise ValueError("Unknown reference peer configuration")
        with self.lock:
            merged = dict(self.config, **values)
            # C# sends its complete configuration every sensor frame. A one-shot
            # request remains armed until the next path consumes it, regardless
            # of subsequent default-false snapshots from the test controller.
            if self.config["dropNextPathAck"]:
                merged["dropNextPathAck"] = True
            for name, default in self.DEFAULTS.items():
                value = merged[name]
                if isinstance(default, bool):
                    if not isinstance(value, bool):
                        raise ValueError("Expected boolean: " + name)
                elif not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
                    raise ValueError("Expected finite number: " + name)
            if (not 0 < merged["ghostDurationSeconds"] < merged["ghostPeriodSeconds"] or
                    not .12 < merged["ghostRangeMeters"] < 11.5 or
                    not 0 < merged["obstacleRadius"] < 1 or
                    not 0 < merged["secondObstacleRadius"] < 1 or
                    not 0 <= merged["callbackDelayMilliseconds"] <= 1000 or
                    not 0 <= merged["pathAckDelayMilliseconds"] <= 3000 or
                    not 0 <= merged["pathCancelAckDelayMilliseconds"] <= 3000 or
                    not isinstance(merged["heartbeatDropEvery"], int) or
                    not 0 <= merged["heartbeatDropEvery"] <= 1000 or
                    not 0 <= merged["heartbeatBlackoutSeconds"] <= 10 or
                    not isinstance(merged["dropPathCancelCount"], int) or
                    not 0 <= merged["dropPathCancelCount"] <= 10 or
                    not 0 <= merged["startHoldSeconds"] <= 1):
                raise ValueError("Reference peer configuration is out of range")
            if merged["resetPose"]:
                if self.follower.active:
                    raise ValueError("Cannot reset the ideal pose while a path is active")
                self.pose = [merged["resetPoseX"], merged["resetPoseY"], merged["resetPoseYaw"]]
                self.sensor_pose = tuple(self.pose)
                self.next_sensor = 0.0
            if merged["startHoldSeconds"] != self.config["startHoldSeconds"]:
                self.hold = self.protocol.StartHold(merged["startHoldSeconds"])
            changed_permission = merged["motionPermitted"] != self.config["motionPermitted"]
            # Positive values arm a one-shot fault. The controller sends a full
            # default-zero config next frame; zero must not undo an armed fault.
            if merged["heartbeatBlackoutSeconds"] > 0:
                self._heartbeat_blackout_until = max(self._heartbeat_blackout_until,
                    time.monotonic() + merged["heartbeatBlackoutSeconds"])
            if merged["dropPathCancelCount"] > 0:
                self._remaining_cancel_drops = max(self._remaining_cancel_drops,
                    merged["dropPathCancelCount"])
            self.config = merged
            self.config["resetPose"] = False
            self.config["heartbeatBlackoutSeconds"] = 0.0
            self.config["dropPathCancelCount"] = 0
        if changed_permission:
            self.server.set_motion_permitted(merged["motionPermitted"], "reference test arbiter")
        return {"ok": True}

    def release_cancel(self):
        with self.lock:
            pending, self.receipts = self.receipts, []
            self.config["holdCancelAck"] = False
        for deliver in pending:
            deliver()
        return {"ok": True}

    def obstacles(self):
        for prefix in ("obstacle", "secondObstacle"):
            if self.config[prefix]:
                yield (self.config[prefix + "X"], self.config[prefix + "Y"],
                       self.config[prefix + "Radius"])

    def obstacle_range(self, angle):
        closest = None
        for x, y, radius in self.obstacles():
            dx, dy = x - self.pose[0], y - self.pose[1]
            along = dx * math.cos(angle) + dy * math.sin(angle)
            disc = radius ** 2 - (dx * dx + dy * dy - along * along)
            if disc < 0 or along <= 0:
                continue
            hit = along - math.sqrt(disc)
            if hit >= .02 and (closest is None or hit < closest):
                closest = hit
        return closest

    def update_sensors(self, now):
        if self.config["pauseSensors"]:
            return
        self.scan_number += 1
        self.sensor_at = self.scan_at = now
        self.sensor_pose = tuple(self.pose)
        elapsed = max(0.0, now - self.started)
        phase = elapsed % self.config["ghostPeriodSeconds"]
        ghost = (self.config["periodicGhosts"] and self.follower.active and
                 elapsed >= .4 and phase < self.config["ghostDurationSeconds"])
        if ghost:
            self.ghost_scans += 1
        self.ranges = []
        for i in range(450):
            if self.config["allBeamsMissing"] or 435 <= i <= 447:
                self.ranges.append(None)
                continue
            measured = self.obstacle_range(self.pose[2] + i * 2 * math.pi / 450)
            value = 3.0 if measured is None else measured
            if ghost and i in (1, 449):
                value = min(value, self.config["ghostRangeMeters"])
            self.ranges.append(value)
        raw = [math.nan if value is None else value for value in self.ranges]
        self.scan_points = self.following.scan_to_points(
            raw, 0, 2 * math.pi / 450,
            max(.02, getattr(self.follower.cfg, "scan_min_range", .02)),
            min(12, getattr(self.follower.cfg, "scan_max_range", 12)))
        if hasattr(self.scan_points, "source_stamp"):
            self.scan_points.source_stamp = self.scan_number

    def sample_contact(self):
        for x, y, radius in self.obstacles():
            dx, dy = x - self.pose[0], y - self.pose[1]
            forward = dx * math.cos(self.pose[2]) + dy * math.sin(self.pose[2])
            side = -dx * math.sin(self.pose[2]) + dy * math.cos(self.pose[2])
            fx, sy = max(-.163, min(.163, forward)), max(-.1245, min(.1245, side))
            if (forward - fx) ** 2 + (side - sy) ** 2 <= radius ** 2:
                self.collision_samples += 1
                return

    def run(self):
        try:
            while not self.stopping.wait(.005):
                now = time.monotonic()
                if now - self.last_tick < .019:
                    continue
                dt = min(.1, now - self.last_tick)
                self.last_tick = now
                with self.server.lock:
                    session_state, owner = self.session.state, self.session.owner
                with self.lock:
                    if now >= self.next_sensor:
                        self.next_sensor = now + .1
                        self.update_sensors(now)
                    if self.follower.active:
                        if session_state not in ("idle", "active"):
                            self.follower.cancel("session " + session_state)
                        elif owner != self.follower.owner:
                            self.follower.cancel("owner changed")
                        if self.follower.active:
                            self.target = self.follower.update(now, self.sensor_pose,
                                                               self.sensor_at, self.scan_points, self.scan_at)
                        else:
                            self.target = (0.0, 0.0)
                    elif self.follower.state in ("done", "cancelled"):
                        self.target = (0.0, 0.0)
                    if not self.config["motionPermitted"]:
                        self.target = (0.0, 0.0)
                    if self.target == (0.0, 0.0):
                        self.hold.reset()
                        self.slew.reset()
                        self.output = (0.0, 0.0)
                    else:
                        self.output = self.slew.update(*self.hold.update(*self.target, now), now)
                    x, yaw = self.output
                    self.pose[0] += math.cos(self.pose[2]) * x / 100 * dt
                    self.pose[1] += math.sin(self.pose[2]) * x / 100 * dt
                    self.pose[2] = self.following.wrap(self.pose[2] + yaw * dt)
                    self.sample_contact()
        except Exception as exc:
            with self.lock:
                self.error = type(exc).__name__ + ": " + str(exc)
            self.stopping.set()

    def snapshot(self):
        with self.server.lock:
            session = self.session.snapshot()
        with self.lock:
            now = time.monotonic()
            path = self.follower.status(now, self.sensor_pose)
            stamp = {"sec": 1000 + self.scan_number // 10,
                     "nanosec": self.scan_number % 10 * 100000000}
            pose = {"op": "publish", "topic": "/lidar_map/pose", "msg": {
                "header": {"frame_id": "map", "stamp": stamp},
                "pose": {"position": {"x": self.sensor_pose[0], "y": self.sensor_pose[1]},
                         "orientation": {"z": math.sin(self.sensor_pose[2] / 2),
                                         "w": math.cos(self.sensor_pose[2] / 2)}}}}
            scan = {"op": "publish", "topic": "/scan", "msg": {
                "header": {"frame_id": "laser", "stamp": stamp}, "angle_min": 0,
                "angle_max": 2 * math.pi * 449 / 450, "angle_increment": 2 * math.pi / 450,
                "range_min": .02, "range_max": 12, "ranges": self.ranges}}
            status = dict(media="puppypi-media/1", age_s=0, session_state=session["state"],
                          emergency_stop=session["emergency_stop"], path=path)
            dumps = lambda value: json.dumps(value, separators=(",", ":"), allow_nan=False)
            return dict(x=self.pose[0], y=self.pose[1], yaw=self.pose[2],
                        state=self.follower.state, active=self.follower.active,
                        index=self.follower.index, started=self.started,
                        elapsed=max(0.0, now-self.started) if self.started else 0.0,
                        pointsJson=dumps(self.follower.points), pathJson=dumps(status),
                        poseJson=dumps(pose), scanJson=dumps(scan), scanNumber=self.scan_number,
                        paths=self.paths, cancels=self.cancels, heartbeats=self.heartbeats,
                        nonNeutral=self.non_neutral, error=self.error, events=self.events,
                        collisionSamples=self.collision_samples, ghostScans=self.ghost_scans,
                        blockedCount=self.follower.blocked_count,
                        droppedHeartbeats=self.dropped_heartbeats,
                        droppedCancels=self.dropped_cancels,
                        emergencyCommands=self.emergency_commands,
                        pathDatagrams=self.path_datagrams,
                        cancelDatagrams=self.cancel_datagrams,
                        emergencyDatagrams=self.emergency_datagrams,
                        validRays=len(self.scan_points) if self.scan_points is not None else 0,
                        lastPathSequence=self.last_path_sequence,
                        cancelAckAt=self.cancel_ack_at if math.isfinite(self.cancel_ack_at) else -1,
                        motionPermitted=self.config["motionPermitted"])

    def close(self):
        self.stopping.set()
        for timer in self.timers:
            timer.cancel()
        self.thread.join(timeout=2)
        self.server.shutdown()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--robot-src", required=True, type=Path)
    args = parser.parse_args()
    peer = ReferencePeer(args.robot_src)
    try:
        hello = dict(udp_port=peer.server.bound_port, source_sha256={
            name: hashlib.sha256((peer.source / name).read_bytes()).hexdigest()
            for name in ("protocol_v2.py", "v2_server.py", "path_follower.py")})
        print(json.dumps(hello, separators=(",", ":")), flush=True)
        for line in sys.stdin:
            request = json.loads(line)
            op = request.get("op")
            if op == "configure":
                result = peer.configure(request.get("values"))
            elif op == "snapshot":
                result = peer.snapshot()
            elif op == "release_cancel_receipts":
                result = peer.release_cancel()
            elif op == "shutdown":
                print('{"ok":true}', flush=True)
                break
            else:
                raise ValueError("Unknown reference peer operation")
            print(json.dumps(result, separators=(",", ":"), allow_nan=False), flush=True)
    finally:
        peer.close()


if __name__ == "__main__":
    main()
