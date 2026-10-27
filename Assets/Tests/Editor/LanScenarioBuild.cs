using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

public static class LanScenarioBuild
{
    public const string OutputPath = "/tmp/FateTempered-LanBuild/FateTempered.app";

    [MenuItem("Tools/Validation/Build LAN Scenario Player")]
    public static void Build()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Stop Play mode before building the scenario player.");
        string[] scenes =
        {
            NetworkLobbySetup.EntryPath, NetworkLobbySetup.LobbyPath,
            "Assets/Scenes/InGame.unity", "Assets/Scenes/Map.unity"
        };
        foreach (string path in scenes)
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                throw new InvalidOperationException("Scenario scene is missing: " + path);
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath,
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("LAN scenario build failed: " + report.summary.result);
        Debug.Log("LAN_SCENARIO_BUILD_PASSED: " + OutputPath);
    }
}
