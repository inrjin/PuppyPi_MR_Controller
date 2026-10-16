import importlib.util
from pathlib import Path
import socket
import unittest
from urllib.parse import unquote

MODULE = Path(__file__).resolve().parents[1] / "discovery_service.py"
spec = importlib.util.spec_from_file_location("discovery_service", MODULE)
discovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(discovery)


class DiscoveryTests(unittest.TestCase):
    def test_valid_probe_and_unicode_name(self):
        nonce = "a" * 32
        reply = discovery.make_reply(discovery.REQUEST + nonce.encode(), "퍼피 파이", "b" * 32, 5001)
        fields = reply.decode("ascii").split("|")
        self.assertEqual(fields[:3], [discovery.REPLY, nonce, "b" * 32])
        self.assertEqual(unquote(fields[3]), "퍼피 파이")
        self.assertEqual(fields[4:], ["5001", "2"])

    def test_control_packets_and_malformed_probes_get_no_reply(self):
        for packet in (b'{"protocol":2,"type":"command"}', b"X:0,Z:0", discovery.REQUEST,
                       discovery.REQUEST + b"g" * 32, discovery.REQUEST + b"a" * 33,
                       discovery.REQUEST + b"a" * 32 + b"\n", b"a" * 2048):
            self.assertIsNone(discovery.make_reply(packet, "Pi", "b" * 32, 5001))

    def test_udp_reply_uses_request_source_and_does_not_claim_control_port(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as control:
            control.bind(("127.0.0.1", 0))
            control.settimeout(0.1)
            server = discovery.DiscoveryServer("Pi", "c" * 32, control.getsockname()[1], "127.0.0.1", 0)
            server.start()
            try:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                    client.settimeout(1)
                    for nonce in (b"a" * 32, b"b" * 32):
                        client.sendto(discovery.REQUEST + nonce, ("127.0.0.1", server.bound_port))
                        reply, peer = client.recvfrom(640)
                        self.assertEqual(peer, ("127.0.0.1", server.bound_port))
                        self.assertIn(b"|" + nonce + b"|", reply)
                    with self.assertRaises(socket.timeout):
                        control.recvfrom(2048)
            finally:
                server.shutdown()

    def test_configuration_and_stable_identity(self):
        self.assertEqual(discovery.device_id(), discovery.device_id())
        self.assertRegex(discovery.device_id(), r"^[0-9a-f]{32}$")
        for kwargs in ({"name": ""}, {"name": "x\ny"}, {"name": "x" * 33},
                       {"identity": "wrong"}, {"control_port": 0}, {"control_port": 65536}):
            with self.assertRaises(ValueError):
                discovery.DiscoveryServer(**kwargs)


if __name__ == "__main__":
    unittest.main()
