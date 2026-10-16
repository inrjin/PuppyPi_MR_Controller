using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using UnityEngine;

namespace PuppyPi
{
    // UI-facing connection lifecycle. No DNS or blocking socket work on Unity's main thread.
    public sealed class PuppyPiConnection : MonoBehaviour
    {
        private const string PreferenceKey = "PuppyPi.Connection.v1.";
        public PuppyPiV2Client client;
        public string AddressText = "";
        public string PortText = "5001";
        public string Notice { get; private set; } = "같은 공유기에 연결한 뒤 기기를 검색하세요.";
        public string SavedDeviceId { get; private set; } = "";
        public string SavedName { get; private set; } = "";
        public PuppyPiDiscovery Discovery { get; } = new PuppyPiDiscovery();
        public bool Resolving => _lookup != null;
        public bool HasSearched { get; private set; }
        private Task<IPAddress[]> _lookup;
        private double _deadline;
        private bool _waiting;
        private bool _saveOnAck;
        private int _requestedPort;
        private string _requestedHost;
        private string _deviceId = "";
        private string _deviceName = "";
        private static double Now => Time.realtimeSinceStartupAsDouble;

        private void Awake()
        {
            AddressText = PlayerPrefs.GetString(PreferenceKey + "host", "");
            PortText = PlayerPrefs.GetInt(PreferenceKey + "port", 5001).ToString(CultureInfo.InvariantCulture);
            SavedDeviceId = PlayerPrefs.GetString(PreferenceKey + "id", "");
            SavedName = PlayerPrefs.GetString(PreferenceKey + "name", "");
        }

        public void Search()
        {
            HasSearched = true;
            Discovery.Start(Now);
            Notice = "같은 네트워크의 PuppyPi를 검색하고 있습니다…";
        }

        public void ConnectDevice(DiscoveredPi device)
        {
            if (device == null || Now - device.LastSeen > PuppyPiDiscovery.ResultLifetime)
            { Notice = "검색 결과가 만료되었습니다. 다시 검색하세요."; return; }
            AddressText = device.Address;
            PortText = device.Port.ToString(CultureInfo.InvariantCulture);
            BeginConnect(device.Address, device.Port, device.DeviceId, device.Name);
        }

        public void ConnectManual()
        {
            if (!int.TryParse(PortText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port < 1 || port > 65535)
            { Notice = "포트는 1–65535 사이의 숫자로 입력하세요."; return; }
            BeginConnect(AddressText.Trim(), port, "", "직접 입력");
        }

        private void BeginConnect(string host, int port, string id, string name)
        {
            if (client == null) { Notice = "통신 컴포넌트가 연결되지 않았습니다."; return; }
            UriHostNameType hostType = Uri.CheckHostName(host);
            if (host.Length > 253 || (hostType != UriHostNameType.IPv4 && hostType != UriHostNameType.Dns))
            { Notice = "Pi의 IPv4 주소 또는 호스트 이름을 입력하세요."; return; }
            // Retire the previous destination before DNS starts. A late DNS result is
            // ignored after another connect/disconnect; it can never switch the robot.
            Disconnect();
            _requestedHost = host;
            _requestedPort = port;
            _deviceId = id;
            _deviceName = name;
            _deadline = Now + 5;
            if (IPAddress.TryParse(host, out IPAddress address)) ConnectResolved(address);
            else
            {
                Notice = "주소를 확인하고 있습니다…";
                try { _lookup = Dns.GetHostAddressesAsync(host); }
                catch (Exception e) when (e is SocketException || e is ArgumentException)
                { Notice = "주소를 찾지 못했습니다. IP를 직접 입력하거나 다시 검색하세요."; }
            }
        }

        private void ConnectResolved(IPAddress address)
        {
            if (!client.ConnectTo(address.ToString(), _requestedPort, out string error))
            { Notice = error; return; }
            _waiting = true;
            _saveOnAck = true;
            _deadline = Now + 5;
            Notice = "Pi의 조종 서버 응답을 기다리고 있습니다…";
        }

        public void Disconnect()
        {
            _lookup = null;
            _waiting = false;
            _saveOnAck = false;
            if (client != null) client.DisconnectSession();
            Notice = "연결을 해제했습니다.";
        }

        private void Update()
        {
            bool wasSearching = Discovery.Searching;
            Discovery.Tick(Now);
            if (wasSearching && !Discovery.Searching && Notice == "같은 네트워크의 PuppyPi를 검색하고 있습니다…")
                Notice = Discovery.Devices.Count > 0 ? "검색이 끝났습니다. 연결할 기기를 선택하세요." : "기기를 찾지 못했습니다. 다시 검색하거나 주소를 직접 입력하세요.";
            if (_lookup != null)
            {
                if (Now > _deadline) { _lookup = null; Notice = "주소 확인 시간이 초과되었습니다."; }
                else if (_lookup.IsCompleted)
                {
                    Task<IPAddress[]> lookup = _lookup;
                    _lookup = null;
                    IPAddress address = null;
                    try
                    {
                        foreach (IPAddress result in lookup.GetAwaiter().GetResult())
                            if (result.AddressFamily == AddressFamily.InterNetwork) { address = result; break; }
                    }
                    catch (Exception e) when (e is SocketException || e is ArgumentException) { }
                    if (address == null) Notice = "IPv4 주소를 찾지 못했습니다. 기기를 검색하거나 IP를 입력하세요.";
                    else ConnectResolved(address);
                }
            }
            if (client == null) return;
            if (_waiting && client.Session.Connected)
            {
                _waiting = false;
                Notice = client.Session.LastAccepted ? "연결되었습니다. 그립을 놓았다가 새로 잡아 주세요." : "Pi가 응답했지만 조종 요청을 수락하지 않았습니다.";
            }
            if (_waiting && Now > _deadline)
            {
                Disconnect();
                Notice = "응답이 없습니다. Pi의 v2 조종 서버와 주소/포트를 확인하세요.";
            }
            if (_saveOnAck && client.networkEnabled && client.Session.Connected && client.Session.LastAccepted)
            {
                _saveOnAck = false;
                SavedDeviceId = _deviceId;
                SavedName = _deviceName;
                PlayerPrefs.SetString(PreferenceKey + "host", _requestedHost);
                PlayerPrefs.SetInt(PreferenceKey + "port", _requestedPort);
                PlayerPrefs.SetString(PreferenceKey + "id", SavedDeviceId);
                PlayerPrefs.SetString(PreferenceKey + "name", SavedName);
                PlayerPrefs.Save();
            }
        }

        public string Status
        {
            get
            {
                if (Resolving) return "주소 확인 중";
                if (client == null || !client.networkEnabled) return "연결 안 됨";
                V2Session session = client.Session;
                if (!session.Connected) return _waiting ? "연결 중" : "응답 끊김 · 그립 중립";
                if (session.Faulted) return "로봇 오류 · 제어 중지";
                if (session.Emergency) return "비상정지";
                if (session.ServerReason == "not_owner") return "다른 조종기가 사용 중";
                if (!session.LastAccepted) return "연결됨 · 제어 요청 거부";
                return session.Armed ? "연결됨 · 조작 중" : "연결됨 · 그립을 놓고 다시 잡으세요";
            }
        }

        private void OnDisable()
        {
            _lookup = null;
            Discovery.Dispose();
            if (_waiting) Disconnect();
        }
    }
}
