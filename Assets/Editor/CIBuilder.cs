using System;
using System.IO;
using UnityEditor;
using UnityEngine;

public class CIBuilder
{
    static string[] args = Environment.GetCommandLineArgs();
    static string buildPath = string.Empty;
    static BuildTarget buildTarget = BuildTarget.NoTarget;
    static string projectName = string.Empty;
    
    // sh 파일에서 실행 
    public static void Build()
    {
        if (Directory.Exists(buildPath))
        {
            Directory.Delete(buildPath, true);  // true = 하위 파일까지 전부 삭제
            Directory.CreateDirectory(buildPath);
        }
        
        for (int i = 0; i < args.Length; i++)
        {
            Debug.Log($"args[{i}] = {args[i]}");
            
            if (args[i] == "-customBuildPath")
                buildPath = args[i + 1];
            else if (args[i] == "-customBuildTarget")
                buildTarget = Enum.Parse<BuildTarget>(args[i + 1]);
            else if (args[i] == "-productName")
                projectName = args[i + 1];
        }
        
        Debug.Log($"buildPath: {buildPath}");
        Debug.Log($"GetBuildPathName: {buildTarget}");
        Debug.Log($"projectName: {projectName}");

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = EditorBuildSettingsScene.GetActiveSceneList(EditorBuildSettings.scenes),
            locationPathName =  GetBuildPathName(),
            target = buildTarget,
            options = BuildOptions.None
        };

        if (buildTarget == BuildTarget.Android)
        {
            SetAndroidBuild();
        }
        
        BuildPipeline.BuildPlayer(options);
    }

    private static void SetAndroidBuild()
    {
        EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
    }

    private static string GetBuildPathName()
    {
        switch (buildTarget)
        {
            case BuildTarget.Android:
                return Path.Combine(buildPath, $"{projectName}.apk"); ;
            case BuildTarget.iOS:
                return Path.Combine(buildPath);
            case BuildTarget.StandaloneWindows64: 
                return Path.Combine(buildPath, $"{projectName}.exe");
            default:
                throw new Exception($"$지원하지 않는 플랫폼 : {buildTarget}");
        }
    }

}
