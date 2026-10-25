#if UNITY_IOS
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

namespace GuitarAR.Editor
{
    /// <summary>
    /// Links Apple's Vision framework, which the hand-tracking plugin needs and Unity doesn't add.
    /// </summary>
    static class GuitarARXcodePostProcess
    {
        [PostProcessBuild]
        static void OnPostprocessBuild(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS)
                return;

            var projectPath = PBXProject.GetPBXProjectPath(path);
            var project = new PBXProject();
            project.ReadFromFile(projectPath);
            project.AddFrameworkToProject(project.GetUnityFrameworkTargetGuid(), "Vision.framework", false);
            project.WriteToFile(projectPath);
        }
    }
}
#endif
