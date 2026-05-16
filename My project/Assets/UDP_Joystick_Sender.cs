using UnityEngine;
using System.Net.Sockets;
using System.Text;

public class UDP_Joystick_Sender : MonoBehaviour
{
    [Header("네트워크 설정")]
    public string raspberryPi_IP = "192.168.0.100"; // ★ 라즈베리파이의 실제 IP로 변경해야 합니다.
    public int port = 5005;

    [Header("조이스틱 관절 연결")]
    public Transform joystickJoint; // 꺾이는 관절 (BoxLid) 연결

    private UdpClient udpClient;

    void Start()
    {
        udpClient = new UdpClient();
    }

    void Update()
    {
        // 1. 관절(BoxLid)의 로컬 회전 각도 읽기
        float angleX = joystickJoint.localEulerAngles.x;
        float angleZ = joystickJoint.localEulerAngles.z;

        // (유니티 기본 각도는 0~360도이므로, 조종하기 편하게 -180~180도로 변환)
        if (angleX > 180) angleX -= 360;
        if (angleZ > 180) angleZ -= 360;

        // 2. 전송할 문자열 조립 (소수점 1자리까지만 전송)
        string message = $"X:{angleX:F1},Z:{angleZ:F1}";

        // 3. 문자열을 바이트 데이터로 변환 후 전송
        byte[] data = Encoding.UTF8.GetBytes(message);
        udpClient.Send(data, data.Length, raspberryPi_IP, port);
    }

    void OnApplicationQuit()
    {
        // 게임 종료 시 포트 닫기
        if (udpClient != null) udpClient.Close();
    }
}