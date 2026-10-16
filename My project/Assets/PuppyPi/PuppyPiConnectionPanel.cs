using UnityEngine;

namespace PuppyPi
{
    // Desktop Game view controls; Quest continues to show the world-space HUD.
    // Created by the existing HUD, so existing scenes need no manual wiring.
    public sealed class PuppyPiConnectionPanel
    {
        private Vector2 _scroll;
        private bool _expanded = true;

        public void Draw(PuppyPiConnection connection, BallGripInput input)
        {
            if (connection == null || connection.client == null) return;
            var client = connection.client;
            float width = Mathf.Min(570, Screen.width - 20);
            float height = Mathf.Min(_expanded ? 570 : 155, Screen.height - 20);
            GUILayout.BeginArea(new Rect(10, 10, width, height), GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("PuppyPi 연결", GUI.skin.box, GUILayout.ExpandWidth(true));
            if (GUILayout.Button(_expanded ? "접기" : "펼치기", GUILayout.Width(65))) _expanded = !_expanded;
            GUILayout.EndHorizontal();
            GUILayout.Label(connection.Status);
            if (client.networkEnabled) GUILayout.Label($"{client.robotAddress}:{client.port}  |  응답: {(client.Session.Connected ? (client.AckAge * 1000).ToString("F0") + " ms 전" : "대기 중")}");
            GUILayout.BeginHorizontal();
            Color prior = GUI.backgroundColor;
            GUI.backgroundColor = new Color(1, 0.35f, 0.3f);
            if (GUILayout.Button("비상정지 (B)", GUILayout.Height(30))) client.EmergencyStop();
            GUI.backgroundColor = prior;
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && client.networkEnabled && (input == null || !input.Grabbed);
            if (GUILayout.Button("정지 해제 (Y)", GUILayout.Height(30))) client.ClearEmergency();
            GUI.enabled = wasEnabled;
            GUILayout.EndHorizontal();
            if (_expanded)
            {
                _scroll = GUILayout.BeginScrollView(_scroll);
                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                GUI.enabled = wasEnabled && !connection.Discovery.Searching;
                if (GUILayout.Button(connection.Discovery.Searching ? "검색 중…" : "기기 검색", GUILayout.Height(30))) connection.Search();
                GUI.enabled = wasEnabled && (client.networkEnabled || connection.Resolving);
                if (GUILayout.Button("연결 해제", GUILayout.Height(30))) connection.Disconnect();
                GUI.enabled = wasEnabled;
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(connection.Discovery.Error)) GUILayout.Label(connection.Discovery.Error);
                foreach (DiscoveredPi pi in connection.Discovery.Devices)
                {
                    GUILayout.BeginHorizontal(GUI.skin.box);
                    string recent = pi.DeviceId == connection.SavedDeviceId ? " · 최근 연결" : "";
                    GUILayout.Label($"{pi.Name}{recent}\n{pi.Address}:{pi.Port}", GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("연결", GUILayout.Width(65), GUILayout.Height(38))) connection.ConnectDevice(pi);
                    GUILayout.EndHorizontal();
                }
                if (connection.HasSearched && !connection.Discovery.Searching && connection.Discovery.Devices.Count == 0)
                    GUILayout.Label("기기를 찾지 못했습니다. Pi 검색 서비스와 같은 공유기 연결을 확인하거나 주소를 입력하세요.");
                GUILayout.Space(8);
                GUILayout.Label("주소 직접 입력 (IP 또는 puppypi.local)");
                GUILayout.BeginHorizontal();
                connection.AddressText = GUILayout.TextField(connection.AddressText, 253, GUILayout.MinWidth(100));
                GUILayout.Label(":", GUILayout.Width(8));
                connection.PortText = GUILayout.TextField(connection.PortText, 5, GUILayout.Width(55));
                if (GUILayout.Button("연결", GUILayout.Width(65))) connection.ConnectManual();
                GUILayout.EndHorizontal();
                GUILayout.Label(connection.Notice, GUI.skin.box);
                if (input != null)
                    GUILayout.Label($"입력  전후 {input.Axes.x:F2} / 회전 {input.Axes.y:F2}   ·   잡기 {(input.Grabbed ? "ON" : "OFF")}");
                GUILayout.Label("IP가 바뀌면 다시 검색 → 기기 선택 → 연결\n검색은 조종을 시작하지 않습니다. 연결 후 그립을 다시 잡으세요.");
                GUILayout.EndScrollView();
            }
            GUILayout.EndArea();
        }
    }
}
