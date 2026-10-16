using Oculus.Interaction;
using UnityEngine;

namespace PuppyPi
{
    // Drives the existing pivot joint. No second fixed joint competes with it.
    [RequireComponent(typeof(ConfigurableJoint))]
    public sealed class BallGripJointDriver : MonoBehaviour, ITransformer
    {
        public BallGripInput input;
        public float spring = 100;
        public float damper = 20;
        public float maximumForce = 1000;
        [Min(0.02f)] public float minimumLeverLength = 0.1f;
        private ConfigurableJoint _joint;
        private IGrabbable _grabbable;
        private Vector3 _grabStart;
        private Vector3 _directionStart;
        private float _leverLength;
        private bool _selected;

        public void Initialize(IGrabbable grabbable)
        {
            _grabbable = grabbable;
            _joint = GetComponent<ConfigurableJoint>();
            var body = GetComponent<Rigidbody>();
            body.solverIterations = 12;
            body.solverVelocityIterations = 12;
            _joint.rotationDriveMode = RotationDriveMode.XYAndZ;
            JointDrive drive = new JointDrive { positionSpring = spring, positionDamper = damper, maximumForce = maximumForce };
            _joint.angularXDrive = drive;
            _joint.angularYZDrive = drive;
            _joint.targetRotation = Quaternion.identity;
        }

        public void BeginTransform()
        {
            if (input == null || _grabbable.GrabPoints.Count == 0) return;
            _selected = true;
            _grabStart = input.baseFrame.InverseTransformPoint(_grabbable.GrabPoints[0].position);
            Vector3 pivot = input.baseFrame.InverseTransformPoint(transform.TransformPoint(_joint.anchor));
            _leverLength = Mathf.Max(minimumLeverLength, (_grabStart - pivot).magnitude);
            _directionStart = Quaternion.Inverse(input.Neutral) *
                input.baseFrame.InverseTransformDirection(transform.forward);
        }

        public void UpdateTransform()
        {
            if (!_selected || input == null || !input.ControlActive || _grabbable.GrabPoints.Count == 0) return;
            Vector3 current = input.baseFrame.InverseTransformPoint(_grabbable.GrabPoints[0].position);
            Vector3 direction = _directionStart + Quaternion.Inverse(input.Neutral) * (current - _grabStart) / _leverLength;
            direction.z = Mathf.Max(0.01f, direction.z);
            Vector2 tilt = BallGripMath.TiltDegrees(Quaternion.FromToRotation(Vector3.forward, direction.normalized));
            tilt.x = Mathf.Clamp(tilt.x, -input.maxTiltDegrees, input.maxTiltDegrees);
            tilt.y = Mathf.Clamp(tilt.y, -input.maxTiltDegrees, input.maxTiltDegrees);
            // Joint axes are the authored local X/Y; PhysX target rotation uses the inverse.
            _joint.targetRotation = Quaternion.Inverse(BallGripMath.Rotation(tilt));
        }

        private void FixedUpdate()
        {
            if (_joint != null && input != null)
            {
                float limit = Mathf.Clamp(input.maxTiltDegrees, 5, 60);
                if (!Mathf.Approximately(_joint.highAngularXLimit.limit, limit))
                {
                    _joint.lowAngularXLimit = new SoftJointLimit { limit = -limit };
                    _joint.highAngularXLimit = _joint.angularYLimit = new SoftJointLimit { limit = limit };
                }
            }
#if UNITY_EDITOR
            if (_joint != null && input != null && input.editorPreview && input.ControlActive)
            {
                Vector2 tilt = input.previewTilt;
                tilt.x = Mathf.Clamp(tilt.x, -input.maxTiltDegrees, input.maxTiltDegrees);
                tilt.y = Mathf.Clamp(tilt.y, -input.maxTiltDegrees, input.maxTiltDegrees);
                _joint.targetRotation = Quaternion.Inverse(BallGripMath.Rotation(tilt));
                return;
            }
#endif
            if (_joint != null && (!_selected || input == null || !input.ControlActive))
                _joint.targetRotation = Quaternion.identity;
        }

        public void EndTransform()
        {
            _selected = false;
            if (_joint != null) _joint.targetRotation = Quaternion.identity;
        }
    }
}
