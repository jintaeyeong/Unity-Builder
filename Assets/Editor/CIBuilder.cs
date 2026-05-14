using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class CIBuilder
{
    static string[] args = Environment.GetCommandLineArgs();

    public static void Build()
    {
        string buildPath = string.Empty;
        BuildTarget buildTarget = BuildTarget.NoTarget;
        string projectName = string.Empty;
        bool cleanBuild = false;
        bool isDevelopment = false;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-customBuildPath") buildPath = args[i + 1];
            else if (args[i] == "-customBuildTarget") buildTarget = Enum.Parse<BuildTarget>(args[i + 1]);
            else if (args[i] == "-productName") projectName = args[i + 1];
            else if (args[i] == "-cleanBuild") cleanBuild = true;
            else if (args[i] == "-development") isDevelopment = true;
        }

        Debug.Log($"buildPath: {buildPath}");
        Debug.Log($"buildTarget: {buildTarget}");
        Debug.Log($"projectName: {projectName}");
        Debug.Log($"cleanBuild: {cleanBuild}, isDevelopment: {isDevelopment}");

        if (cleanBuild && Directory.Exists(buildPath))
            Directory.Delete(buildPath, true);
        Directory.CreateDirectory(buildPath);

        switch (buildTarget)
        {
            case BuildTarget.Android: SetAndroidBuild(isDevelopment); break;
            case BuildTarget.iOS: SetIOSBuild(isDevelopment); break;
        }

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = EditorBuildSettingsScene.GetActiveSceneList(EditorBuildSettings.scenes),
            locationPathName = GetBuildPathName(buildTarget, buildPath, projectName),
            target = buildTarget,
            options = isDevelopment ? BuildOptions.Development : BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        Debug.Log($"빌드 결과: {report.summary.result}, 소요시간: {report.summary.totalTime.TotalSeconds:F1}초");

        if (report.summary.result == BuildResult.Failed)
            EditorApplication.Exit(1);
    }

    private static void SetAndroidBuild(bool isDevelopment)
    {
        EditorUserBuildSettings.androidBuildSystem = AndroidBuildSystem.Gradle;
        EditorUserBuildSettings.exportAsGoogleAndroidProject = false;

        // 개발 빌드: Mono (빌드 빠름), 릴리즈: IL2CPP (런타임 빠름·크기 작음)
        ScriptingImplementation backend = isDevelopment
            ? ScriptingImplementation.Mono2x
            : ScriptingImplementation.IL2CPP;
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, backend);
        PlayerSettings.stripEngineCode = !isDevelopment;
    }

    private static void SetIOSBuild(bool isDevelopment)
    {
        // ARM64만 빌드 (32bit 제거 → 빌드 시간 단축)
        PlayerSettings.SetArchitecture(NamedBuildTarget.iOS, 1);

        // OptimizeSize: 빌드 빠름 (개발/QA), OptimizeSpeed: 빌드 느림 런타임 빠름 (릴리즈)
        Il2CppCodeGeneration codeGen = isDevelopment
            ? Il2CppCodeGeneration.OptimizeSize
            : Il2CppCodeGeneration.OptimizeSpeed;
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.iOS, codeGen);
 
        PlayerSettings.stripEngineCode = !isDevelopment;
    }

    private static string GetBuildPathName(BuildTarget buildTarget, string buildPath, string projectName)
    {
        switch (buildTarget)
        {
            case BuildTarget.Android: return Path.Combine(buildPath, $"{projectName}.apk");
            case BuildTarget.iOS: return buildPath;
            case BuildTarget.StandaloneWindows64: return Path.Combine(buildPath, $"{projectName}.exe");
            default: throw new Exception($"지원하지 않는 플랫폼: {buildTarget}");
        }
    }
}
