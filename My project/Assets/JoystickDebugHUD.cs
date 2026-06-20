using UnityEngine;

/// <summary>
/// 라즈베리파이/UDP 없이, Unity 에디터 Play 모드에서 조이스틱 값을 화면에 띄워 확인하는 디버그 HUD.
/// 조이스틱 관절(BoxLid)을 움직이면 화면 좌상단에 X/Z 각도와 로봇 변환 미리보기가 실시간 표시됩니다.
/// 사용법: 아무 GameObject에 이 스크립트를 붙이고 joystickJoint 에 BoxLid 를 연결 후 Play.
/// </summary>
public class JoystickDebugHUD : MonoBehaviour
{
    [Header("조이스틱 관절 연결")]
    [Tooltip("UDP_Joystick_Sender 와 동일한 관절(BoxLid)을 연결하세요.")]
    public Transform joystickJoint;

    [Header("로봇 변환 미리보기 (라즈베리파이 수신기와 동일 로직)")]
    public bool showRobotPreview = true;
    [Tooltip("이 각도(±) 이내는 중립 0 으로 처리")]
    public float deadzone = 5f;
    [Tooltip("이 각도에서 최대값 ±1.0")]
    public float maxAngle = 45f;

    [Header("화면 표시")]
    public bool showOnScreen = true;
    [Tooltip("켜면 Unity Console 에도 값을 출력")]
    public bool alsoLogToConsole = false;
    public int fontSize = 26;
    public Color textColor = new Color(0.3f, 1f, 0.3f);

    private float angleX, angleZ, forward, turn;
    private GUIStyle style;

    void Update()
    {
        if (joystickJoint == null) return;

        // UDP_Joystick_Sender 와 똑같이 각도 읽고 -180~180 으로 변환
        angleX = joystickJoint.localEulerAngles.x;
        angleZ = joystickJoint.localEulerAngles.z;
        if (angleX > 180f) angleX -= 360f;
        if (angleZ > 180f) angleZ -= 360f;

        forward = Normalize(angleZ); // 전/후진
        turn = Normalize(angleX);    // 좌/우 회전

        if (alsoLogToConsole)
            Debug.Log($"[JoystickDebugHUD] X:{angleX:F1} Z:{angleZ:F1}  전진:{forward:F2} 회전:{turn:F2}");
    }

    private float Normalize(float angle)
    {
        if (Mathf.Abs(angle) < deadzone) return 0f;
        return Mathf.Clamp(angle / maxAngle, -1f, 1f);
    }

    void OnGUI()
    {
        if (!showOnScreen) return;

        if (style == null) style = new GUIStyle();
        style.fontSize = fontSize;
        style.fontStyle = FontStyle.Bold;
        style.normal.textColor = textColor;

        string text;
        if (joystickJoint == null)
        {
            text = "[JoystickDebugHUD] joystickJoint(BoxLid)를 인스펙터에 연결하세요";
        }
        else
        {
            text = $"조이스틱   X: {angleX,7:F1}°    Z: {angleZ,7:F1}°";
            if (showRobotPreview)
                text += $"\n로봇변환   전진: {forward,6:F2}    회전: {turn,6:F2}";
        }

        // 가독성을 위한 반투명 검은 배경
        Rect rect = new Rect(15, 15, 760, (showRobotPreview && joystickJoint != null) ? 95 : 55);
        Color old = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = old;

        GUI.Label(new Rect(rect.x + 12, rect.y + 8, rect.width - 20, rect.height - 12), text, style);
    }
}
