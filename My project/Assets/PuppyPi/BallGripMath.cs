using UnityEngine;

namespace PuppyPi
{
    public static class BallGripMath
    {
        // In the authored base frame +Z is up, -Y is forward, -X is left.
        public static Vector2 TiltDegrees(Quaternion relativeToNeutral)
        {
            Vector3 direction = relativeToNeutral * Vector3.forward;
            return new Vector2(Mathf.Atan2(-direction.y, direction.z),
                Mathf.Atan2(-direction.x, direction.z)) * Mathf.Rad2Deg;
        }

        public static Quaternion Rotation(Vector2 degrees)
        {
            Vector3 direction = new Vector3(-Mathf.Tan(degrees.y * Mathf.Deg2Rad),
                -Mathf.Tan(degrees.x * Mathf.Deg2Rad), 1f).normalized;
            return Quaternion.FromToRotation(Vector3.forward, direction);
        }

        public static float Normalize(float degrees, float deadzone, float maximum, float sensitivity)
        {
            if (float.IsNaN(degrees) || float.IsInfinity(degrees)) return 0;
            float dz = Mathf.Clamp(deadzone, 0, maximum - 0.01f);
            float value = Mathf.Max(0, Mathf.Abs(degrees) - dz) / Mathf.Max(0.01f, maximum - dz);
            return Mathf.Sign(degrees) * Mathf.Clamp01(value * Mathf.Max(0, sensitivity));
        }
    }
}
