using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class UDP_Joystick_Sender : MonoBehaviour
{
    [Header("네트워크 설정")]
    public string raspberryPi_IP = "192.168.0.100"; // ★ 라즈베리파이의 실제 IP로 변경해야 합니다.
    public int port = 5005;

    [Header("전송 설정")]
    [Tooltip("초당 전송 횟수(Hz). 매 프레임 보내면 네트워크가 폭주하므로 제한합니다.")]
    public float sendRate = 20f;
    [Tooltip("켜면 전송하는 값을 콘솔에 출력해 디버깅할 수 있습니다.")]
    public bool debugLog = false;

    [Header("조이스틱 관절 연결")]
    public Transform joystickJoint; // 꺾이는 관절 (BoxLid) 연결

    private UdpClient udpClient;
    private float sendInterval;
    private float timer;

    void Start()
    {
        udpClient = new UdpClient();
        sendInterval = (sendRate > 0f) ? 1f / sendRate : 0f;

        if (joystickJoint == null)
            Debug.LogWarning("[UDP_Joystick_Sender] joystickJoint(BoxLid)가 연결되지 않았습니다. 인스펙터에서 연결하세요.");
    }

    void Update()
    {
        // 관절이 연결되지 않았으면 전송하지 않음 (NullReference 방지)
        if (joystickJoint == null) return;

        // 전송 주기 제한: sendRate(Hz)에 맞춰서만 전송
        timer += Time.deltaTime;
        if (timer < sendInterval) return;
        timer = 0f;

        // 1. 관절(BoxLid)의 로컬 회전 각도 읽기
        float angleX = joystickJoint.localEulerAngles.x;
        float angleZ = joystickJoint.localEulerAngles.z;

        // (유니티 기본 각도는 0~360도이므로, 조종하기 편하게 -180~180도로 변환)
        if (angleX > 180) angleX -= 360;
        if (angleZ > 180) angleZ -= 360;

        // 2. 전송할 문자열 조립 (소수점 1자리까지만 전송)
        string message = $"X:{angleX:F1},Z:{angleZ:F1}";

        // 3. 문자열을 바이트 데이터로 변환 후 전송 (예외 발생해도 게임이 멈추지 않도록 보호)
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);
            udpClient.Send(data, data.Length, raspberryPi_IP, port);

            if (debugLog)
                Debug.Log($"[UDP_Joystick_Sender] 전송 -> {raspberryPi_IP}:{port}  {message}");
        }
        catch (SocketException e)
        {
            Debug.LogWarning($"[UDP_Joystick_Sender] 전송 실패: {e.Message}");
        }
    }

    void OnApplicationQuit()
    {
        // 게임 종료 시 포트 닫기
        if (udpClient != null) udpClient.Close();
    }
}
