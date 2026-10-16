using System.Net;
using System.Text;
using NUnit.Framework;

namespace PuppyPi.Tests
{
    public class DiscoveryTests
    {
        private const string Nonce = "0123456789abcdef0123456789abcdef";
        private const string Device = "abcdef0123456789abcdef0123456789";
        private static string Reply => $"PUPPYPI_HERE/1|{Nonce}|{Device}|%ED%8D%BC%ED%94%BC%20%ED%8C%8C%EC%9D%B4|5001|2";

        [Test]
        public void DiscoveryUsesObservedAddressAndStableIdentityAcrossDhcpChanges()
        {
            Assert.That(Parse(Reply, "192.168.10.20", out var first), Is.True);
            Assert.That(Parse(Reply, "192.168.30.40", out var second), Is.True);
            Assert.That(first.DeviceId, Is.EqualTo(second.DeviceId));
            Assert.That(second.Address, Is.EqualTo("192.168.30.40"));
            Assert.That(second.Name, Is.EqualTo("퍼피 파이"));
            Assert.That(second.Port, Is.EqualTo(5001));
        }

        [TestCase("nonce")] [TestCase("version")] [TestCase("port")]
        [TestCase("id")] [TestCase("control")] [TestCase("fields")]
        [TestCase("encoding")] [TestCase("oversize")]
        public void MalformedOrUnsolicitedRepliesAreIgnored(string kind)
        {
            string packet = Reply;
            switch (kind)
            {
                case "nonce": packet = packet.Replace(Nonce, "f" + Nonce.Substring(1)); break;
                case "version": packet = packet.Substring(0, packet.Length - 1) + "1"; break;
                case "port": packet = packet.Replace("5001", "65536"); break;
                case "id": packet = packet.Replace(Device, "not-a-device"); break;
                case "control": packet = packet.Replace("%ED%8D%BC", "%0A"); break;
                case "fields": packet += "|192.168.0.9"; break;
                case "encoding": packet = packet.Replace("%ED", "%GG"); break;
                case "oversize": packet += new string('a', 700); break;
            }
            Assert.That(Parse(packet, "127.0.0.1", out _), Is.False);
        }

        [Test]
        public void WrongDiscoveryPortIsIgnored()
        {
            Assert.That(PuppyPiDiscoveryProtocol.TryParse(Encoding.ASCII.GetBytes(Reply),
                new IPEndPoint(IPAddress.Loopback, 5001), Nonce, 1, out _), Is.False);
        }

        private static bool Parse(string packet, string source, out DiscoveredPi pi) =>
            PuppyPiDiscoveryProtocol.TryParse(Encoding.ASCII.GetBytes(packet),
                new IPEndPoint(IPAddress.Parse(source), 5002), Nonce, 1, out pi);
    }
}
