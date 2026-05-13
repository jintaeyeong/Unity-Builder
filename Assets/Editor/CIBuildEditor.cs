using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class CIBuildEditor : EditorWindow
{
    private string loadPath = string.Empty;
    private string buildPath = string.Empty;
    private BuildTarget buildTarget = BuildTarget.Android;
    private string productName = string.Empty;
    private string autherName = string.Empty;
    private bool cleanBuild = false;
    private bool isDevelopment = false;
    private string branchName = string.Empty;
    private string[] remoteBranches = Array.Empty<string>();
    private int branchIndex = 0;
    private string loadPathError = string.Empty;
    private string gitUsername = string.Empty;
    private string gitToken = string.Empty;

    private Process buildProcess;
    private double buildStartTime;

    private readonly ConcurrentQueue<(string msg, bool isError)> pendingLogs = new();
    private readonly List<(string msg, bool isError)> buildLogs = new();
    private Vector2 settingsScroll;
    private Vector2 logScroll;
    private bool autoScroll = true;
    private float lastMaxScrollY = 0;

    private static GUIStyle helpStyle;
    private static GUIStyle logNormalStyle;
    private static GUIStyle logErrorStyle;

    [MenuItem("Tool/Unity_CI")]
    public static void SetWindow()
    {
        var window = GetWindow<CIBuildEditor>("Unity Build CI Tool");
        window.minSize = new Vector2(400, 500);
        window.maxSize = new Vector2(1000, 1200);
    }

    private void OnEnable()
    {
        loadPath = EditorPrefs.GetString("CIBuildEditor_LoadPath", string.Empty);
        buildPath = EditorPrefs.GetString("CIBuildEditor_BuildPath", string.Empty);
        buildTarget = (BuildTarget)EditorPrefs.GetInt("CIBuildEditor_BuildTarget", (int)BuildTarget.Android);
        productName = EditorPrefs.GetString("CIBuildEditor_ProductName", string.Empty);
        autherName = EditorPrefs.GetString("CIBuildEditor_AutherName", string.Empty);
        cleanBuild = EditorPrefs.GetBool("CIBuildEditor_CleanBuild", false);
        isDevelopment = EditorPrefs.GetBool("CIBuildEditor_IsDevelopment", false);
        branchName = EditorPrefs.GetString("CIBuildEditor_BranchName", string.Empty);
        gitUsername = EditorPrefs.GetString("CIBuildEditor_GitUsername", string.Empty);
        gitToken = EditorPrefs.GetString("CIBuildEditor_GitToken", string.Empty);

        if (!string.IsNullOrEmpty(loadPath))
        {
            ReadProjectName();
            FetchBranches();
        }
    }

    private void OnDisable()
    {
        EditorPrefs.SetString("CIBuildEditor_LoadPath", loadPath);
        EditorPrefs.SetString("CIBuildEditor_BuildPath", buildPath);
        EditorPrefs.SetInt("CIBuildEditor_BuildTarget", (int)buildTarget);
        EditorPrefs.SetString("CIBuildEditor_ProductName", productName);
        EditorPrefs.SetString("CIBuildEditor_AutherName", autherName);
        EditorPrefs.SetBool("CIBuildEditor_CleanBuild", cleanBuild);
        EditorPrefs.SetBool("CIBuildEditor_IsDevelopment", isDevelopment);
        EditorPrefs.SetString("CIBuildEditor_BranchName", branchName);
        EditorPrefs.SetString("CIBuildEditor_GitUsername", gitUsername);
        EditorPrefs.SetString("CIBuildEditor_GitToken", gitToken);
    }

    private void OnGUI()
    {
        InitStyles();

        settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll, GUILayout.ExpandHeight(false));

        EditorGUILayout.LabelField(
            "Load Path는 빌드 하고싶은 프로젝트의 경로 \n" +
            "Build Path는 빌드 후 생성 된 폴더의 최상위 폴더입니다\n" +
            "설정된 폴더\\projectName\\platformTarget_생성자\\파일명 으로 생성됩니다",
            helpStyle,
            GUILayout.Height(100)
        );

        EditorGUILayout.Space(30);

        EditorGUILayout.LabelField("경로 설정", EditorStyles.boldLabel);
        EditorGUILayout.Separator();
        DrawLoadPath();
        DrawBuildPath();
        DrawGitAccount();

        EditorGUILayout.Space(30);

        EditorGUILayout.LabelField("빌드 설정", EditorStyles.boldLabel);
        DrawBuildSettings();

        EditorGUILayout.Space(10);

        DrawBuildRow();
        DrawBuildStatus();

        EditorGUILayout.EndScrollView();

        DrawLogPanel();
    }

    private void InitStyles()
    {
        if (helpStyle == null)
            helpStyle = new GUIStyle(EditorStyles.helpBox) { fontSize = 13, richText = true };

        if (logNormalStyle == null)
        {
            logNormalStyle = new GUIStyle(EditorStyles.label) { wordWrap = true, richText = false };
            logErrorStyle = new GUIStyle(logNormalStyle);
            logErrorStyle.normal.textColor = new Color(1f, 0.4f, 0.4f);
        }
    }

    private void DrawBuildRow()
    {
        EditorGUILayout.BeginHorizontal();
        cleanBuild = EditorGUILayout.ToggleLeft("클린 빌드", cleanBuild, GUILayout.Width(80));
        isDevelopment = EditorGUILayout.ToggleLeft("개발 빌드", isDevelopment, GUILayout.Width(80));
        GUILayout.FlexibleSpace();
        bool canBuild = !string.IsNullOrEmpty(loadPath) && !string.IsNullOrEmpty(buildPath) && buildProcess == null;
        GUI.enabled = canBuild;
        if (GUILayout.Button("Build", GUILayout.Height(25), GUILayout.Width(100)))
            OnClickBuild();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBuildStatus()
    {
        if (buildProcess == null) return;

        EditorGUILayout.Space(10);
        double elapsed = EditorApplication.timeSinceStartup - buildStartTime;
        int min = (int)elapsed / 60;
        int sec = (int)elapsed % 60;
        EditorGUILayout.HelpBox($"빌드 중...  {min:D2}:{sec:D2}", MessageType.Info);
    }

    private void DrawLogPanel()
    {
        if (buildLogs.Count == 0) return;

        EditorGUILayout.Space(10);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("빌드 로그", EditorStyles.boldLabel);
        if (GUILayout.Button("지우기", GUILayout.Width(60)))
            buildLogs.Clear();
        EditorGUILayout.EndHorizontal();

        // 코드가 float.MaxValue로 설정한 프레임인지 구분
        bool codeScrolled = logScroll.y == float.MaxValue;
        float beforeY = logScroll.y;

        logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.ExpandHeight(true));
        foreach (var (msg, isError) in buildLogs)
            EditorGUILayout.LabelField(msg, isError ? logErrorStyle : logNormalStyle);
        EditorGUILayout.EndScrollView();

        if (codeScrolled)
        {
            // BeginScrollView가 MaxValue를 클램프한 결과 = 실제 최하단 Y
            lastMaxScrollY = logScroll.y;
        }
        else
        {
            // 사용자가 위로 스크롤하면 자동 스크롤 해제
            if (logScroll.y < beforeY - 1f)
                autoScroll = false;

            // 사용자가 다시 최하단으로 돌아오면 자동 스크롤 재활성화
            if (logScroll.y >= lastMaxScrollY - 5f)
                autoScroll = true;
        }
    }

    private void DrawLoadPath()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("Load Path", loadPath);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            EditorGUILayout.EndHorizontal();
            string selected = EditorUtility.OpenFolderPanel("폴더 선택", loadPath, "");
            if (!string.IsNullOrEmpty(selected))
            {
                if (!File.Exists(Path.Combine(selected, "ProjectSettings/ProjectSettings.asset")))
                {
                    loadPathError = "선택한 폴더가 Unity 프로젝트가 아닙니다.";
                }
                else
                {
                    loadPathError = string.Empty;
                    loadPath = selected;
                    ReadProjectName();
                    FetchBranches();
                }
            }
            GUIUtility.ExitGUI();
        }
        EditorGUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(loadPathError))
            EditorGUILayout.HelpBox(loadPathError, MessageType.Error);
    }

    private void DrawGitAccount()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("Git 계정", EditorStyles.boldLabel);
        gitUsername = EditorGUILayout.TextField("사용자명", gitUsername);
        gitToken = EditorGUILayout.PasswordField("토큰 / 비밀번호", gitToken);
    }

    private void DrawBuildPath()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("Build Path", buildPath);
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            EditorGUILayout.EndHorizontal();
            string selected = EditorUtility.OpenFolderPanel("폴더 선택", buildPath, "");
            if (!string.IsNullOrEmpty(selected))
                buildPath = selected;
            GUIUtility.ExitGUI();
        }
        EditorGUILayout.EndHorizontal();
    }

    private void DrawBuildSettings()
    {
        EditorGUILayout.BeginHorizontal();
        if (remoteBranches.Length > 0)
        {
            int newIdx = EditorGUILayout.Popup("Git Branch", branchIndex, remoteBranches);
            if (newIdx != branchIndex)
            {
                branchIndex = newIdx;
                branchName = remoteBranches[branchIndex];
            }
        }
        else
        {
            EditorGUILayout.LabelField("Git Branch", "Load Path를 먼저 설정하거나 ↻ 클릭");
        }
        if (GUILayout.Button("↻", GUILayout.Width(30)))
            FetchBranches();
        EditorGUILayout.EndHorizontal();

        buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", buildTarget);
        autherName = EditorGUILayout.TextField("빌드 생성자", autherName);
        productName = EditorGUILayout.TextField("빌드 파일 이름", productName);
    }

    private void ReadProjectName()
    {
        string settingPath = Path.Combine(loadPath, "ProjectSettings/ProjectSettings.asset");
        if (!File.Exists(settingPath))
        {
            loadPathError = "ProjectSettings.asset 파일을 찾을 수 없습니다.";
            return;
        }

        foreach (var line in File.ReadAllLines(settingPath))
        {
            if (line.Contains("productName"))
            {
                productName = line.Split(':')[1].Trim();
                break;
            }
        }
    }

    private void FetchBranches()
    {
        if (string.IsNullOrEmpty(loadPath) || !Directory.Exists(Path.Combine(loadPath, ".git")))
        {
            remoteBranches = Array.Empty<string>();
            return;
        }

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{loadPath}\" branch -r",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            }
        };

        p.Start();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();

        remoteBranches = output.Split('\n')
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrEmpty(b) && !b.Contains("HEAD"))
            .Select(b => b.StartsWith("origin/") ? b["origin/".Length..] : b)
            .ToArray();

        branchIndex = Array.IndexOf(remoteBranches, branchName);
        if (branchIndex < 0) branchIndex = 0;
        if (remoteBranches.Length > 0)
            branchName = remoteBranches[branchIndex];

        Repaint();
    }

    private void OnClickBuild()
    {
        buildLogs.Clear();
        autoScroll = true;

        string shPath = Path.GetFullPath(Path.Combine(Application.dataPath, "../AutoBuild.sh"));
        string ciBuilderPath = Path.Combine(Application.dataPath, "Editor/CIBuilder.cs");
        string cleanFlag = cleanBuild.ToString().ToLower();
        string devFlag = isDevelopment.ToString().ToLower();

#if UNITY_EDITOR_WIN
        string fileName = "cmd.exe";
        string arguments = $"/c {shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{ciBuilderPath}\" \"{productName}\" \"{autherName}\" \"{cleanFlag}\" \"{devFlag}\" \"{branchName}\"";
#elif UNITY_EDITOR_OSX
        string fileName = "/bin/bash";
        string arguments = $"{shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{ciBuilderPath}\" \"{productName}\" \"{autherName}\" \"{cleanFlag}\" \"{devFlag}\" \"{branchName}\"";
#endif

        var startInfo = new ProcessStartInfo
        {
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(gitUsername))
            startInfo.EnvironmentVariables["GIT_USER"] = gitUsername;
        if (!string.IsNullOrEmpty(gitToken))
            startInfo.EnvironmentVariables["GIT_TOKEN"] = gitToken;

        var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                pendingLogs.Enqueue((e.Data, false));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                pendingLogs.Enqueue((e.Data, true));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        buildStartTime = EditorApplication.timeSinceStartup;
        buildProcess = process;
        EditorApplication.update += OnEditorUpdate;
    }

    private void OnEditorUpdate()
    {
        bool hasNew = false;
        while (pendingLogs.TryDequeue(out var entry))
        {
            if (entry.isError) Debug.LogError(entry.msg);
            else Debug.Log(entry.msg);

            buildLogs.Add(entry);
            hasNew = true;
        }

        if (hasNew && autoScroll)
            logScroll.y = float.MaxValue;

        if (buildProcess != null && buildProcess.HasExited)
        {
            double elapsed = EditorApplication.timeSinceStartup - buildStartTime;
            int min = (int)elapsed / 60;
            int sec = (int)elapsed % 60;

            bool success = buildProcess.ExitCode == 0;
            string summary = $"빌드 {(success ? "성공" : "실패")} - 총 {min:D2}:{sec:D2}";
            buildLogs.Add((summary, !success));

            if (success) Debug.Log(summary);
            else Debug.LogError(summary);

            buildProcess = null;
            EditorApplication.update -= OnEditorUpdate;
        }

        Repaint();
    }
}
