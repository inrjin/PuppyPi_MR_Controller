using System;
using System.Linq;
using Oculus.Interaction;
using Oculus.Interaction.HandGrab;
using PuppyPi;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ConfigureBallGrip
{
    [MenuItem("PuppyPi/Configure BallGrip scene")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        GameObject root = scene.GetRootGameObjects().Single(o => o.name == "Console_BallGrip");
        Transform grip = root.transform.Find("BoxLid");
        foreach (GameObject obj in scene.GetRootGameObjects())
        {
            if (obj.name.Trim() == "Console_StickGrip") obj.SetActive(false);
            foreach (var sender in obj.GetComponentsInChildren<UDP_Joystick_Sender>(true)) sender.enabled = false;
            foreach (var hud in obj.GetComponentsInChildren<JoystickDebugHUD>(true)) hud.enabled = false;
        }
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<HingeJoint>());
        UnityEngine.Object.DestroyImmediate(grip.GetComponent<OneGrabPhysicsJointTransformer>());
        var joint = GetOrAdd<ConfigurableJoint>(grip.gameObject);
        joint.connectedBody = root.GetComponent<Rigidbody>();
        joint.anchor = Vector3.zero;
        joint.autoConfigureConnectedAnchor = false;
        joint.connectedAnchor = Vector3.zero;
        joint.axis = Vector3.right;
        joint.secondaryAxis = Vector3.up;
        joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
        joint.angularXMotion = joint.angularYMotion = ConfigurableJointMotion.Limited;
        joint.angularZMotion = ConfigurableJointMotion.Locked;
        joint.lowAngularXLimit = new SoftJointLimit { limit = -40 };
        joint.highAngularXLimit = new SoftJointLimit { limit = 40 };
        joint.angularYLimit = new SoftJointLimit { limit = 40 };
        joint.rotationDriveMode = RotationDriveMode.XYAndZ;
        joint.angularXDrive = joint.angularYZDrive = new JointDrive { positionSpring = 100, positionDamper = 20, maximumForce = 1000 };
        joint.targetRotation = Quaternion.identity;
        joint.enablePreprocessing = false;
        joint.projectionMode = JointProjectionMode.PositionAndRotation;
        joint.projectionDistance = 0.005f;
        joint.projectionAngle = 1;
        var body = grip.GetComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.solverIterations = 12;
        body.solverVelocityIterations = 12;
        // Installation helpers must not grab the fixed base through the handle's colliders.
        foreach (var interactable in grip.GetComponentsInChildren<HandGrabInteractable>(true))
            if (interactable.Rigidbody == joint.connectedBody) interactable.gameObject.SetActive(false);
        foreach (var interactable in grip.GetComponentsInChildren<GrabInteractable>(true))
            if (interactable.Rigidbody == joint.connectedBody) interactable.gameObject.SetActive(false);
        var input = GetOrAdd<BallGripInput>(root);
        input.baseFrame = root.transform;
        input.grip = grip;
        input.grabbable = grip.GetComponent<Grabbable>();
        var driver = GetOrAdd<BallGripJointDriver>(grip.gameObject);
        driver.input = input;
        input.grabbable.InjectOptionalOneGrabTransformer(driver);
        input.grabbable.InjectOptionalTwoGrabTransformer(null);
        input.grabbable.InjectOptionalKinematicWhileSelected(false);
        input.grabbable.InjectOptionalThrowWhenUnselected(false);
        input.grabbable.MaxGrabPoints = 1;
        var client = GetOrAdd<PuppyPiV2Client>(root);
        client.input = input;
        client.port = 5001;
        client.networkEnabled = false;
        var hudNew = GetOrAdd<BallGripHud>(root);
        hudNew.input = input;
        hudNew.client = client;
        // Summon only the selected BallGrip; preserve its authored -90 degree base tilt.
        foreach (var obj in scene.GetRootGameObjects())
            foreach (var summon in obj.GetComponentsInChildren<SummonCockpit>(true)) summon.cockpitBase = root.transform;
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("PUPPYPI_SCENE_CONFIGURED");
    }

    private static T GetOrAdd<T>(GameObject target) where T : Component
    {
        T component = target.GetComponent<T>();
        return component != null ? component : target.AddComponent<T>();
    }
}
