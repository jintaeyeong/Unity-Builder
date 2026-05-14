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

UNITY_VERSION=$(grep "m_EditorVersion:" "$LOAD_PATH/ProjectSettings/ProjectVersion.txt" | awk '{print $2}' | tr -d '\r')

# 설치 경로 후보 순서대로 탐색
UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
if [ ! -f "$UNITY_PATH" ]; then
    UNITY_PATH="/Applications/Unity/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
fi
if [ ! -f "$UNITY_PATH" ]; then
    FOUND=$(mdfind "kMDItemFSName == 'Unity.app'" 2>/dev/null | grep "$UNITY_VERSION" | head -1)
    [ -n "$FOUND" ] && UNITY_PATH="$FOUND/Contents/MacOS/Unity"
fi

if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity $UNITY_VERSION 이(가) 설치되어 있지 않습니다."
    echo "Unity Hub에서 해당 버전을 설치하세요:"
    echo "  1. Unity Hub 실행 → Installs → Install Editor"
    echo "  2. Archive 탭에서 $UNITY_VERSION 검색 후 설치"
    echo "  또는 CLI: \"/Applications/Unity Hub.app/Contents/MacOS/Unity Hub\" -- --headless install --version $UNITY_VERSION"
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
echo "======================"

# Git 최신화
if [ -n "$BRANCH_NAME" ]; then
    echo "=== Git 최신화: $BRANCH_NAME ==="

    if [ ! -d "$LOAD_PATH/.git" ]; then
        echo "Git 저장소가 아닙니다: $LOAD_PATH"
        exit 1
    fi

    git -C "$LOAD_PATH" fetch origin
    if [ $? -ne 0 ]; then
        echo "git fetch 실패 — 네트워크 또는 권한을 확인하세요"
        exit 1
    fi

    git -C "$LOAD_PATH" stash
    git -C "$LOAD_PATH" checkout "$BRANCH_NAME"
    if [ $? -ne 0 ]; then
        echo "브랜치 전환 실패: $BRANCH_NAME"
        exit 1
    fi

    git -C "$LOAD_PATH" pull origin "$BRANCH_NAME"
    if [ $? -ne 0 ]; then
        echo "git pull 실패 — 네트워크 또는 권한을 확인하세요"
        exit 1
    fi

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
  $EXTRA_FLAGS &
UNITY_PID=$!

# build.log 파일을 실시간 스트리밍 (파이프 버퍼링 없이 직접 출력)
tail -F "$BUILD_PATH/build.log" &
TAIL_PID=$!

wait $UNITY_PID
UNITY_EXIT=$?
UNITY_TIME=$((SECONDS - START_TIME))

kill $TAIL_PID 2>/dev/null
wait $TAIL_PID 2>/dev/null

if [ $UNITY_EXIT -ne 0 ]; then
    echo "Unity 빌드 실패 (${UNITY_TIME}초)"
    exit 1
fi

echo "Unity 빌드 완료: ${UNITY_TIME}초"

TOTAL_TIME=$((SECONDS - START_TIME))
echo "빌드 성공: $BUILD_PATH (총 ${TOTAL_TIME}초)"
