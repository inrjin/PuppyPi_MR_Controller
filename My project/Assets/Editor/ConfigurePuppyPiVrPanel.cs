using PuppyPi;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ConfigurePuppyPiVrPanel
{
    [MenuItem("PuppyPi/Configure VR control panel")]
    public static void Run()
    {
        var controller = Object.FindFirstObjectByType<PuppyPiRobotController>();
        if (controller == null)
        {
            Debug.LogError("Add the PuppyPi robot controller to the cockpit scene first.");
            return;
        }
        Ensure(controller);
    }

    public static PuppyPiVrPanel Ensure(PuppyPiRobotController controller)
    {
        var panel = Object.FindFirstObjectByType<PuppyPiVrPanel>();
        if (panel == null)
        {
            var host = new GameObject("PuppyPi VR control panel");
            Undo.RegisterCreatedObjectUndo(host, "Create PuppyPi VR panel");
            panel = host.AddComponent<PuppyPiVrPanel>();
            Transform cockpit = controller.GripInput != null ? controller.GripInput.baseFrame : controller.transform;
            host.transform.SetParent(cockpit, false);
            var camera = Camera.main;
            if (camera != null)
            {
                panel.Recenter();
            }
            else
            {
                host.transform.position = cockpit.position + new Vector3(0.48f, 0.7f, -0.25f);
                host.transform.rotation = Quaternion.Euler(0, 35, 0);
            }
        }
        Undo.RecordObject(panel, "Connect PuppyPi VR panel");
        panel.controller = controller;
        Undo.RecordObject(controller, "Connect movable PuppyPi dashboard");
        controller.dashboard = panel;
        EditorUtility.SetDirty(controller);
        EditorUtility.SetDirty(panel);
        EditorSceneManager.MarkSceneDirty(panel.gameObject.scene);
        Selection.activeGameObject = panel.gameObject;
        Debug.Log("PuppyPi VR panel configured. Save the scene to include it in the APK.", panel);
        return panel;
    }
}
