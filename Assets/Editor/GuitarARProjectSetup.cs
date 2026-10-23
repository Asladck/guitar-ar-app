using System.IO;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace GuitarAR.Editor
{
    /// <summary>
    /// One-click setup for the hand-authored project: creates and assigns the URP asset
    /// (with the AR camera background feature), sets mobile AR player settings, and builds the
    /// AR scene. Safe to run again: existing assets are reused and the scene is rebuilt.
    /// </summary>
    static class GuitarARProjectSetup
    {
        const string k_SettingsFolder = "Assets/Settings";
        const string k_ScenePath = "Assets/Scenes/GuitarAR.unity";
        const string k_RendererPath = k_SettingsFolder + "/MobileAR_Renderer.asset";
        const string k_PipelinePath = k_SettingsFolder + "/MobileAR_URP.asset";
        const string k_PlaneMaterialPath = "Assets/Materials/ARPlane.mat";
        const string k_GuitarMaterialPath = "Assets/Materials/Guitar.mat";
        const string k_PlanePrefabPath = "Assets/Prefabs/ARPlane.prefab";

        [MenuItem("Guitar AR/Set Up Project")]
        static void SetUpProject()
        {
            ConfigureRenderPipeline();
            ConfigurePlayerSettings();
            CreateScene();
            AssetDatabase.SaveAssets();

            EditorUtility.DisplayDialog("Guitar AR",
                "Setup done.\n\nLast manual step: Edit > Project Settings > XR Plug-in Management, " +
                "tick ARCore on the Android tab and ARKit on the iOS tab.", "OK");
        }

        static void ConfigureRenderPipeline()
        {
            EnsureFolder(k_SettingsFolder);

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(k_RendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(rendererData, k_RendererPath);
            }

            if (!rendererData.rendererFeatures.Exists(f => f is ARBackgroundRendererFeature))
            {
                var feature = ScriptableObject.CreateInstance<ARBackgroundRendererFeature>();
                feature.name = "ARBackgroundRendererFeature";
                AssetDatabase.AddObjectToAsset(feature, rendererData);
                rendererData.rendererFeatures.Add(feature);

                // URP tracks features by local file id; keep that map in sync like the inspector does.
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
                var so = new SerializedObject(rendererData);
                var map = so.FindProperty("m_RendererFeatureMap");
                map.arraySize++;
                map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(rendererData);
            }

            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(k_PipelinePath);
            if (pipeline == null)
            {
                pipeline = UniversalRenderPipelineAsset.Create(rendererData);
                AssetDatabase.CreateAsset(pipeline, k_PipelinePath);
            }

            GraphicsSettings.defaultRenderPipeline = pipeline;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = pipeline;
            }
            QualitySettings.SetQualityLevel(current, false);
        }

        static void ConfigurePlayerSettings()
        {
            PlayerSettings.companyName = "Asladck";
            PlayerSettings.productName = "Guitar AR";
            PlayerSettings.colorSpace = ColorSpace.Linear;

            // Android (ARCore): IL2CPP + ARM64, API 26+ (Unity 6 minimum), OpenGL ES 3.
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, "com.asladck.guitarar");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = (AndroidSdkVersions)26;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

            // iOS (ARKit).
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.iOS, "com.asladck.guitarar");
            PlayerSettings.iOS.cameraUsageDescription = "The camera is used to place a guitar in your room.";
            PlayerSettings.iOS.targetOSVersionString = "15.0";
        }

        static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var light = new GameObject("Directional Light").AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var session = new GameObject("AR Session");
            session.AddComponent<ARSession>();
            session.AddComponent<ARInputManager>();

            var originGo = new GameObject("XR Origin");
            var origin = originGo.AddComponent<XROrigin>();
            var offset = new GameObject("Camera Offset");
            offset.transform.SetParent(originGo.transform, false);

            var cameraGo = new GameObject("Main Camera") { tag = "MainCamera" };
            cameraGo.transform.SetParent(offset.transform, false);
            var cam = cameraGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.black;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            cameraGo.AddComponent<AudioListener>();
            cameraGo.AddComponent<ARCameraManager>();
            cameraGo.AddComponent<ARCameraBackground>();

            var poseDriver = cameraGo.AddComponent<TrackedPoseDriver>();
            var position = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
            position.AddBinding("<HandheldARInputDevice>/devicePosition");
            var rotation = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
            rotation.AddBinding("<HandheldARInputDevice>/deviceRotation");
            poseDriver.positionInput = new InputActionProperty(position);
            poseDriver.rotationInput = new InputActionProperty(rotation);

            origin.Camera = cam;
            origin.CameraFloorOffsetObject = offset;

            var planeManager = originGo.AddComponent<ARPlaneManager>();
            planeManager.planePrefab = CreatePlanePrefab();
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            originGo.AddComponent<ARRaycastManager>();
            var placer = new SerializedObject(originGo.AddComponent<GuitarPlacer>());
            placer.FindProperty("m_GuitarMaterial").objectReferenceValue = CreateGuitarMaterial();
            placer.ApplyModifiedPropertiesWithoutUndo();

            EnsureFolder(Path.GetDirectoryName(k_ScenePath));
            EditorSceneManager.SaveScene(scene, k_ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(k_ScenePath, true) };
        }

        static GameObject CreatePlanePrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(k_PlanePrefabPath);
            if (existing != null)
                return existing;

            var go = new GameObject("ARPlane");
            go.AddComponent<ARPlane>();
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshCollider>();
            go.AddComponent<MeshRenderer>().sharedMaterial = CreatePlaneMaterial();
            var line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = false;
            line.widthMultiplier = 0.005f;
            line.sharedMaterial = line.GetComponent<MeshRenderer>().sharedMaterial;
            go.AddComponent<ARPlaneMeshVisualizer>();

            EnsureFolder(Path.GetDirectoryName(k_PlanePrefabPath));
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, k_PlanePrefabPath);
            Object.DestroyImmediate(go);
            return prefab;
        }

        static Material CreateGuitarMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(k_GuitarMaterialPath);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                throw new System.InvalidOperationException("URP Lit shader not found. Is the URP package installed?");

            var mat = new Material(shader) { name = "Guitar" };
            mat.SetFloat("_Smoothness", 0.6f);
            EnsureFolder(Path.GetDirectoryName(k_GuitarMaterialPath));
            AssetDatabase.CreateAsset(mat, k_GuitarMaterialPath);
            return mat;
        }

        static Material CreatePlaneMaterial()
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(k_PlaneMaterialPath);
            if (existing != null)
                return existing;

            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                throw new System.InvalidOperationException("URP Unlit shader not found. Is the URP package installed?");

            // Semi-transparent white so detected surfaces are visible without hiding the room.
            var mat = new Material(shader) { name = "ARPlane" };
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetColor("_BaseColor", new Color(1f, 1f, 1f, 0.25f));

            EnsureFolder(Path.GetDirectoryName(k_PlaneMaterialPath));
            AssetDatabase.CreateAsset(mat, k_PlaneMaterialPath);
            return mat;
        }

        static void EnsureFolder(string path)
        {
            path = path.Replace('\\', '/');
            if (AssetDatabase.IsValidFolder(path))
                return;

            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
