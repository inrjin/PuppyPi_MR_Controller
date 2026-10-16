using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace PuppyPi
{
    public sealed class DiscoveredPi
    {
        public string DeviceId { get; internal set; }
        public string Name { get; internal set; }
        public string Address { get; internal set; }
        public int Port { get; internal set; }
        public double LastSeen { get; internal set; }
    }

    // Discovery never sends control packets or claims a robot's control lease.
    public static class PuppyPiDiscoveryProtocol
    {
        public const int Port = 5002;
        public const string RequestPrefix = "PUPPYPI_DISCOVER/1|";
        public const string ReplyPrefix = "PUPPYPI_HERE/1|";
        private static readonly Regex HexId = new Regex("\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
        private static readonly Regex EncodedName = new Regex("\\A(?:[A-Za-z0-9_.~-]|%[0-9A-Fa-f]{2}){1,384}\\z", RegexOptions.CultureInvariant);

        public static bool TryParse(byte[] packet, IPEndPoint source, string nonce, double now, out DiscoveredPi pi)
        {
            pi = null;
            if (packet == null || packet.Length > 640 || packet.Length < 80 || source == null ||
                source.Address.AddressFamily != AddressFamily.InterNetwork || source.Port != Port ||
                nonce == null || !HexId.IsMatch(nonce)) return false;
            foreach (byte value in packet) if (value < 32 || value > 126) return false;
            string[] fields = Encoding.ASCII.GetString(packet).Split('|');
            if (fields.Length != 6 || fields[0] != "PUPPYPI_HERE/1" || fields[1] != nonce ||
                !HexId.IsMatch(fields[2]) || !EncodedName.IsMatch(fields[3]) || fields[5] != "2" ||
                !int.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
                return false;
            string name = Uri.UnescapeDataString(fields[3]);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64) return false;
            foreach (char value in name) if (char.IsControl(value)) return false;
            pi = new DiscoveredPi { DeviceId = fields[2], Name = name, Address = source.Address.ToString(), Port = port, LastSeen = now };
            return true;
        }
    }

    public sealed class PuppyPiDiscovery : IDisposable
    {
        public const double ResultLifetime = 10;
        private readonly List<DiscoveredPi> _devices = new List<DiscoveredPi>();
        private readonly HashSet<IPAddress> _targets = new HashSet<IPAddress>();
        private UdpClient _socket;
        private string _nonce;
        private double _until;
        private double _nextProbe;
        public IReadOnlyList<DiscoveredPi> Devices => _devices;
        public bool Searching => _socket != null;
        public string Error { get; private set; }

        public void Start(double now, bool loopbackOnly = false)
        {
            Dispose();
            _devices.Clear();
            Error = null;
            _nonce = Guid.NewGuid().ToString("N");
            _until = now + 3;
            _nextProbe = now;
            _targets.Clear();
            if (!loopbackOnly) _targets.Add(IPAddress.Broadcast);
            _targets.Add(IPAddress.Loopback);
            // Directed broadcasts cover Ethernet/Wi-Fi when several adapters exist.
            try
            {
                foreach (var adapter in loopbackOnly ? new NetworkInterface[0] : NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask == null) continue;
                        byte[] address = unicast.Address.GetAddressBytes(), mask = unicast.IPv4Mask.GetAddressBytes();
                        if ((mask[3] & 254) == 254) continue; // /31 and /32 have no broadcast.
                        for (int i = 0; i < 4; i++) address[i] |= (byte)~mask[i];
                        _targets.Add(new IPAddress(address));
                    }
                }
            }
            catch (NetworkInformationException) { }
            catch (NotSupportedException) { }
            try
            {
                _socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                _socket.EnableBroadcast = true;
                _socket.Client.Blocking = false;
            }
            catch (SocketException e) { Error = e.Message; Dispose(); }
        }

        public void Tick(double now)
        {
            _devices.RemoveAll(pi => now - pi.LastSeen > ResultLifetime);
            if (_socket == null) return;
            if (now >= _until) { Dispose(); return; }
            if (now >= _nextProbe)
            {
                _nextProbe = now + 0.7;
                byte[] probe = Encoding.ASCII.GetBytes(PuppyPiDiscoveryProtocol.RequestPrefix + _nonce);
                bool sent = false;
                foreach (IPAddress target in _targets)
                {
                    try { _socket.Send(probe, probe.Length, new IPEndPoint(target, PuppyPiDiscoveryProtocol.Port)); sent = true; }
                    catch (SocketException) { } // One unavailable interface must not break the other ones.
                }
                if (!sent) Error = "검색 요청을 보내지 못했습니다. 네트워크 연결을 확인하세요.";
            }
            try
            {
                for (int i = 0; i < 64 && _socket.Available > 0; i++)
                {
                    var source = new IPEndPoint(IPAddress.Any, 0);
                    byte[] packet = _socket.Receive(ref source);
                    if (!PuppyPiDiscoveryProtocol.TryParse(packet, source, _nonce, now, out DiscoveredPi found)) continue;
                    int existing = _devices.FindIndex(pi => pi.DeviceId == found.DeviceId && pi.Address == found.Address);
                    if (existing >= 0) _devices[existing] = found;
                    else if (_devices.Count < 32) _devices.Add(found);
                }
            }
            catch (SocketException e)
            {
                if (e.SocketErrorCode != SocketError.WouldBlock && e.SocketErrorCode != SocketError.ConnectionReset)
                { Error = e.Message; Dispose(); }
            }
        }

        public void Dispose()
        {
            _socket?.Close();
            _socket = null;
        }
    }
}
