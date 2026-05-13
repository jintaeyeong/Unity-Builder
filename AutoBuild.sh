#!/bin/bash

LOAD_PATH=$1

if [ ! -f "$LOAD_PATH/ProjectSettings/ProjectVersion.txt" ]; then
    echo "유효한 Unity 프로젝트가 아닙니다: $LOAD_PATH"
    exit 1
fi

BUILD_TARGET=$3
CI_BUILDER_PATH=$4
PRODUCT_NAME=$5
AUTHOR_NAME=$6
CLEAN_BUILD=${7:-false}
IS_DEVELOPMENT=${8:-false}
BRANCH_NAME=${9:-""}

PROJECT_NAME=$(basename "$LOAD_PATH")
BUILD_PATH="$2/$PROJECT_NAME/${BUILD_TARGET}_${AUTHOR_NAME}"

UNITY_VERSION=$(grep "m_EditorVersion:" "$LOAD_PATH/ProjectSettings/ProjectVersion.txt" | awk '{print $2}')
UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"

if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity 실행파일을 찾을 수 없습니다: $UNITY_PATH"
    exit 1
fi

mkdir -p "$BUILD_PATH"
chmod 777 "$BUILD_PATH"

mkdir -p "$LOAD_PATH/Assets/Editor"
cp "$CI_BUILDER_PATH" "$LOAD_PATH/Assets/Editor/CIBuilder.cs"

if [ $? -ne 0 ]; then
    echo "CIBuilder.cs 복사 실패"
    exit 1
fi

echo "=== 빌드 인자 확인 ==="
echo "LOAD_PATH:      $LOAD_PATH"
echo "BUILD_PATH:     $BUILD_PATH"
echo "BUILD_TARGET:   $BUILD_TARGET"
echo "PRODUCT_NAME:   $PRODUCT_NAME"
echo "CLEAN_BUILD:    $CLEAN_BUILD"
echo "IS_DEVELOPMENT: $IS_DEVELOPMENT"
echo "BRANCH_NAME:    ${BRANCH_NAME:-(생략)}"
echo "GIT_USER:       ${GIT_USER:-(미설정)}"
echo "======================"

# Git 최신화
if [ -n "$BRANCH_NAME" ]; then
    echo "=== Git 최신화: $BRANCH_NAME ==="

    if [ ! -d "$LOAD_PATH/.git" ]; then
        echo "Git 저장소가 아닙니다: $LOAD_PATH"
        exit 1
    fi

    # 계정 정보가 있으면 임시 credential helper 스크립트 생성
    CRED_SCRIPT=""
    if [ -n "$GIT_USER" ] && [ -n "$GIT_TOKEN" ]; then
        REMOTE_URL=$(git -C "$LOAD_PATH" remote get-url origin 2>/dev/null)
        if echo "$REMOTE_URL" | grep -q "^git@"; then
            echo "경고: SSH 원격 URL은 사용자명/토큰 인증을 지원하지 않습니다."
            echo "      SSH 키를 등록하거나 HTTPS URL로 변경하세요: $REMOTE_URL"
        else
            echo "Git 계정 적용: $GIT_USER"
            CRED_SCRIPT=$(mktemp /tmp/git_cred_XXXXXX)
            chmod +x "$CRED_SCRIPT"
            printf '#!/bin/bash\necho "username=%s"\necho "password=%s"\n' \
                "$GIT_USER" "$GIT_TOKEN" > "$CRED_SCRIPT"
            # 빈 값으로 먼저 설정 → 글로벌/시스템 helper(osxkeychain 등) 무력화
            git -C "$LOAD_PATH" config --local credential.helper ""
            git -C "$LOAD_PATH" config --local --add credential.helper "$CRED_SCRIPT"
            export GIT_TERMINAL_PROMPT=0
        fi
    fi

    _git_cleanup() {
        if [ -n "$CRED_SCRIPT" ]; then
            git -C "$LOAD_PATH" config --local --unset-all credential.helper 2>/dev/null
            rm -f "$CRED_SCRIPT"
        fi
    }

    git -C "$LOAD_PATH" fetch origin
    if [ $? -ne 0 ]; then
        echo "git fetch 실패 — 네트워크, 권한 또는 계정 정보를 확인하세요"
        _git_cleanup; exit 1
    fi

    git -C "$LOAD_PATH" checkout "$BRANCH_NAME"
    if [ $? -ne 0 ]; then
        echo "브랜치 전환 실패: $BRANCH_NAME"
        _git_cleanup; exit 1
    fi

    git -C "$LOAD_PATH" pull origin "$BRANCH_NAME"
    if [ $? -ne 0 ]; then
        echo "git pull 실패 — 네트워크, 권한 또는 계정 정보를 확인하세요"
        _git_cleanup; exit 1
    fi

    _git_cleanup
    echo "Git 최신화 완료 ($(git -C "$LOAD_PATH" rev-parse --short HEAD))"
    echo "================================"
