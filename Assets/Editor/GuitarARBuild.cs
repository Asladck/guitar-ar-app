using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace GuitarAR.Editor
{
    /// <summary>
    /// Builds the iOS Xcode project into Builds/IOS without opening the editor window:
    /// Unity -batchmode -quit -projectPath . -buildTarget iOS -executeMethod GuitarAR.Editor.GuitarARBuild.BuildIOS
    /// </summary>
    static class GuitarARBuild
    {
        const string k_IOSPath = "Builds/IOS";

        [MenuItem("Guitar AR/Build iOS")]
        public static void BuildIOS()
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = k_IOSPath,
                target = BuildTarget.iOS,
                options = BuildOptions.None,
            });

            var summary = report.summary;
            Debug.Log($"[GuitarARBuild] {summary.result}: {summary.totalErrors} errors, {summary.outputPath}");
            if (Application.isBatchMode && summary.result != BuildResult.Succeeded)
                EditorApplication.Exit(1);
        }
    }
}
