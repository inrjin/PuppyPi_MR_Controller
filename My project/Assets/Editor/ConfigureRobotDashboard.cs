using System.Linq;
using PuppyPi;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ConfigureRobotDashboard
{
    [MenuItem("PuppyPi/Configure complete VR dashboard")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene("Assets/Scenes/SampleScene.unity");
        var client = scene.GetRootGameObjects()
            .SelectMany(root => root.GetComponentsInChildren<PuppyPiV2Client>(true))
            .Single(component => component.isActiveAndEnabled);
        var root = client.gameObject;
        var controller = GetOrAdd<PuppyPiRobotController>(root);
        controller.driveClient = client;
        controller.gripInput = client.input;
        controller.connection = GetOrAdd<PuppyPiConnection>(root);
        controller.connection.client = client;
        controller.rosBridge = GetOrAdd<RobotRosBridge>(root);
        controller.cameraStream = GetOrAdd<PuppyPiCameraStream>(root);
        controller.ResolveComponents();
        ConfigurePuppyPiVrPanel.Ensure(controller);
        BuildQuest.ConfigureNetworkAccess();
        EditorUtility.SetDirty(controller);
        EditorUtility.SetDirty(controller.connection);
        if (controller.cockpitPlacement != null) EditorUtility.SetDirty(controller.cockpitPlacement);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("PUPPYPI_VR_DASHBOARD_CONFIGURED");
    }

    private static T GetOrAdd<T>(GameObject target) where T : Component
    {
        var component = target.GetComponent<T>();
        return component != null ? component : target.AddComponent<T>();
    }
}
