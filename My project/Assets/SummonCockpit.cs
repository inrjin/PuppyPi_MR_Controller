using UnityEngine;

public class SummonCockpit : MonoBehaviour
{
    [Header("연결할 부품들")]
    public Transform headCamera;   // 진짜 내 눈 (CenterEyeAnchor)
    public Transform cockpitBase;  // 통째로 움직일 조이스틱 받침대

    [Header("소환될 위치 세팅 (1 = 1미터)")]
    public float distanceForward = 0.4f; // 눈앞으로 40cm
    public float distanceDown = 0.4f;    // 눈에서 아래로 40cm (가슴~배 높이)

    private Quaternion authoredTilt;

    void Awake()
    {
        if (cockpitBase != null)
            authoredTilt = Quaternion.Inverse(Quaternion.Euler(0, cockpitBase.eulerAngles.y, 0)) * cockpitBase.rotation;
    }

    void Update()
    {
        // 오른쪽 컨트롤러의 A 버튼으로 콘솔을 다시 배치합니다.
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch))
        {
            Summon();
        }
    }

    public void Summon()
    {
        if (headCamera == null || cockpitBase == null) return;
        var input = cockpitBase.GetComponent<PuppyPi.BallGripInput>();
        if (input != null) input.Invalidate();
        Vector3 targetPosition = headCamera.position + headCamera.forward * distanceForward;
        targetPosition.y -= distanceDown;
        Quaternion targetOrientation = Quaternion.Euler(0, headCamera.eulerAngles.y, 0) * authoredTilt;
        var placement = cockpitBase.GetComponent<PuppyPi.PuppyPiCockpitPlacement>();
        if (placement != null)
        {
            placement.CancelGrab();
            placement.MoveTo(new Pose(targetPosition, targetOrientation));
            return;
        }
        Vector3 oldPosition = cockpitBase.position;
        Quaternion oldRotation = cockpitBase.rotation;
        var bodies = cockpitBase.GetComponentsInChildren<Rigidbody>();
        var positions = new Vector3[bodies.Length];
        var rotations = new Quaternion[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            positions[i] = bodies[i].position;
            rotations[i] = bodies[i].rotation;
        }
        // 이동용 손잡이가 없는 기존 콘솔도 같은 상대 자세를 보존합니다.
        cockpitBase.position = targetPosition;

        // 고개를 숙여도 받침대의 원래 기울기를 유지합니다.
        cockpitBase.rotation = targetOrientation;
        Quaternion delta = cockpitBase.rotation * Quaternion.Inverse(oldRotation);
        for (int i = 0; i < bodies.Length; i++)
        {
            bodies[i].position = targetPosition + delta * (positions[i] - oldPosition);
            bodies[i].rotation = delta * rotations[i];
            if (!bodies[i].isKinematic)
            {
                bodies[i].linearVelocity = Vector3.zero;
                bodies[i].angularVelocity = Vector3.zero;
            }
        }
        Physics.SyncTransforms();
    }
}
