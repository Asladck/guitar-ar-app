#!/bin/zsh
# Builds the Unity project for iOS, compiles it with Xcode and installs it on a connected iPhone.
# Unity must be closed (a project can't be built from the command line while the editor has it open).
# Usage: Tools/build-ios.sh <device-udid> <apple-team-id>
set -euo pipefail

PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
DEVICE="${1:?device UDID (xcrun devicectl list devices)}"
TEAM="${2:?Apple team id}"
UNITY_VERSION="$(sed -n 's/^m_EditorVersion: //p' "$PROJECT/ProjectSettings/ProjectVersion.txt")"
UNITY="/Applications/Unity/Hub/Editor/$UNITY_VERSION/Unity.app/Contents/MacOS/Unity"
export DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer
DERIVED="$PROJECT/Builds/DerivedData"

echo "== Unity $UNITY_VERSION: exporting Xcode project"
"$UNITY" -batchmode -quit -projectPath "$PROJECT" -buildTarget iOS \
  -executeMethod GuitarAR.Editor.GuitarARBuild.BuildIOS -logFile "$PROJECT/Builds/unity-build.log"

echo "== Xcode: building"
cd "$PROJECT/Builds/IOS"
xcodebuild -project Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Debug \
  -destination "id=$DEVICE" -derivedDataPath "$DERIVED" -allowProvisioningUpdates \
  DEVELOPMENT_TEAM="$TEAM" CODE_SIGN_STYLE=Automatic build > "$PROJECT/Builds/xcode-build.log" 2>&1 \
  || { grep -E "error:" "$PROJECT/Builds/xcode-build.log" | head -20; exit 1; }

echo "== Installing on $DEVICE"
xcrun devicectl device install app --device "$DEVICE" "$DERIVED/Build/Products/Debug-iphoneos/GuitarAR.app"
