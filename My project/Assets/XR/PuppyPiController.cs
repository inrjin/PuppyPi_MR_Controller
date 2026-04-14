using UnityEngine;

public class PuppyPiController : MonoBehaviour
{
    [ContextMenu("전진 테스트!")]
    public void MoveForward()
    {
        Debug.Log("명령: 전진 (W)");
    }

    [ContextMenu("후진 테스트!")]
    public void MoveBackward()
    {
        Debug.Log("명령: 후진 (S)");
    }

    [ContextMenu("좌회전 테스트!")]
    public void TurnLeft()
    {
        Debug.Log("명령: 좌회전 (A)");
    }

    [ContextMenu("우회전 테스트!")]
    public void TurnRight()
    {
        Debug.Log("명령: 우회전 (D)");
    }

    [ContextMenu("정지 테스트!")]
    public void Stop()
    {
        Debug.Log("명령: 정지! (Space)");
    }
}