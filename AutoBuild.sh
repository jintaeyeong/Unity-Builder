#!/bin/bash

# 변수 및 경로 설정
LOAD_PATH=$1

## LOAD_PATH가 Unity 프로젝트가 아닐 때 처리
if [ ! -f "$LOAD_PATH/ProjectSettings/ProjectVersion.txt" ]; then
    echo "유효한 Unity 프로젝트가 아닙니다: $LOAD_PATH"
    exit 1
fi

# BuildTarget 설정
BUILD_TARGET=$3
CI_BUILDER_PATH=$4
PRODUCT_NAME=$5
AUTHOR_NAME=$6


# Build Path 설정
PROJECT_NAME=$(basename "$LOAD_PATH")
BUILD_PATH="$2/$PROJECT_NAME/${BUILD_TARGET}_${AUTHOR_NAME}"



# Unity Version을 몰라도 실행 가능
UNITY_VERSION=$(cat "$LOAD_PATH/ProjectSettings/ProjectVersion.txt" | grep "m_EditorVersion:" | awk '{print $2}')
UNITY_PATH="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
## UNITY_PATH가 존재하지 않을 때 예외 처리
if [ ! -f "$UNITY_PATH" ]; then
    echo "Unity 실행파일을 찾을 수 없습니다: $UNITY_PATH"
    exit 1
fi




# 빌드 폴더 생성(이미 존재하면 무시)
mkdir -p "$BUILD_PATH"

# CIBuilder 파일 복사
mkdir -p "$LOAD_PATH/Assets/Editor"
cp "$CI_BUILDER_PATH" "$LOAD_PATH/Assets/Editor/CIBuilder.cs"

## CIBuilder.cs 실패 시 예외 처리
if [ $? -ne 0 ]; then
    echo "CIBuilder.cs 복사 실패"
    exit 1
fi

echo "=== batchmode 인자 확인 ==="
echo "LOAD_PATH: $LOAD_PATH"
echo "BUILD_PATH: $BUILD_PATH"
echo "BATCH_TARGET: $BUILD_TARGET"
echo "PRODUCT_NAME: $PRODUCT_NAME"
echo "=========================="

#bash 실행
# Unity 인자로 넘겨지지 않음, 내부적으로 가져감
# 대신 customBuildTarget 추가
$UNITY_PATH \
  -batchmode \
  -quit \
  -projectPath "$LOAD_PATH" \
  -buildTarget "$BUILD_TARGET" \
  -customBuildTarget "$BUILD_TARGET" \
  -executeMethod CIBuilder.Build \
  -customBuildPath "$BUILD_PATH" \
  -productName "$PRODUCT_NAME" \
  -logFile "$BUILD_PATH/build.log"

## 빌드 결과 확인
if [ $? -ne 0 ]; then
    echo "빌드 실패"
    exit 1
fi

echo "빌드 성공: $BUILD_PATH"

