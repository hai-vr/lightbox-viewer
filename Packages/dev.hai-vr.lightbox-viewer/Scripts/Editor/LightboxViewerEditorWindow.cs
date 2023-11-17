﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    [InitializeOnLoad]
    public class LightboxViewerEditorWindow : EditorWindow
    {
        public Transform objectToView;
        public Camera referenceCamera;
        public SceneAsset lightboxScene;
        public float cameraRoll;
        public bool counterRotate = true;
        public bool postProcessing = true;
        public bool advanced;
        public float verticalDisplacement;
        public bool enabled;
        private Vector2 _scrollPos;
        private int _generatedSize;

        public LightboxViewerEditorWindow()
        {
            titleContent = new GUIContent("LightboxViewer");
            // SceneView.duringSceneGui -= DuringSceneGui;
            // SceneView.duringSceneGui += DuringSceneGui;
            _repaint = Repaint;

            PplType = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(assembly => assembly.GetTypes())
                .FirstOrDefault(type => type.Name == "PostProcessLayer");
            if (PplType != null)
            {
                PplVolumeLayerField = PplType.GetField("volumeLayer", BindingFlags.Instance | BindingFlags.Public);
                PplVolumeTriggerField = PplType.GetField("volumeTrigger", BindingFlags.Instance | BindingFlags.Public);
            }
        }

        private void OnEnable()
        {
            if (lightboxScene == null)
            {
                var path = AssetDatabase.GUIDToAssetPath("1cef314dbf6e7814a8f2867c36e87835");
                if (path != null)
                {
                    lightboxScene = AssetDatabase.LoadAssetAtPath<SceneAsset>($"{path}.unity");
                }
            }
        }

        private void Update()
        {
            if (!enabled) return;
            if (enabled && lightboxScene == null)
            {
                Disable();
            }

            if (enabled && !EditorApplication.isPlaying && objectToView == null)
            {
                // Happens when restarting Unity
                Disable();
            }

            if (objectToView == null) return;

            UpdateAny();
        }

        private void OnDisable()
        {
            if (enabled && !ProjectRenderQueue.SceneIsChanged() && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Disable();
            }
        }

        // private void DuringSceneGui(SceneView obj)
        // {
            // if (!_enabled) return;
            // Handles.TransformHandle(ref _referentialVector, ref _referentialQuaternion);
            // _projectRenderQueue.Referential(_referentialVector, _referentialQuaternion);
        // }

        private void OnGUI()
        {
            var headerLines = 6.5f;

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(position.height - EditorGUIUtility.singleLineHeight));
            var serializedObject = new SerializedObject(this);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(objectToView)));

            EditorGUI.BeginDisabledGroup(enabled);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(lightboxScene)));
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Slider(serializedObject.FindProperty(nameof(cameraRoll)), -1f, 1f);
            EditorGUILayout.LabelField("Counter-rotate", GUILayout.Width(100));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(counterRotate)), GUIContent.none, GUILayout.Width(EditorGUIUtility.singleLineHeight));
            EditorGUI.BeginDisabledGroup(cameraRoll == 0);
            if (ColoredBgButton(cameraRoll != 0, Color.green, () => GUILayout.Button("Reset", GUILayout.Width(150))))
            {
                serializedObject.FindProperty(nameof(cameraRoll)).floatValue = 0;
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (objectToView == null || lightboxScene == null || SceneManager.GetSceneAt(0).path == AssetDatabase.GetAssetPath(lightboxScene))
            {
                EditorGUI.BeginDisabledGroup(true);
                ColoredBgButton(enabled, Color.red, () => GUILayout.Button("Activate LightboxViewer"));
                EditorGUI.EndDisabledGroup();
            }
            else if (Application.isPlaying && !enabled)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("To use LightboxViewer in Play mode, activate it before entering Play mode.", MessageType.Warning);
                if (GUILayout.Button("Restart Play mode with LightboxViewer", GUILayout.Width(position.width / 2), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    EditorApplication.isPlaying = false;
                    EditorApplication.delayCall += RestartPlayMode;
                }
                EditorGUILayout.EndHorizontal();
            }
            else if (!Application.isPlaying && enabled && ProjectRenderQueue.SceneIsChanged())
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginDisabledGroup(true);
                ColoredBgButton(enabled, Color.red, () => GUILayout.Button("Activate LightboxViewer", GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)));
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.HelpBox("You have modified the lightbox scene.\nDo you want to save the lightbox scene?", MessageType.Warning);
                headerLines += 2;
                if (GUILayout.Button("Save", GUILayout.Width(100), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    ProjectRenderQueue.SaveLightbox();
                }
                if (GUILayout.Button("Discard", GUILayout.Width(100), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    Disable();
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(objectToView == null || Application.isPlaying);
                if (ColoredBgButton(enabled, Color.red, () => GUILayout.Button("Activate LightboxViewer")))
                {
                    ToggleLightboxViewer();
                }
                EditorGUI.EndDisabledGroup();
            }

            if (advanced)
            {
                advanced = EditorGUILayout.Foldout(advanced, "Advanced");
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(postProcessing)));
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(referenceCamera)));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.Slider(serializedObject.FindProperty(nameof(verticalDisplacement)), 0, 2f);
                EditorGUI.BeginDisabledGroup(verticalDisplacement == 0);
                if (ColoredBgButton(verticalDisplacement != 0, Color.green, () => GUILayout.Button("Reset", GUILayout.Width(150))))
                {
                    serializedObject.FindProperty(nameof(verticalDisplacement)).floatValue = 0;
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginDisabledGroup(!enabled);
                if (GUILayout.Button("Realign"))
                {
                    Realign();
                }
                EditorGUI.EndDisabledGroup();
                headerLines += 5;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                advanced = EditorGUILayout.Foldout(advanced, "Advanced");
                GUILayout.FlexibleSpace();
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(postProcessing)));
                EditorGUILayout.EndHorizontal();
            }

            if (PplType == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("Post-processing is missing from the project.\nInstall post-processing?", MessageType.Warning);
                headerLines += 2;
                EditorGUI.BeginDisabledGroup(_ppInstall);
                if (GUILayout.Button("Install Post-processing", GUILayout.Width(200), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    _ppInstall = true;
                    Client.Add("com.unity.postprocessing");
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();
            }

            serializedObject.ApplyModifiedProperties();

            if (objectToView != null)
            {
                _focusedObjectNullable = objectToView.gameObject;
                ProjectRenderQueue.QueueSize(int.MaxValue);
                ProjectRenderQueue.Roll(cameraRoll * 180);
                ProjectRenderQueue.CounterRotate(counterRotate);
                ProjectRenderQueue.Camera(referenceCamera);
                ProjectRenderQueue.PostProcessing(postProcessing);
                ProjectRenderQueue.VerticalDisplacement(verticalDisplacement);
            }

            var att = ProjectRenderQueue.Textures().ToArray();
            if (att.Length != 0)
            {
                var names = ProjectRenderQueue.Names();

                var availableWidth = position.width;
                var availableHeight = position.height - EditorGUIUtility.singleLineHeight * headerLines;

                var numberOfRows = Mathf.CeilToInt(Mathf.Sqrt(att.Length));
                var numberOfColumns = (1 + (att.Length - 1) / numberOfRows);

                var padding = 10;
                var actualWidth = SanitizeTextureSize((int) availableWidth / numberOfRows - padding);
                var actualHeight = SanitizeTextureSize((int) availableHeight / numberOfColumns - padding);
                ProjectRenderQueue.Width(actualWidth);
                ProjectRenderQueue.Height(actualHeight);

                var bypassPlaymodeTintOldColor = GUI.color;
                GUI.color = Color.white;
                for (var i = 0; i < numberOfRows; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    for (int k = i * numberOfRows; k < Math.Min(att.Length, (i * numberOfRows) + numberOfRows); k++)
                    {
                        var texture = att[k];
                        GUILayout.Box(new GUIContent(texture, names[k]), GUILayout.Width(actualWidth), GUILayout.Height(actualHeight));
                    }

                    EditorGUILayout.EndHorizontal();
                }

                GUI.color = bypassPlaymodeTintOldColor;
            }
            else
            {
                if (enabled)
                {
                    EditorGUILayout.HelpBox("Lightbox scene has no root GameObject named \"Lightboxes\", or it is empty.", MessageType.Error);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void RestartPlayMode()
        {
            if (EditorApplication.isPlaying)
            {
                EditorApplication.delayCall += RestartPlayMode;
                return;
            }
            Enable();
            EditorApplication.isPlaying = true;
        }

        private int SanitizeTextureSize(int min)
        {
            var bounded = Math.Max(16, Mathf.Min(2048, min));
            return bounded - bounded % 8;
        }

        private void ToggleLightboxViewer()
        {
            if (!enabled)
            {
                Enable();
            }
            else
            {
                Disable();
            }
        }

        private void Enable()
        {
            if (enabled) return;

            ProjectRenderQueue.LoadLightbox(lightboxScene);
            var so = new SerializedObject(this);
            so.FindProperty(nameof(enabled)).boolValue = true;
            so.ApplyModifiedPropertiesWithoutUndo();
            Realign();

            // Fix UI text rendering over everything.
            // This normally fixes itself when entering Play mode, but this will allow not needing to enter Play mode.
            var LessEqual = 4;
            Shader.SetGlobalInt("unity_GUIZTestMode", LessEqual);
        }

        private void Realign()
        {
            _referentialVector = objectToView.position;
            _referentialQuaternion = Quaternion.Inverse(objectToView.rotation);
            ProjectRenderQueue.Referential(_referentialVector, _referentialQuaternion);
        }

        private void Disable()
        {
            if (!enabled) return;
            ProjectRenderQueue.UnloadLightbox();
            var so = new SerializedObject(this);
            so.FindProperty(nameof(enabled)).boolValue = false;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private void UsingObjectToView(Transform newObjectToView)
        {
            this.objectToView = newObjectToView;
        }

        private static bool ColoredBgButton(bool isActive, Color bgColor, Func<bool> inside)
        {
            var col = GUI.backgroundColor;
            try
            {
                if (isActive) GUI.backgroundColor = bgColor;
                return inside();
            }
            finally
            {
                GUI.backgroundColor = col;
            }
        }

        [MenuItem("Window/Haï/LightboxViewer")]
        public static void ShowWindow()
        {
            Obtain().Show();
        }

        [MenuItem("CONTEXT/Transform/Haï LightboxViewer")]
        public static void OpenEditor(MenuCommand command)
        {
            var window = Obtain();
            window.UsingObjectToView((Transform) command.context);
            window.Show();
        }

        private static LightboxViewerEditorWindow Obtain()
        {
            var editor = GetWindow<LightboxViewerEditorWindow>(false, null, false);
            editor.titleContent = new GUIContent("LightboxViewer");
            return editor;
        }

        private static Action _repaint;
        private static readonly LightboxViewerRenderQueue ProjectRenderQueue;
        private static GameObject _focusedObjectNullable;
        private float _generatedNormalizedTime;
        private Vector3 _referentialVector;
        private Quaternion _referentialQuaternion;
        private bool _ppInstall;
        internal static Type PplType;
        internal static FieldInfo PplVolumeLayerField;
        internal static FieldInfo PplVolumeTriggerField;
        private bool _ensured;

        static LightboxViewerEditorWindow()
        {
            ProjectRenderQueue = new LightboxViewerRenderQueue();
        }

        private void UpdateAny()
        {
            if (!enabled) return;

            if (!_ensured && EditorApplication.isPlaying)
            {
                _ensured = true;
                ProjectRenderQueue.EnsureLightbox(AssetDatabase.GetAssetPath(lightboxScene));
                Realign();
            }
            else if (_ensured && !EditorApplication.isPlaying)
            {
                _ensured = false;
            }
            ProjectRenderQueue.ForceRequireRenderAll();
            var didRerender = Rerender();
            if (didRerender && _repaint != null)
            {
                _repaint.Invoke();
            }
        }

        private static bool Rerender()
        {
            if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive) return false; // Async readback has issues when the editor is not in focus.

            if (_focusedObjectNullable == null) return false;

            return ProjectRenderQueue.TryRender(_focusedObjectNullable);
        }
    }

    public class LightboxViewerRenderQueue
    {
        private readonly Dictionary<int, Texture2D> _lightboxIndexToTexture;
        private readonly Queue<int> _queue;
        private int _queueSize;
        private Scene _openScene;
        private bool _sceneLoaded;
        private Texture2D[] _textures = new Texture2D[1];
        private string[] _names = new string[1];
        private int _width = 512;
        private int _height = 512;
        private float _roll;
        private LightboxViewerGenerator _previousViewer;
        private bool _counterRotate;
        private Vector3 _referentialVector;
        private Quaternion _referentialQuaternion;
        private Camera _cameraOptional;
        private bool _postProcessing;
        private float _verticalDisplacement;

        public LightboxViewerRenderQueue()
        {
            _lightboxIndexToTexture = new Dictionary<int, Texture2D>();
            _queue = new Queue<int>();
        }

        private Texture2D RequireRender(int lightboxIndex, int width, int height)
        {
            if (_lightboxIndexToTexture.ContainsKey(lightboxIndex)
                && _lightboxIndexToTexture[lightboxIndex] != null // Can happen when the texture is destroyed (Unity invalid object)
                && _lightboxIndexToTexture[lightboxIndex].width == width
                && _lightboxIndexToTexture[lightboxIndex].height == height)
            {
                if (!_queue.Contains(lightboxIndex))
                {
                    _queue.Enqueue(lightboxIndex);
                }
                return _lightboxIndexToTexture[lightboxIndex];
            }

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            _lightboxIndexToTexture[lightboxIndex] = texture; // TODO: Dimensions

            _queue.Enqueue(lightboxIndex);

            return texture;
        }

        public bool TryRender(GameObject root)
        {
            if (_previousViewer != null && _previousViewer.IsStillRendering() > 0)
            {
                return false;
            }

            if (_queue.Count == 0) return false;

            if (Application.isPlaying)
            {
                var pos = root.transform.position;
                var rot = root.transform.rotation;
                var scale = root.transform.localScale;
                try
                {
                    Render(root);
                }
                finally
                {
                    root.transform.position = pos;
                    root.transform.rotation = rot;
                    root.transform.localScale = scale;
                }
            }
            else
            {
                var originalAvatarGo = root;
                GameObject copy = null;
                var wasActive = originalAvatarGo.activeSelf;
                try
                {
                    copy = Object.Instantiate(originalAvatarGo);
                    copy.SetActive(true);
                    originalAvatarGo.SetActive(false);
                    Render(copy);
                }
                finally
                {
                    if (wasActive) originalAvatarGo.SetActive(true);
                    if (copy != null) Object.DestroyImmediate(copy);
                }
            }

            return true;
        }

        private void Render(GameObject copy)
        {
            var history = RecordDisableLightboxes();
            var allLights = Object.FindObjectsOfType<Light>().Where(light => light.isActiveAndEnabled);
            var allReflectionProbes = Object.FindObjectsOfType<ReflectionProbe>().Where(reflectionProbe => reflectionProbe.isActiveAndEnabled);
            var all = allLights
                .Concat<Behaviour>(allReflectionProbes)
                .Where(behaviour => behaviour.gameObject.scene != _openScene)
                .ToArray();
            try
            {
                foreach (var it in all) it.enabled = false;
                TrueRender(copy);
            }
            finally
            {
                foreach (var it in all) it.enabled = true;
                var lightboxes = AllLightboxes();
                for (var index = 0; index < lightboxes.Length; index++)
                {
                    lightboxes[index].SetActive(history[index]);
                }
            }
        }

        private bool[] RecordDisableLightboxes()
        {
            var lightboxes = AllLightboxes();
            var history = lightboxes
                .Select(o => o.activeSelf)
                .ToArray();

            foreach (var lightbox in lightboxes)
            {
                lightbox.SetActive(false);
            }

            return history;
        }

        private void TrueRender(GameObject copy)
        {
            var viewer = new LightboxViewerGenerator();
            try
            {
                viewer.Begin(copy, _roll, _counterRotate, _cameraOptional, _postProcessing);

                if (!Application.isPlaying)
                {
                    // Fixes a problem where the avatar is flickering
                    copy.gameObject.SetActive(false);
                    copy.gameObject.SetActive(true);
                }

                var itemCount = 0;
                var renderTexture = RenderTexture.GetTemporary(_lightboxIndexToTexture[0].width, _lightboxIndexToTexture[0].height, 24);
                var allApplicableLightboxes = AllApplicableLightboxes();
                while (_queue.Count > 0 && itemCount < _queueSize)
                {
                    var lightboxIndex = _queue.Dequeue();
                    var currentLightbox = allApplicableLightboxes[lightboxIndex];
                    currentLightbox.SetActive(true);
                    viewer.RenderNoAnimator(_lightboxIndexToTexture[lightboxIndex], currentLightbox, renderTexture, _referentialVector, _referentialQuaternion, _verticalDisplacement);
                    currentLightbox.SetActive(false);

                    itemCount++;
                }
                RenderTexture.ReleaseTemporary(renderTexture);
            }
            finally
            {
                viewer.Terminate();
            }

            _previousViewer = viewer;
        }

        public void QueueSize(int queueSize)
        {
            _queueSize = queueSize;
        }

        public void Roll(float roll)
        {
            _roll = roll;
        }

        public void Camera(Camera camera)
        {
            _cameraOptional = camera;
        }

        public void PostProcessing(bool postProcessing)
        {
            _postProcessing = postProcessing;
        }

        public void LoadLightbox(SceneAsset lightbox)
        {
            _openScene = EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(lightbox), OpenSceneMode.Additive);
            LightProbes.Tetrahedralize();
            _sceneLoaded = true;
            ForceRequireRenderAll();
        }

        public void EnsureLightbox(string path)
        {
            if (_sceneLoaded) return;

            _openScene = SceneManager.GetSceneByPath(path);
            _sceneLoaded = true;
            ForceRequireRenderAll();
        }

        public void UnloadLightbox()
        {
            if (_sceneLoaded)
            {
                EditorSceneManager.CloseScene(_openScene, true);
                LightProbes.Tetrahedralize();
                _sceneLoaded = false;
                _openScene = default;
            }
        }

        public bool SceneIsChanged()
        {
            return _openScene.isDirty;
        }

        public IEnumerable<Texture> Textures()
        {
            return _textures;
        }

        public string[] Names()
        {
            return _names;
        }

        public void ForceRequireRenderAll()
        {
            var lightboxes = AllApplicableLightboxes();
            if (_textures.Length != lightboxes.Length)
            {
                _textures = new Texture2D[lightboxes.Length];
                _names = new string[lightboxes.Length];
            }
            for (var i = 0; i < lightboxes.Length; i++)
            {
                _textures[i] = RequireRender(i, _width, _height);
                _names[i] = lightboxes[i].name;
            }
        }

        public void Width(int actualWidth)
        {
            _width = actualWidth;
        }

        public void Height(int actualHeight)
        {
            _height = actualHeight;
        }

        public void CounterRotate(bool counterRotate)
        {
            _counterRotate = counterRotate;
        }

        public void SaveLightbox()
        {
            EditorSceneManager.SaveScene(_openScene);
        }

        public void Referential(Vector3 referentialVector, Quaternion referentialQuaternion)
        {
            _referentialVector = referentialVector;
            _referentialQuaternion = referentialQuaternion;
        }

        private GameObject[] AllLightboxes()
        {
            var holder = _openScene.GetRootGameObjects()
                .FirstOrDefault(o => o.name == "Lightboxes");

            if (holder == null) return new GameObject[0];

            return holder.transform
                .Cast<Transform>()
                .Select(lightbox => lightbox.gameObject)
                .ToArray();
        }

        private GameObject[] AllApplicableLightboxes()
        {
            return AllLightboxes()
                .Where(lightbox => !lightbox.CompareTag("EditorOnly"))
                .ToArray();
        }

        public void VerticalDisplacement(float verticalDisplacement)
        {
            _verticalDisplacement = verticalDisplacement;
        }
    }
}