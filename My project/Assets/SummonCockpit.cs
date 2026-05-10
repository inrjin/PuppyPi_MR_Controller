using UnityEngine;

public class SummonCockpit : MonoBehaviour
{
    [Header("연결할 부품들")]
    public Transform headCamera;   // 진짜 내 눈 (CenterEyeAnchor)
    public Transform cockpitBase;  // 통째로 움직일 조이스틱 받침대

    [Header("소환될 위치 세팅 (1 = 1미터)")]
    public float distanceForward = 0.4f; // 눈앞으로 40cm
    public float distanceDown = 0.4f;    // 눈에서 아래로 40cm (가슴~배 높이)

    void Update()
    {
        // 오른손 엄지와 검지를 맞대는 핀치(Pinch) 제스처를 감지!
        // (VR 컨트롤러를 들고 있다면 A버튼을 눌러도 똑같이 작동합니다)
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            Summon();
        }
    }

    public void Summon()
    {
        // 1. 머리 앞쪽 위치 계산
        Vector3 targetPosition = headCamera.position + (headCamera.forward * distanceForward);
        
        // 2. 높이를 내 배꼽/가슴 위치로 내리기 (월드 Y축 기준)
        targetPosition.y -= distanceDown;

        // 3. 조이스틱 받침대 순간이동!
        cockpitBase.position = targetPosition;

        // 4. 조이스틱이 항상 나를 바라보도록 회전 (대신 고개를 숙여도 조이스틱이 기울지 않게 Y축만 회전)
        Vector3 targetRotation = headCamera.eulerAngles;
        targetRotation.x = 0; 
        targetRotation.z = 0; 
        cockpitBase.eulerAngles = targetRotation;
    }
}