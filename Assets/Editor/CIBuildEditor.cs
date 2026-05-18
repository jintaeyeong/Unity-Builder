using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class CIBuildEditor : EditorWindow
{
    #region Constants

    private const string PrefLoadPath      = "CIBuildEditor_LoadPath";
    private const string PrefBuildPath     = "CIBuildEditor_BuildPath";
    private const string PrefBuildTarget   = "CIBuildEditor_BuildTarget";
    private const string PrefProductName   = "CIBuildEditor_ProductName";
    private const string PrefAutherName    = "CIBuildEditor_AutherName";
    private const string PrefCleanBuild    = "CIBuildEditor_CleanBuild";
    private const string PrefIsDevelopment = "CIBuildEditor_IsDevelopment";
    private const string PrefBranchName    = "CIBuildEditor_BranchName";
    private const string PrefSSHHost       = "CIBuildEditor_SSHHost";
    private const string PrefSSHUser       = "CIBuildEditor_SSHUser";
    private const string PrefRemoteLoadPath = "CIBuildEditor_RemoteLoadPath";
    private const string PrefBuildMode     = "CIBuildEditor_BuildMode";
    private const string PrefGithubUser  = "CIBuildEditor_GithubUser";
    private const string PrefGithubToken = "CIBuildEditor_GithubToken";

    // macOS TCC는 실제 컴파일된 바이너리일 때만 .app 번들 FDA를 인식함
    // 쉘스크립트(#!/bin/bash)는 실제 프로세스가 /bin/bash로 뜨기 때문에 FDA가 적용 안 됨
    // → C 소스를 Mac Mini에서 cc로 컴파일해 진짜 바이너리로 만듦
    // system()은 /bin/sh -c 경유로 실행되어 FDA 상속 체인이 끊김
    // fork+execvp로 bash를 직접 실행하면 ci_worker의 FDA가 그대로 상속됨
    private const string CIWorkerCSource =
        "#include <stdlib.h>\n" +
        "#include <stdio.h>\n" +
        "#include <unistd.h>\n" +
        "#include <fcntl.h>\n" +
        "#include <sys/wait.h>\n" +
        "\n" +
        "int main(void) {\n" +
        "    const char *home = getenv(\"HOME\");\n" +
        "    if (!home) home = \"/tmp\";\n" +
        "    char req[512], run[512], log[512], done[512];\n" +
        "    snprintf(req,  sizeof(req),  \"%s/.ci_request\",   home);\n" +
        "    snprintf(run,  sizeof(run),  \"%s/.ci_running\",   home);\n" +
        "    snprintf(log,  sizeof(log),  \"%s/.ci_build.log\", home);\n" +
        "    snprintf(done, sizeof(done), \"%s/.ci_done\",      home);\n" +
        "    while (1) {\n" +
        "        if (access(req, F_OK) == 0) {\n" +
        "            rename(req, run);\n" +
        "            remove(done); remove(log);\n" +
        "            pid_t pid = fork();\n" +
        "            if (pid == 0) {\n" +
        "                int fd = open(log, O_WRONLY|O_CREAT|O_TRUNC, 0644);\n" +
        "                if (fd >= 0) { dup2(fd, STDOUT_FILENO); dup2(fd, STDERR_FILENO); close(fd); }\n" +
        "                char *args[] = {\"/bin/bash\", run, NULL};\n" +
        "                execvp(\"/bin/bash\", args);\n" +
        "                _exit(127);\n" +
        "            }\n" +
        "            int status = 0;\n" +
        "            waitpid(pid, &status, 0);\n" +
        "            int code = WIFEXITED(status) ? WEXITSTATUS(status) : 1;\n" +
        "            FILE *f = fopen(done, \"w\");\n" +
        "            if (f) { fprintf(f, \"%d\", code); fclose(f); }\n" +
        "            remove(run);\n" +
        "        }\n" +
        "        usleep(500000);\n" +
        "    }\n" +
        "    return 0;\n" +
        "}\n";

    // MacBook에서 SSH로 접속해 빌드 로그를 스트리밍하는 모니터 스크립트
    // tail -F 대신 wc -l 폴링 방식 사용 — macOS에서 tail -F 백그라운드 방식이 불안정함
    private const string CIMonitorScript =
        "#!/bin/bash\n" +
        "TIMEOUT=30; ELAPSED=0\n" +
        "while [ -f \"$HOME/.ci_request\" ]; do\n" +
        "  sleep 0.5; ELAPSED=$((ELAPSED+1))\n" +
        "  [ $ELAPSED -eq 4 ] && echo '[Monitor] CI 워커 응답 대기 중...'\n" +
        "  [ $ELAPSED -ge $((TIMEOUT*2)) ] && echo '[Monitor] 오류: CI 워커가 응답하지 않습니다. 빌드 서버 설치를 다시 실행하세요.' && exit 1\n" +
        "done\n" +
        "while [ ! -f \"$HOME/.ci_build.log\" ]; do sleep 0.1; done\n" +
        "LAST=0\n" +
        "while true; do\n" +
        "  [ -f \"$HOME/.ci_done\" ] && break\n" +
        "  TOTAL=$(wc -l < \"$HOME/.ci_build.log\" 2>/dev/null | tr -d ' ')\n" +
        "  if [ \"$TOTAL\" -gt \"$LAST\" ]; then\n" +
        "    tail -n \"+$((LAST+1))\" \"$HOME/.ci_build.log\"\n" +
        "    LAST=$TOTAL\n" +
        "  fi\n" +
        "  sleep 0.2\n" +
        "done\n" +
        "TOTAL=$(wc -l < \"$HOME/.ci_build.log\" 2>/dev/null | tr -d ' ')\n" +
        "[ \"$TOTAL\" -gt \"$LAST\" ] && tail -n \"+$((LAST+1))\" \"$HOME/.ci_build.log\"\n" +
        "exit $(cat \"$HOME/.ci_done\" 2>/dev/null || echo 1)\n";

    // ~/.ci_worker.app/Contents/Info.plist — 번들로 등록해 FDA 토글이 System Settings에 나타나게 함
    private const string CIWorkerAppPlist =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
        "<plist version=\"1.0\"><dict>\n" +
        "    <key>CFBundleIdentifier</key><string>com.cibuild.worker</string>\n" +
        "    <key>CFBundleName</key><string>CIBuildWorker</string>\n" +
        "    <key>CFBundleExecutable</key><string>ci_worker</string>\n" +
        "    <key>CFBundleVersion</key><string>1.0</string>\n" +
        "    <key>LSUIElement</key><true/>\n" +
        "</dict></plist>\n";

    #endregion

    #region Fields

    private enum BuildMode { Local, Remote }
    private BuildMode buildMode = BuildMode.Local;

    private string loadPath = string.Empty;
    private string buildPath = string.Empty;
    private BuildTarget buildTarget = BuildTarget.Android;
    private string productName = string.Empty;
    private string autherName = string.Empty;
    private bool cleanBuild = false;
    private bool isDevelopment = false;
    private string branchName = string.Empty;
    private string loadPathError = string.Empty;

    private string[] remoteBranches = Array.Empty<string>();
    private int branchIndex = 0;
    private bool branchFetching = false;
    private string[] pendingBranchResult = null;
    private string pendingBranchError = null;

    private string sshHost = string.Empty;
    private string sshUser = string.Empty;
    private string sshPassword = string.Empty;
    private bool sshSetupRunning = false;
    private bool workerSetupRunning = false;
    private string remoteLoadPath = string.Empty;

    private string githubUser = string.Empty;
    private string githubToken = string.Empty;
    private bool githubSetupRunning = false;

    private Process buildProcess;
    private double buildStartTime;
    private bool buildProcessExited = false;

    private readonly ConcurrentQueue<(string msg, bool isError)> pendingLogs = new();
    private readonly List<(string msg, bool isError)> buildLogs = new();
    private Vector2 settingsScroll;
    private Vector2 logScroll;
    private bool autoScroll = true;
    private double lastRepaintTime = 0;

    private static GUIStyle helpStyle;
    private static GUIStyle logEntryStyle;

    #endregion

    #region Unity Editor Window

    [MenuItem("Tool/Unity_CI")]
    public static void SetWindow()
    {
        var window = GetWindow<CIBuildEditor>("Unity Build CI Tool");
        window.minSize = new Vector2(400, 500);
        window.maxSize = new Vector2(1000, 1200);
    }

    private void OnEnable()
    {
        loadPath       = EditorPrefs.GetString(PrefLoadPath, string.Empty);
        buildPath      = EditorPrefs.GetString(PrefBuildPath, string.Empty);
        buildTarget    = (BuildTarget)EditorPrefs.GetInt(PrefBuildTarget, (int)BuildTarget.Android);
        productName    = EditorPrefs.GetString(PrefProductName, string.Empty);
        autherName     = EditorPrefs.GetString(PrefAutherName, string.Empty);
        cleanBuild     = EditorPrefs.GetBool(PrefCleanBuild, false);
        isDevelopment  = EditorPrefs.GetBool(PrefIsDevelopment, false);
        branchName     = EditorPrefs.GetString(PrefBranchName, string.Empty);
        sshHost        = EditorPrefs.GetString(PrefSSHHost, string.Empty);
        sshUser        = EditorPrefs.GetString(PrefSSHUser, string.Empty);
        remoteLoadPath = EditorPrefs.GetString(PrefRemoteLoadPath, string.Empty);
        buildMode      = (BuildMode)EditorPrefs.GetInt(PrefBuildMode, (int)BuildMode.Local);
        githubUser     = EditorPrefs.GetString(PrefGithubUser, string.Empty);
        githubToken  = EditorPrefs.GetString(PrefGithubToken, string.Empty);

        if (buildMode == BuildMode.Local && !string.IsNullOrEmpty(loadPath))
        {
            ReadProjectName();
            FetchBranches();
        }
        else if (buildMode == BuildMode.Remote && !string.IsNullOrEmpty(remoteLoadPath)
                 && !string.IsNullOrEmpty(sshHost) && !string.IsNullOrEmpty(sshUser))
        {
            FetchBranches();
        }
    }

    private void OnDisable()
    {
        EditorPrefs.SetString(PrefLoadPath, loadPath);
        EditorPrefs.SetString(PrefBuildPath, buildPath);
        EditorPrefs.SetInt(PrefBuildTarget, (int)buildTarget);
        EditorPrefs.SetString(PrefProductName, productName);
        EditorPrefs.SetString(PrefAutherName, autherName);
        EditorPrefs.SetBool(PrefCleanBuild, cleanBuild);
        EditorPrefs.SetBool(PrefIsDevelopment, isDevelopment);
        EditorPrefs.SetString(PrefBranchName, branchName);
        EditorPrefs.SetString(PrefSSHHost, sshHost);
        EditorPrefs.SetString(PrefSSHUser, sshUser);
        EditorPrefs.SetString(PrefRemoteLoadPath, remoteLoadPath);
        EditorPrefs.SetInt(PrefBuildMode, (int)buildMode);
        EditorPrefs.SetString(PrefGithubUser, githubUser);
        EditorPrefs.SetString(PrefGithubToken, githubToken);
    }

    #endregion

    #region GUI

    private void OnGUI()
    {
        InitStyles();

        settingsScroll = EditorGUILayout.BeginScrollView(settingsScroll, GUILayout.ExpandHeight(false));

        EditorGUILayout.Space(10);
        EditorGUILayout.BeginHorizontal();
        if (GUILayout.Toggle(buildMode == BuildMode.Local, "로컬 빌드", EditorStyles.radioButton, GUILayout.Width(100)))
            buildMode = BuildMode.Local;
        if (GUILayout.Toggle(buildMode == BuildMode.Remote, "원격 빌드 (SSH)", EditorStyles.radioButton, GUILayout.Width(130)))
            buildMode = BuildMode.Remote;
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);
        EditorGUILayout.Separator();

        if (buildMode == BuildMode.Local)
        {
            EditorGUILayout.LabelField(
                "Load Path는 빌드할 프로젝트 경로\n" +
                "Build Path는 빌드 결과물이 저장될 폴더\n" +
                "결과: Build Path\\프로젝트명\\플랫폼_생성자\\파일명",
                helpStyle, GUILayout.Height(70));
            EditorGUILayout.Space(10);
            DrawLoadPath();
            DrawBuildPath();
        }
        else
        {
            EditorGUILayout.LabelField(
                "CI Worker (LaunchAgent)를 통해 Mac Mini에서 빌드를 실행합니다\n" +
                "빌드 결과는 Mac Mini의 [원격 Load Path 상위 폴더]/Build 에 저장됩니다",
                helpStyle, GUILayout.Height(50));
            EditorGUILayout.Space(10);
            DrawSSHSettings();
        }

        bool sshReady = buildMode == BuildMode.Local ||
            (!string.IsNullOrEmpty(sshHost) && !string.IsNullOrEmpty(sshUser));

        if (sshReady)
        {
            EditorGUILayout.Space(20);
            EditorGUILayout.LabelField("빌드 설정", EditorStyles.boldLabel);
            DrawBuildSettings();

            EditorGUILayout.Space(10);
            DrawBuildRow();
            DrawBuildStatus();
        }

        EditorGUILayout.EndScrollView();

        DrawLogPanel();
    }

    private void DrawLogPanel()
    {
        const float lineH = 17f;
        float scrollH = Mathf.Clamp(buildLogs.Count * lineH, 20f, 500f);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("빌드 로그", EditorStyles.boldLabel);
        autoScroll = EditorGUILayout.ToggleLeft("자동 스크롤", autoScroll, GUILayout.Width(85));
        if (GUILayout.Button("복사", GUILayout.Width(40), GUILayout.Height(17)))
            GUIUtility.systemCopyBuffer = string.Join("\n", buildLogs.Select(l => l.msg));
        if (GUILayout.Button("지우기", GUILayout.Width(45), GUILayout.Height(17)))
            ClearLogs();
        EditorGUILayout.EndHorizontal();

        logScroll = EditorGUILayout.BeginScrollView(logScroll, GUILayout.Height(scrollH));
        var errorColor = new Color(1f, 0.45f, 0.45f);
        foreach (var (msg, isError) in buildLogs)
        {
            if (isError) GUI.color = errorColor;
            GUILayout.Label(msg, logEntryStyle);
            if (isError) GUI.color = Color.white;
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.EndVertical();
    }

    private void InitStyles()
    {
        helpStyle ??= new GUIStyle(EditorStyles.helpBox) { fontSize = 13, richText = true };
        if (logEntryStyle == null)
            logEntryStyle = new GUIStyle(EditorStyles.label) { wordWrap = false, richText = true, fontSize = 11 };
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

    private void DrawSSHSettings()
    {
        EditorGUILayout.Space(8);
        EditorGUILayout.LabelField("원격 빌드 (SSH)", EditorStyles.boldLabel);
        sshHost = EditorGUILayout.TextField("원격 IP", sshHost);
        sshUser = EditorGUILayout.TextField("원격 사용자명", sshUser);

        if (!string.IsNullOrEmpty(sshHost) && !string.IsNullOrEmpty(sshUser))
        {
            sshPassword = EditorGUILayout.PasswordField("원격 비밀번호 (없으면 빈칸)", sshPassword);
            GUI.enabled = !sshSetupRunning && buildProcess == null;
            if (GUILayout.Button(sshSetupRunning ? "등록 중..." : "SSH 키 등록", GUILayout.Height(22)))
                SetupSSHKey();
            GUI.enabled = true;

            GUI.enabled = !workerSetupRunning && buildProcess == null;
            if (GUILayout.Button(workerSetupRunning ? "설치 중..." : "빌드 서버 설치 (최초 1회)", GUILayout.Height(22)))
                SetupCIWorker();
            GUI.enabled = true;

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("GitHub 계정", EditorStyles.boldLabel);
            githubUser  = EditorGUILayout.TextField("GitHub 사용자명", githubUser);
            githubToken = EditorGUILayout.PasswordField("GitHub 토큰 (PAT)", githubToken);

            if (!string.IsNullOrEmpty(githubUser) && !string.IsNullOrEmpty(githubToken))
            {
                GUI.enabled = !githubSetupRunning && buildProcess == null;
                if (GUILayout.Button(githubSetupRunning ? "설정 중..." : "GitHub 인증 설정", GUILayout.Height(22)))
                    SetupGithubCredentials();
                GUI.enabled = true;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            remoteLoadPath = EditorGUILayout.TextField("원격 Load Path", remoteLoadPath);
            if (GUILayout.Button("✕", GUILayout.Width(24)))
            {
                GUIUtility.keyboardControl = 0;
                remoteLoadPath = string.Empty;
                remoteBranches = Array.Empty<string>();
            }
            if (GUILayout.Button("...", GUILayout.Width(30)))
            {
                EditorGUILayout.EndHorizontal();
                string selected = EditorUtility.OpenFolderPanel("Mac Mini 프로젝트 선택", "", "");
                if (!string.IsNullOrEmpty(selected))
                {
                    GUIUtility.keyboardControl = 0;
                    remoteLoadPath = selected;
                    FetchBranches();
                }
                GUIUtility.ExitGUI();
            }
            EditorGUILayout.EndHorizontal();
        }
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
        GUI.enabled = !branchFetching;
        if (GUILayout.Button(branchFetching ? "..." : "↻", GUILayout.Width(30)))
            FetchBranches();
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();

        buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", buildTarget);
        autherName  = EditorGUILayout.TextField("빌드 생성자", autherName);
        productName = EditorGUILayout.TextField("빌드 파일 이름", productName);
    }

    private void DrawBuildRow()
    {
        EditorGUILayout.BeginHorizontal();
        cleanBuild    = EditorGUILayout.ToggleLeft("클린 빌드", cleanBuild, GUILayout.Width(80));
        isDevelopment = EditorGUILayout.ToggleLeft("개발 빌드", isDevelopment, GUILayout.Width(80));
        GUILayout.FlexibleSpace();
        bool canBuild = buildProcess == null && (
            buildMode == BuildMode.Local
                ? !string.IsNullOrEmpty(loadPath) && !string.IsNullOrEmpty(buildPath)
                : !string.IsNullOrEmpty(sshHost) && !string.IsNullOrEmpty(sshUser) && !string.IsNullOrEmpty(remoteLoadPath));
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

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.HelpBox($"빌드 중...  {min:D2}:{sec:D2}", MessageType.Info);
        if (GUILayout.Button("중지", GUILayout.Height(38), GUILayout.Width(60)))
            StopBuild();
        EditorGUILayout.EndHorizontal();
    }

    #endregion

    #region Helpers

    private void ClearLogs() { buildLogs.Clear(); Repaint(); }

    private void FlushPendingLogs(ref bool runningFlag, Action onComplete = null)
    {
        bool hasNew = false;
        while (pendingLogs.TryDequeue(out var entry))
        {
            if (entry.isError) Debug.LogError(entry.msg);
            else               Debug.Log(entry.msg);
            buildLogs.Add(entry);
            hasNew = true;
        }
        if (hasNew && autoScroll) logScroll.y = float.MaxValue;
        if (!runningFlag) onComplete?.Invoke();
        double now = EditorApplication.timeSinceStartup;
        if (hasNew || !runningFlag || now - lastRepaintTime >= 0.5) { lastRepaintTime = now; Repaint(); }
    }

    private void RunSilent(string file, string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
            }
        };
        p.Start();
        p.WaitForExit(30000);
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

    #endregion

    #region Branch

    private void FetchBranches()
    {
        string targetPath = buildMode == BuildMode.Remote ? remoteLoadPath : loadPath;
        if (string.IsNullOrEmpty(targetPath)) { remoteBranches = Array.Empty<string>(); return; }

        // 로컬 접근 가능하면 (로컬 빌드 or Finder에 마운트된 원격 경로) 바로 git 실행
        if (Directory.Exists(Path.Combine(targetPath, ".git")))
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"-C \"{targetPath}\" -c core.quotepath=false branch -r",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    CreateNoWindow = true,
                }
            };
            p.Start();
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            ApplyBranchOutput(output);
            Repaint();
            return;
        }

        if (buildMode == BuildMode.Local) { remoteBranches = Array.Empty<string>(); return; }

        // Remote 전용: 로컬 접근 불가 시 SSH (async)
        if (string.IsNullOrEmpty(sshHost) || string.IsNullOrEmpty(sshUser)) return;
        if (branchFetching) return;

        branchFetching = true;
        pendingBranchResult = null;
        pendingBranchError = null;
        string host = sshHost, user = sshUser, path = targetPath;

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"ssh {user}@{host} 'git -C \\\"{path}\\\" -c core.quotepath=false branch -r 2>&1'\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        CreateNoWindow = true,
                    }
                };
                p.Start();
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                if (p.ExitCode != 0)
                    pendingBranchError = output.Trim();
                else
                    pendingBranchResult = ParseBranchOutput(output);
            }
            finally { branchFetching = false; }
        });

        EditorApplication.update += OnBranchFetchUpdate;
    }

    private void OnBranchFetchUpdate()
    {
        if (branchFetching) { Repaint(); return; }

        EditorApplication.update -= OnBranchFetchUpdate;

        if (pendingBranchError != null)
            Debug.LogError($"브랜치 조회 실패: {pendingBranchError}");
        else if (pendingBranchResult != null)
            ApplyBranches(pendingBranchResult);

        Repaint();
    }

    private string[] ParseBranchOutput(string output) =>
        output.Split('\n')
            .Select(b => b.Trim())
            .Where(b => !string.IsNullOrEmpty(b) && !b.Contains("HEAD") && !b.Contains("fatal") && !b.Contains("error"))
            .Select(b => b.StartsWith("origin/") ? b["origin/".Length..] : b)
            .ToArray();

    private void ApplyBranches(string[] branches)
    {
        remoteBranches = branches;
        branchIndex = Array.IndexOf(remoteBranches, branchName);
        if (branchIndex < 0) branchIndex = 0;
        if (remoteBranches.Length > 0)
            branchName = remoteBranches[branchIndex];
    }

    private void ApplyBranchOutput(string output) => ApplyBranches(ParseBranchOutput(output));

    #endregion

    #region SSH Setup

    private void SetupSSHKey()
    {
        ClearLogs();
        sshSetupRunning = true;

        string sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        string keyPath = Path.Combine(sshDir, "id_ed25519");
        string pubKeyPath = keyPath + ".pub";
        string capturedPassword = sshPassword;
        string capturedUser = sshUser;
        string capturedHost = sshHost;

        System.Threading.Tasks.Task.Run(() =>
        {
            if (!File.Exists(keyPath))
            {
                pendingLogs.Enqueue(("SSH 키 생성 중...", false));
                using var kp = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/bash",
                        Arguments = $"-c \"mkdir -p '{sshDir}' && ssh-keygen -t ed25519 -N '' -f '{keyPath}'\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    }
                };
                kp.Start();
                kp.WaitForExit();
                pendingLogs.Enqueue(("SSH 키 생성 완료", false));
            }
            else
            {
                pendingLogs.Enqueue(("기존 SSH 키 사용", false));
            }

            pendingLogs.Enqueue(($"{capturedUser}@{capturedHost} 에 키 등록 중...", false));

            string tempScript = Path.Combine(Path.GetTempPath(), "ssh_setup_ci.exp");
            File.WriteAllText(tempScript,
                $"spawn ssh-copy-id -i {pubKeyPath} {capturedUser}@{capturedHost}\n" +
                $"expect {{\n" +
                $"  \"yes/no\" {{ send \"yes\\r\"; exp_continue }}\n" +
                $"  \"password:\" {{ send \"{capturedPassword}\\r\"; exp_continue }}\n" +
                $"  \"already exist\" {{ exit 0 }}\n" +
                $"  eof {{}}\n" +
                $"}}\n");

            using var ep = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/expect",
                    Arguments = tempScript,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            ep.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) pendingLogs.Enqueue((e.Data, false)); };
            ep.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) pendingLogs.Enqueue((e.Data, true)); };
            ep.Start();
            ep.BeginOutputReadLine();
            ep.BeginErrorReadLine();
            ep.WaitForExit();
            File.Delete(tempScript);

            pendingLogs.Enqueue(("SSH 키 등록 완료 — 비밀번호 없이 연결됩니다", false));
            sshSetupRunning = false;
        });

        EditorApplication.update += OnSSHSetupUpdate;
    }

    private void OnSSHSetupUpdate() =>
        FlushPendingLogs(ref sshSetupRunning, () =>
        {
            sshPassword = string.Empty;
            EditorApplication.update -= OnSSHSetupUpdate;
        });

    private void SetupGithubCredentials()
    {
        ClearLogs();
        githubSetupRunning = true;
        string user = sshUser, host = sshHost, ghUser = githubUser, ghToken = githubToken;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                pendingLogs.Enqueue(("GitHub 인증 정보 설정 중...", false));
                // git credential store에 토큰 등록
                RunSilent("/bin/bash",
                    $"-c \"ssh {user}@{host} " +
                    $"'git config --global credential.helper store && " +
                    $"echo https://{ghUser}:{ghToken}@github.com >> ~/.git-credentials && " +
                    $"chmod 600 ~/.git-credentials'\"");
                pendingLogs.Enqueue(("GitHub 인증 설정 완료", false));
            }
            catch (Exception ex)
            {
                pendingLogs.Enqueue(($"설정 실패: {ex.Message}", true));
            }
            finally { githubSetupRunning = false; }
        });
        EditorApplication.update += OnGithubSetupUpdate;
    }

    private void OnGithubSetupUpdate() =>
        FlushPendingLogs(ref githubSetupRunning, () =>
            EditorApplication.update -= OnGithubSetupUpdate);

    #endregion

    #region CI Worker Setup

    private void SetupCIWorker()
    {
        ClearLogs();
        workerSetupRunning = true;
        string user = sshUser, host = sshHost;
        string appBundleRemote = $"/Users/{user}/Applications/CIBuildWorker.app";
        string appExecRemote   = $"{appBundleRemote}/Contents/MacOS/ci_worker";

        // Aqua 세션 타입으로 제한 → 그래픽 로그인 세션에서만 실행됨
        // Background 세션은 FDA가 적용 안 됨 (그래픽 세션 밖이라 TCC 컨텍스트가 없음)
        string launchAgentPlist =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\"><dict>\n" +
            "    <key>Label</key><string>com.cibuild.worker</string>\n" +
            "    <key>ProgramArguments</key><array>\n" +
            $"        <string>{appExecRemote}</string>\n" +
            "    </array>\n" +
            "    <key>RunAtLoad</key><true/>\n" +
            "    <key>KeepAlive</key><true/>\n" +
            "    <key>LimitLoadToSessionType</key><string>Aqua</string>\n" +
            "</dict></plist>\n";

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string localCSource    = Path.Combine(Path.GetTempPath(), "ci_worker.c");
                string localInfoPlist  = Path.Combine(Path.GetTempPath(), "CIWorkerInfo.plist");
                string localLAPlist    = Path.Combine(Path.GetTempPath(), "com.cibuild.worker.plist");
                File.WriteAllText(localCSource, CIWorkerCSource);
                File.WriteAllText(localInfoPlist, CIWorkerAppPlist);
                File.WriteAllText(localLAPlist, launchAgentPlist);

                string remoteCSource = $"/Users/{user}/.ci_worker.c";

                pendingLogs.Enqueue(("앱 번들 생성 중 (~/Applications/CIBuildWorker.app)...", false));
                RunSilent("/bin/bash", $"-c \"ssh {user}@{host} 'mkdir -p {appBundleRemote}/Contents/MacOS'\"");
                RunSilent("/bin/bash", $"-c \"scp '{localCSource}' {user}@{host}:{remoteCSource}\"");
                RunSilent("/bin/bash", $"-c \"scp '{localInfoPlist}' {user}@{host}:{appBundleRemote}/Contents/Info.plist\"");

                pendingLogs.Enqueue(("Mac Mini에서 ci_worker 컴파일 중...", false));
                RunSilent("/bin/bash", $"-c \"ssh {user}@{host} 'cc -o {appExecRemote} {remoteCSource} && chmod +x {appExecRemote}'\"");

                pendingLogs.Enqueue(("LaunchAgent 등록 중...", false));
                RunSilent("/bin/bash", $"-c \"scp '{localLAPlist}' {user}@{host}:~/Library/LaunchAgents/com.cibuild.worker.plist\"");
                // Aqua 세션(그래픽 로그인)에 bootstrap — gui/UID 도메인
                RunSilent("/bin/bash", $"-c \"ssh {user}@{host} 'launchctl bootout gui/$(id -u) ~/Library/LaunchAgents/com.cibuild.worker.plist 2>/dev/null; launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.cibuild.worker.plist'\"");

                pendingLogs.Enqueue(("설치 완료!", false));
                pendingLogs.Enqueue(("Mac Mini → 시스템 설정 → 개인 정보 보호 → 전체 디스크 접근 권한", false));
                pendingLogs.Enqueue(("→ CIBuildWorker.app 이 목록에 없으면 + 클릭해서 추가 후 토글 켜기", false));
                pendingLogs.Enqueue(("→ 설정 후 Mac Mini에서 로그아웃/로그인 한 번 필요", false));
            }
            catch (Exception ex)
            {
                pendingLogs.Enqueue(($"설치 실패: {ex.Message}", true));
            }
            finally { workerSetupRunning = false; }
        });

        EditorApplication.update += OnWorkerSetupUpdate;
    }

    private void OnWorkerSetupUpdate() =>
        FlushPendingLogs(ref workerSetupRunning, () =>
            EditorApplication.update -= OnWorkerSetupUpdate);

    #endregion

    #region Build

    private void OnClickBuild()
    {
        ClearLogs();

        string shPath       = Path.GetFullPath(Path.Combine(Application.dataPath, "../AutoBuild.sh"));
        string ciBuilderPath = Path.Combine(Application.dataPath, "Editor/CIBuilder.cs");
        string cleanFlag    = cleanBuild.ToString().ToLower();
        string devFlag      = isDevelopment.ToString().ToLower();

        string fileName = "/bin/bash";
        string arguments;

        if (buildMode == BuildMode.Remote)
        {
            string sshTarget    = $"{sshUser}@{sshHost}";
            string remoteProject = string.IsNullOrEmpty(remoteLoadPath) ? loadPath : remoteLoadPath;
            string remoteProjectTrimmed = remoteProject.TrimEnd('/');
            string remoteProjectName    = Path.GetFileName(remoteProjectTrimmed);
            string remoteParent         = remoteProjectTrimmed[..remoteProjectTrimmed.LastIndexOf('/')];
            string remoteBuildOutputPath = $"{remoteParent}/Build/{remoteProjectName}/{buildTarget}_{autherName}";

            string monitorPath = Path.Combine(Path.GetTempPath(), "ci_monitor.sh");
            File.WriteAllText(monitorPath, CIMonitorScript);

            string requestContent =
                $"bash ~/.ci_autobuild.sh '{remoteProject}' " +
                $"\"$(dirname '{remoteProject}')/Build\" " +
                $"{buildTarget} ~/.ci_builder.cs '{productName}' '{autherName}' {cleanFlag} {devFlag} '{branchName}'";
            string requestPath = Path.Combine(Path.GetTempPath(), "ci_request.sh");
            File.WriteAllText(requestPath, requestContent);

            // cleanBuild이 아닐 때만 출력 폴더 사전 체크 (요청 전송 전에 확인)
            string folderCheckCmd = cleanBuild ? "" :
                $"ssh {sshTarget} \\\"if [ -d '{remoteBuildOutputPath}' ]; then " +
                $"echo '오류: 빌드 출력 폴더가 이미 존재합니다: {remoteBuildOutputPath}'; " +
                $"echo '클린 빌드 옵션을 켜거나 폴더를 삭제하세요.'; exit 1; fi\\\" && ";

            arguments =
                $"-c \"echo '빌드 스크립트 전송 중...' && " +
                $"scp '{ciBuilderPath}' {sshTarget}:~/.ci_builder.cs && " +
                $"scp '{shPath}' {sshTarget}:~/.ci_autobuild.sh && " +
                $"scp '{monitorPath}' {sshTarget}:~/.ci_monitor.sh && " +
                $"ssh {sshTarget} 'chmod +x ~/.ci_monitor.sh ~/.ci_autobuild.sh' && " +
                folderCheckCmd +
                $"echo '빌드 요청 전송 중...' && " +
                $"scp '{requestPath}' {sshTarget}:~/.ci_request && " +
                $"echo '빌드 서버 연결 중...' && " +
                $"ssh {sshTarget} 'bash ~/.ci_monitor.sh'\"";
        }
        else
        {
            if (!cleanBuild)
            {
                string projectName = Path.GetFileName(loadPath.TrimEnd('/'));
                string outputDir   = Path.Combine(buildPath, projectName, $"{buildTarget}_{autherName}");
                if (Directory.Exists(outputDir) && Directory.GetFileSystemEntries(outputDir).Length > 0)
                {
                    Debug.LogError($"오류: 빌드 출력 폴더가 이미 존재합니다: {outputDir}\n클린 빌드 옵션을 켜거나 폴더를 삭제하세요.");
                    return;
                }
            }
#if UNITY_EDITOR_WIN
            fileName  = "cmd.exe";
            arguments = $"/c {shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{ciBuilderPath}\" \"{productName}\" \"{autherName}\" \"{cleanFlag}\" \"{devFlag}\" \"{branchName}\"";
#else
            arguments = $"{shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{ciBuilderPath}\" \"{productName}\" \"{autherName}\" \"{cleanFlag}\" \"{devFlag}\" \"{branchName}\"";
#endif
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
                CreateNoWindow = true,
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data) && e.Data != "===CI_DONE===")
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
        buildProcess   = process;
        EditorApplication.update += OnEditorUpdate;
    }

    private void StopBuild()
    {
        if (buildProcess == null) return;
        try
        {
            int pid = buildProcess.Id;
            // PGID kill은 Unity Editor 자체가 같은 그룹에 있어 Editor까지 종료됨
            // → pgrep -P로 자식 트리를 재귀 탐색해 PID 단위로 kill
            string killScript = "#!/bin/bash\n" +
                "f(){ for c in $(pgrep -P $1 2>/dev/null); do f $c; done; kill -9 $1 2>/dev/null; }; f " + pid;
            string killPath = Path.Combine(Path.GetTempPath(), "ci_kill.sh");
            File.WriteAllText(killPath, killScript);
            using var killer = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = killPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            killer.Start();
            killer.WaitForExit(2000);
            if (!buildProcess.HasExited)
                buildProcess.Kill();
        }
        catch { }
        Debug.Log("빌드 중지됨");
        buildProcess = null;
        EditorApplication.update -= OnEditorUpdate;
        Repaint();
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
        if (hasNew && autoScroll) logScroll.y = float.MaxValue;

        if (buildProcess != null && buildProcess.HasExited)
        {
            double elapsed = EditorApplication.timeSinceStartup - buildStartTime;
            int min = (int)elapsed / 60;
            int sec = (int)elapsed % 60;

            bool success = buildProcess.ExitCode == 0;
            string summary = $"빌드 {(success ? "성공" : "실패")} - 총 {min:D2}:{sec:D2}";
            buildLogs.Add((summary, !success));
            if (autoScroll) logScroll.y = float.MaxValue;

            if (success) Debug.Log(summary);
            else Debug.LogError(summary);

            buildProcess = null;
            buildProcessExited = true;
        }

        // 프로세스 종료 후에도 큐가 빌 때까지 계속 실행하다가 구독 해제
        if (buildProcessExited && pendingLogs.IsEmpty)
        {
            buildProcessExited = false;
            EditorApplication.update -= OnEditorUpdate;
        }

        double now = EditorApplication.timeSinceStartup;
        if (hasNew || buildProcessExited || now - lastRepaintTime >= 0.5)
        {
            lastRepaintTime = now;
            Repaint();
        }
    }

    #endregion
}