fi

EXTRA_FLAGS=""
[ "$CLEAN_BUILD" = "true" ] && EXTRA_FLAGS="$EXTRA_FLAGS -cleanBuild"
[ "$IS_DEVELOPMENT" = "true" ] && EXTRA_FLAGS="$EXTRA_FLAGS -development"

START_TIME=$SECONDS

$UNITY_PATH \
  -batchmode \
  -quit \
  -projectPath "$LOAD_PATH" \
  -buildTarget "$BUILD_TARGET" \
  -customBuildTarget "$BUILD_TARGET" \
  -executeMethod CIBuilder.Build \
  -customBuildPath "$BUILD_PATH" \
  -productName "$PRODUCT_NAME" \
  -logFile "$BUILD_PATH/build.log" \
  $EXTRA_FLAGS

UNITY_EXIT=$?
UNITY_TIME=$((SECONDS - START_TIME))

if [ $UNITY_EXIT -ne 0 ]; then
    echo "Unity 빌드 실패 (${UNITY_TIME}초) — 마지막 로그:"
    tail -50 "$BUILD_PATH/build.log"
    exit 1
fi

echo "Unity 빌드 완료: ${UNITY_TIME}초"

# iOS: Xcode 빌드로 실제 .ipa 생성
if [ "$BUILD_TARGET" = "iOS" ]; then
    echo "=== Xcode 빌드 시작 ==="

    XCODE_PROJECT="$BUILD_PATH/Unity-iPhone.xcodeproj"
    ARCHIVE_PATH="$BUILD_PATH/archive.xcarchive"
    IPA_PATH="$BUILD_PATH/ipa"
    EXPORT_PLIST="$BUILD_PATH/ExportOptions.plist"

    if [ ! -d "$XCODE_PROJECT" ]; then
        echo "Xcode 프로젝트를 찾을 수 없습니다: $XCODE_PROJECT"
        exit 1
    fi

    # 서명 없이 빌드 (CI 환경 기본값 — 배포 시 CODE_SIGN_IDENTITY 설정 필요)
    cat > "$EXPORT_PLIST" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>method</key>
    <string>development</string>
    <key>compileBitcode</key>
    <false/>
</dict>
</plist>
EOF

    xcodebuild \
        -project "$XCODE_PROJECT" \
        -scheme "Unity-iPhone" \
        -configuration "Release" \
        -archivePath "$ARCHIVE_PATH" \
        archive \
        CODE_SIGN_IDENTITY="" \
        CODE_SIGNING_REQUIRED=NO \
        CODE_SIGNING_ALLOWED=NO \
        | grep -E "^(error:|warning:|Build succeeded|** ARCHIVE)"

    if [ $? -ne 0 ]; then
        echo "Xcode archive 실패 — 서명(Code Signing) 설정을 확인하세요"
        exit 1
    fi

    XCODE_TIME=$((SECONDS - START_TIME - UNITY_TIME))
    echo "Xcode 빌드 완료: ${XCODE_TIME}초"
fi

TOTAL_TIME=$((SECONDS - START_TIME))
echo "빌드 성공: $BUILD_PATH (총 ${TOTAL_TIME}초)"
