using UnityEngine;

namespace PuppyPi.PatternExamples
{
    // 공통 인터페이스: 원본과 데코레이터를 같은 방식으로 사용합니다.
    // Vector2.x는 관절 X 각도, Vector2.y는 관절 Z 각도입니다.
    public interface IJoystickInput
    {
        Vector2 ReadAngles();
    }

    // 원본 객체: 학습용 고정 입력. 실제 조이스틱이나 UDP는 사용하지 않습니다.
    public sealed class FixedJoystickInput : IJoystickInput
    {
        private readonly Vector2 angles;

        public FixedJoystickInput(Vector2 angles)
        {
            this.angles = angles;
        }

        public Vector2 ReadAngles() => angles;
    }

    // 데코레이터의 핵심: 같은 인터페이스를 구현하고, 다른 입력 객체를 감쌉니다.
    public abstract class JoystickInputDecorator : IJoystickInput
    {
        protected readonly IJoystickInput Inner;

        protected JoystickInputDecorator(IJoystickInput inner)
        {
            Inner = inner;
        }

        public abstract Vector2 ReadAngles();
    }

    // 기능 추가 1: 작은 기울기를 0으로 처리합니다.
    public sealed class DeadzoneInput : JoystickInputDecorator
    {
        private readonly float threshold;

        public DeadzoneInput(IJoystickInput inner, float threshold) : base(inner)
        {
            this.threshold = Mathf.Abs(threshold);
        }

        public override Vector2 ReadAngles()
        {
            Vector2 angles = Inner.ReadAngles();
            if (Mathf.Abs(angles.x) < threshold) angles.x = 0f;
            if (Mathf.Abs(angles.y) < threshold) angles.y = 0f;
            return angles;
        }
    }

    // 기능 추가 2: 전진/후진에 사용하는 Z 각도의 부호를 반전합니다.
    public sealed class ReverseForwardInput : JoystickInputDecorator
    {
        public ReverseForwardInput(IJoystickInput inner) : base(inner) { }

        public override Vector2 ReadAngles()
        {
            Vector2 angles = Inner.ReadAngles();
            angles.y = -angles.y;
            return angles;
        }
    }

    // 빈 GameObject에 추가한 뒤 컴포넌트 메뉴에서 실행하세요.
    public sealed class JoystickDecoratorExample : MonoBehaviour
    {
        [ContextMenu("데코레이터 예제 실행")]
        public void RunExample()
        {
            IJoystickInput original = new FixedJoystickInput(new Vector2(3f, 20f));
            IJoystickInput deadzone = new DeadzoneInput(original, 5f);
            IJoystickInput reversed = new ReverseForwardInput(deadzone);

            Print("원본", original);             // X:3, Z:20
            Print("데드존 추가", deadzone);      // X:0, Z:20
            Print("방향 반전 추가", reversed);   // X:0, Z:-20
            Print("원본 다시 확인", original);   // X:3, Z:20 (원본은 그대로)
        }

        private static void Print(string label, IJoystickInput input)
        {
            Vector2 angles = input.ReadAngles();
            Debug.Log($"[{label}] X:{angles.x:F1}, Z:{angles.y:F1}");
        }
    }
}
