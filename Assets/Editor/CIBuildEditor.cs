using System;
using System.Diagnostics;
using System.IO;
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

    private Process buildProcess;
    private static GUIStyle helpStyle;


    [MenuItem("Tool/Unity_CI")]
    public static void SetWindow()
    {
        var window = GetWindow<CIBuildEditor>("Unity Build CI Tool");
        window.minSize = new Vector2(400, 400);
        window.maxSize = new Vector2(1000, 1000);
    }

    private void OnEnable()
    {
        loadPath = EditorPrefs.GetString("CIBuildEditor_LoadPath", string.Empty);
        buildPath = EditorPrefs.GetString("CIBuildEditor_BuildPath", string.Empty);
        buildTarget = (BuildTarget)EditorPrefs.GetInt("CIBuildEditor_BuildTarget", (int)BuildTarget.Android);
        productName = EditorPrefs.GetString("CIBuildEditor_ProductName", string.Empty);
        autherName = EditorPrefs.GetString("CIBuildEditor_AutherName", string.Empty);

        // loadPath 있으면 프로젝트 이름 자동 로드
        if (!string.IsNullOrEmpty(loadPath))
            LoadProjectName();
    }

    private void OnDisable()
    {
        EditorPrefs.SetString("CIBuildEditor_LoadPath", loadPath);
        EditorPrefs.SetString("CIBuildEditor_BuildPath", buildPath);
        EditorPrefs.SetInt("CIBuildEditor_BuildTarget", (int)buildTarget);
        EditorPrefs.SetString("CIBuildEditor_ProductName", productName);
        EditorPrefs.SetString("CIBuildEditor_AutherName", autherName);
    }

    // Editor 메소드
    private void OnGUI()
    {
        if (helpStyle == null)
        {
            helpStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontSize = 13,
                richText = true,
            };
        }

        EditorGUILayout.LabelField(
            "Load Path는 빌드 하고싶은 프로젝트의 경로 \n" +
            "Build Path는 빌드 후 생성 된 폴더의 최상위 폴더입니다\n" +
            "설정된 폴더\\projectName\\platformTarget\\파일명 으로 생성됩니다",
            helpStyle,
            new GUILayoutOption[] { GUILayout.Height(100) }
        );

        EditorGUILayout.Space(30);

        // 경로 설정
        EditorGUILayout.LabelField("경로 설정", EditorStyles.boldLabel);
        EditorGUILayout.Separator();
        SetLoadPath();
        SetBuildPath();

        EditorGUILayout.Space(30);

        // 빌드 설정
        EditorGUILayout.LabelField("빌드 설정", EditorStyles.boldLabel);
        SetBuildSetting();
        EditorGUILayout.Space(30);



        GUI.enabled = !string.IsNullOrEmpty(loadPath) && !string.IsNullOrEmpty(buildPath);
        if (GUILayout.Button("Build", GUILayout.Height(40), GUILayout.Width(200)))
        {
            OnClickBuild();
        }
        GUI.enabled = true;

        // 예시 코드
        //ExampleEditorCode();
    }

    #region Test

    private string myString = string.Empty;
    private int myInt = 0;
    private float myFloat = 0;
    private Vector2 scroll;
    private void ExampleEditorCode()
    {
        EditorGUILayout.LabelField("일반 레이블");
        EditorGUILayout.LabelField("굵은 레이블", EditorStyles.boldLabel);
        EditorGUILayout.LabelField("미니 레이블", EditorStyles.miniLabel);
        EditorGUILayout.LabelField("굵은 미니", EditorStyles.miniBoldLabel);
        EditorGUILayout.LabelField("큰 레이블", EditorStyles.largeLabel);
        EditorGUILayout.LabelField("워드랩 레이블", EditorStyles.wordWrappedLabel);
        EditorGUILayout.LabelField("링크 스타일", EditorStyles.linkLabel);

        // 헤더 스타일 (Foldout 섹션 제목 느낌)
        EditorGUILayout.LabelField("헤더", EditorStyles.boldLabel);

        EditorGUILayout.HelpBox("일반 정보입니다.", MessageType.None);
        EditorGUILayout.HelpBox("참고 사항입니다.", MessageType.Info);       // ℹ️ 파란 아이콘
        EditorGUILayout.HelpBox("주의가 필요합니다.", MessageType.Warning);  // ⚠️ 노란 아이콘
        EditorGUILayout.HelpBox("오류가 발생했습니다.", MessageType.Error);  // ❌ 빨간 아이콘

        // 기본 텍스트 필드
        myString = EditorGUILayout.TextField("레이블", myString);

        // 패스워드 필드
        myString = EditorGUILayout.PasswordField("비밀번호", myString);

        // 여러 줄 텍스트
        myString = EditorGUILayout.TextArea(myString, GUILayout.Height(60));

        // 딜레이 적용 (Enter 누를 때만 값 반영)
        myString = EditorGUILayout.DelayedTextField("딜레이 텍스트", myString);
        myInt = EditorGUILayout.DelayedIntField("딜레이 정수", myInt);
        myFloat = EditorGUILayout.DelayedFloatField("딜레이 실수", myFloat);

        // 가로 배치
        EditorGUILayout.BeginHorizontal();
        {
            GUILayout.Button("버튼 A");
            GUILayout.Button("버튼 B");
        }
        EditorGUILayout.EndHorizontal();

        // 박스 테두리
        EditorGUILayout.BeginVertical("box");
        {
            EditorGUILayout.LabelField("박스 안 내용");
            EditorGUILayout.IntField("숫자", 0);
        }
        EditorGUILayout.EndVertical();

        // 스크롤뷰
        scroll = EditorGUILayout.BeginScrollView(scroll);
        {
            for (int i = 0; i < 30; i++)
                EditorGUILayout.LabelField($"항목 {i}");
        }
        EditorGUILayout.EndScrollView();

        // 버튼 크기 지정
        GUILayout.Button("큰 버튼", GUILayout.Width(200), GUILayout.Height(40));

        // 필드 너비 고정
        EditorGUILayout.TextField("라벨", myString, GUILayout.Width(300));

        // 유연하게 남은 공간 채우기
        EditorGUILayout.BeginHorizontal();
        GUILayout.Button("고정", GUILayout.Width(80));
        GUILayout.FlexibleSpace();  // 여백 채움
        GUILayout.Button("오른쪽 끝");
        EditorGUILayout.EndHorizontal();

        // Inspector처럼 보이는 섹션
        EditorGUILayout.BeginVertical("helpbox");
        EditorGUILayout.LabelField("설정", EditorStyles.boldLabel);
        EditorGUILayout.Space(4);
        myInt = EditorGUILayout.IntField("수치", myInt);
        myFloat = EditorGUILayout.Slider("슬라이더", myFloat, 0f, 1f);
        EditorGUILayout.EndVertical();
    }

    #endregion


    private void SetLoadPath()
    {
        EditorGUILayout.BeginHorizontal();

        // 경로 표시 필드 (읽기 전용처럼)
        EditorGUILayout.TextField("Load Path", loadPath);

        // 찾아보기 버튼
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string selected = EditorUtility.OpenFolderPanel(
                "폴더 선택",   // 다이얼로그 제목
                loadPath,    // 초기 경로 (빈 문자열이면 기본 위치)
                ""             // 기본 폴더명
            );

            // 취소하면 빈 문자열 반환 → 기존 값 유지
            if (!string.IsNullOrEmpty(selected))
            {
                loadPath = selected;
                LoadProjectName();
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    private void SetBuildPath()
    {
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.TextField("Build Path", buildPath);

        // 찾아보기 버튼
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string selected = EditorUtility.OpenFolderPanel(
                "폴더 선택",   // 다이얼로그 제목
                buildPath,    // 초기 경로 (빈 문자열이면 기본 위치)
                ""             // 기본 폴더명
            );

            // 취소하면 빈 문자열 반환 → 기존 값 유지
            if (!string.IsNullOrEmpty(selected))
            {
                buildPath = selected;
            }
        }

        EditorGUILayout.EndHorizontal();
    }

    // 빌드 시 파일 이름 설정(기본으로 세팅된 이름 가져올 수 있음)
    private void LoadProjectName()
    {
        string _projectSettingPath = Path.Combine(loadPath, "ProjectSettings/ProjectSettings.asset");
        if (!File.Exists(_projectSettingPath))
        {
            Debug.LogError("ProjectSettings.asset 파일을 찾을 수 없습니다.");
            return;
        }

        string[] lines = File.ReadAllLines(_projectSettingPath);
        foreach (var line in lines)
        {
            if (line.Contains("productName"))
            {
                productName = line.Split(':')[1].Trim();
                break;
            }
        }
    }

    private void SetBuildSetting()
    {
        buildTarget = (BuildTarget)EditorGUILayout.EnumPopup("Build Target", buildTarget);
        autherName = EditorGUILayout.TextField("빌드 생성자", autherName);
        productName = EditorGUILayout.TextField("빌드 파일 이름", productName);
    }



    private void OnClickBuild()
    {
        string shPath = Path.Combine(Application.dataPath, "../AutoBuild.sh");
        shPath = Path.GetFullPath(shPath);

        string _cIBuilderPath = Path.Combine(Application.dataPath, "Editor/CIBuilder.cs");

#if UNITY_EDITOR_WIN
            string fileName = "cmd.exe";
            string arguments = $"/c {_shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{_cIBuilderPath}\" \"{productName}\" \"{autherName}\"";
#elif UNITY_EDITOR_OSX
        string fileName = "/bin/bash";
        string arguments = $"{shPath} \"{loadPath}\" \"{buildPath}\" \"{buildTarget}\" \"{_cIBuilderPath}\" \"{productName}\" \"{autherName}\"";

#endif

        Debug.Log($"arguments: {arguments}");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo()
            {
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Debug.Log(e.Data);
            }
        };

        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Debug.LogError(e.Data);
        };


        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        buildProcess = process;
        EditorApplication.update += CheckBuildProcess;

    }

    private void CheckBuildProcess()
    {
        if (buildProcess != null && buildProcess.HasExited)
        {
            Debug.Log($"빌드 완료 - Exit Code : {buildProcess.ExitCode}");
            buildProcess = null;
            EditorApplication.update -= CheckBuildProcess;
        }
        else
        {
            // 빌드 중일 때 프로그레스 바 애니메이션
            float progress = (float)(EditorApplication.timeSinceStartup % 1.0);
            EditorUtility.DisplayProgressBar("빌드 중", "Unity batchmode 실행 중...", progress);
        }
    }
}