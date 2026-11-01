using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;

public static class BuildQuest
{
    [MenuItem("PuppyPi/Build Quest development APK")]
    public static void Build()
    {
        ConfigureNetworkAccess();
        // Switch platforms before calling this method so script recompilation can finish.
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            throw new BuildFailedException("Select Android in File > Build Profiles first.");

        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        if (xr == null || xr.Manager == null)
            throw new BuildFailedException("Android XR settings are missing.");
        if (xr.Manager.activeLoaders.Count == 0 &&
            !XRPackageMetadataStore.AssignLoader(xr.Manager, "Unity.XR.Oculus.OculusLoader", BuildTargetGroup.Android))
            throw new BuildFailedException("Could not enable the installed Oculus XR loader for Android.");

        xr.InitManagerOnStart = true;
        EditorUtility.SetDirty(xr);
        EditorUtility.SetDirty(xr.Manager);

        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        if ((int)PlayerSettings.Android.minSdkVersion < 32)
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel32;
        PlayerSettings.Android.forceInternetPermission = true;
        PlayerSettings.Android.useAPKExpansionFiles = false;
        var appId = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
        if (string.IsNullOrEmpty(appId) || appId.StartsWith("com.UnityTechnologies."))
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.inrjin.puppypimr");

        EditorUserBuildSettings.buildAppBundle = false;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
        EditorUserBuildSettings.development = true;

        var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(scene => scene.path).ToArray();
        if (scenes.Length == 0)
            throw new BuildFailedException("Add the cockpit scene to the build scene list first.");

        AssetDatabase.SaveAssets();
        string output = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.local/quest/PuppyPiQuest.apk"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            target = BuildTarget.Android,
            locationPathName = output,
            options = BuildOptions.Development | BuildOptions.CompressWithLz4
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException($"Quest APK build failed: {report.summary.result}, {report.summary.totalErrors} errors.");

        Debug.Log($"PUPPYPI_QUEST_BUILD_SUCCEEDED: {output} ({report.summary.totalSize} bytes)");
    }

    public static void ConfigureNetworkAccess()
    {
        // The robot exposes its camera and ROS bridge directly on the local LAN.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
        var config = OVRProjectConfig.CachedProjectConfig;
        config.enableNSCConfig = true;
        config.securityXmlPath = "Assets/PuppyPi/AndroidNetworkSecurity.xml";
        EditorUtility.SetDirty(config);
    }
}
