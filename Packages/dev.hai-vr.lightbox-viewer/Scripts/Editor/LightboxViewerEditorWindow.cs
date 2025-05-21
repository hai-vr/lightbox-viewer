using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hai.LightboxViewer.Scripts.Runtime;
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
        private const string BasicSceneFolder = "1cef314dbf6e7814a8f2867c36e87835";
        private const string LightVolumesSceneFolder = "927b5f5dbdab0a74d93f997f9af74118";
        private const string DepthEnablerAsset = "b5094f9d6061779489b1ead6865042b2";
        
        private const string ActivateLightboxViewerLabel = "Activate LightboxViewer";
        private const string AdvancedLabel = "Advanced";
        private const string CollectionsLabel = "Collections";
        private const string CounterRotateLabel = "Counter-rotate";
        private const string DiscardLabel = "Discard";
        private const string InstallPostProcessingLabel = "Install Post-processing";
        private const string MsgInvalidLightboxScene = "Lightbox scene has no root GameObject named \"Lightboxes\", or it is empty.";
        private const string MsgLightboxSceneModifiedPromptSave = "You have modified the lightbox scene.\nDo you want to save the lightbox scene?";
        private const string MsgPlayModeRequiresPreActivation = "To use LightboxViewer in Play mode, activate it before entering Play mode.";
        private const string MsgPostProcessingMissing = "Post-processing is missing from the project.\nInstall post-processing?";
        private const string RealignLabel = "Realign";
        private const string RenderingLabel = "Rendering";
        private const string ResetLabel = "Reset";
        private const string RestartPlayModeLabel = "Restart Play mode with LightboxViewer";
        private const string SaveLabel = "Save";
        private const string LightboxViewerPrefsKey = "LightboxViewer.";

        public Transform objectToView;
        public Camera referenceCamera;
        public SceneAsset lightboxScene;
        public float cameraRoll;
        public bool advanced = true;
        public bool enabled;
        
        public static bool CounterRotate
        {
            get => EditorPrefs.GetBool(PrefsKey(nameof(CounterRotate)), true);
            set => EditorPrefs.SetBool(PrefsKey(nameof(CounterRotate)), value);
        }
        public static bool PostProcessing
        {
            get => EditorPrefs.GetBool(PrefsKey(nameof(PostProcessing)), true);
            set => EditorPrefs.SetBool(PrefsKey(nameof(PostProcessing)), value);
        }
        public static float VerticalDisplacement
        {
            get => EditorPrefs.GetFloat(PrefsKey(nameof(VerticalDisplacement)), 0f);
            set => EditorPrefs.SetFloat(PrefsKey(nameof(VerticalDisplacement)), value);
        }
        public static bool MuteLightsInsideObject
        {
            get => EditorPrefs.GetBool(PrefsKey(nameof(MuteLightsInsideObject)), false);
            set => EditorPrefs.SetBool(PrefsKey(nameof(MuteLightsInsideObject)), value);
        }
        public static bool SupportDepthTexture
        {
            get => EditorPrefs.GetBool(PrefsKey(nameof(SupportDepthTexture)), false);
            set => EditorPrefs.SetBool(PrefsKey(nameof(SupportDepthTexture)), value);
        }

        private static string PrefsKey(string prop) => $"{LightboxViewerPrefsKey}.{prop}";

        private Vector2 _scrollPos;
        private int _generatedSize;
        
        // Special passes
        private GameObject _depthEnabler;

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
            if (_depthEnabler == null)
            {
                _depthEnabler = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(DepthEnablerAsset));
            }
            
            if (lightboxScene == null)
            {
#if !LIGHTBOXVIEWER_LIGHTVOLUMES_SUPPORTED
                var sceneToUse = BasicSceneFolder;
#else
                var sceneToUse = LightVolumesSceneFolder;
#endif
                var path = AssetDatabase.GUIDToAssetPath(sceneToUse);
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
            var headerLines = 5.5f;

            _scrollPos = GUILayout.BeginScrollView(_scrollPos, GUILayout.Height(position.height - EditorGUIUtility.singleLineHeight));
            var serializedObject = new SerializedObject(this);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(objectToView)));
            EditorGUI.BeginDisabledGroup(enabled);
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(lightboxScene)));
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.Slider(serializedObject.FindProperty(nameof(cameraRoll)), -1f, 1f);
            EditorGUILayout.LabelField(CounterRotateLabel, GUILayout.Width(100));

            EditorGUI.BeginChangeCheck();
            PrefsToggle("", CounterRotate, newValue => CounterRotate = newValue, GUILayout.Width(EditorGUIUtility.singleLineHeight));
            EditorGUI.BeginDisabledGroup(cameraRoll == 0);
            if (ColoredBgButton(cameraRoll != 0, Color.green, () => GUILayout.Button(ResetLabel, GUILayout.Width(150))))
            {
                serializedObject.FindProperty(nameof(cameraRoll)).floatValue = 0;
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();

            if (objectToView == null || lightboxScene == null || SceneManager.GetSceneAt(0).path == AssetDatabase.GetAssetPath(lightboxScene))
            {
                EditorGUI.BeginDisabledGroup(true);
                ColoredBgButton(enabled, Color.red, () => GUILayout.Button(ActivateLightboxViewerLabel));
                EditorGUI.EndDisabledGroup();
            }
            else if (Application.isPlaying && !enabled)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(MsgPlayModeRequiresPreActivation, MessageType.Warning);
                if (GUILayout.Button(RestartPlayModeLabel, GUILayout.Width(position.width / 2), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
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
                ColoredBgButton(enabled, Color.red, () => GUILayout.Button(ActivateLightboxViewerLabel, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)));
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.HelpBox(MsgLightboxSceneModifiedPromptSave, MessageType.Warning);
                headerLines += 2;
                if (GUILayout.Button(SaveLabel, GUILayout.Width(100), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    ProjectRenderQueue.SaveLightbox();
                }
                if (GUILayout.Button(DiscardLabel, GUILayout.Width(100), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    Disable();
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                EditorGUI.BeginDisabledGroup(objectToView == null || Application.isPlaying);
                if (ColoredBgButton(enabled, Color.red, () => GUILayout.Button(ActivateLightboxViewerLabel)))
                {
                    ToggleLightboxViewer();
                }
                EditorGUI.EndDisabledGroup();
            }

            if (advanced)
            {
                advanced = EditorGUILayout.Foldout(advanced, AdvancedLabel);

                EditorGUILayout.BeginHorizontal();
                PrefsSlider(nameof(VerticalDisplacement), VerticalDisplacement, newValue => VerticalDisplacement = newValue, 0f, 2f);
                EditorGUI.BeginDisabledGroup(VerticalDisplacement == 0);
                if (ColoredBgButton(VerticalDisplacement != 0, Color.green, () => GUILayout.Button(ResetLabel, GUILayout.Width(150))))
                {
                    serializedObject.FindProperty(nameof(VerticalDisplacement)).floatValue = 0;
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();
                headerLines += 1;
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                advanced = EditorGUILayout.Foldout(advanced, AdvancedLabel);
                GUILayout.FlexibleSpace();
                PrefsToggle(nameof(PostProcessing), PostProcessing, newValue => PostProcessing = newValue);
                EditorGUILayout.EndHorizontal();
            }

            if (PplType == null)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(MsgPostProcessingMissing, MessageType.Warning);
                headerLines += 2;
                EditorGUI.BeginDisabledGroup(_ppInstall);
                if (GUILayout.Button(InstallPostProcessingLabel, GUILayout.Width(200), GUILayout.Height(EditorGUIUtility.singleLineHeight * 2)))
                {
                    _ppInstall = true;
                    Client.Add("com.unity.postprocessing");
                }
                EditorGUI.EndDisabledGroup();
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (advanced)
            {
                EditorGUILayout.BeginVertical(GUILayout.Width(SidebarWidth));
            
                EditorGUILayout.LabelField(RenderingLabel, EditorStyles.boldLabel);
                PrefsToggle(nameof(PostProcessing), PostProcessing, newValue => PostProcessing = newValue);
                PrefsToggle(nameof(MuteLightsInsideObject), MuteLightsInsideObject, newValue => MuteLightsInsideObject = newValue);
                PrefsToggle(nameof(SupportDepthTexture), SupportDepthTexture, newValue => SupportDepthTexture = newValue);
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(referenceCamera)));

                EditorGUI.BeginDisabledGroup(!enabled);
                if (GUILayout.Button(RealignLabel))
                {
                    Realign();
                }
                EditorGUI.EndDisabledGroup();

                if (enabled)
                {
                    EditorGUILayout.Separator();
                    EditorGUILayout.LabelField(CollectionsLabel, EditorStyles.boldLabel);
                    var definition = ProjectRenderQueue.DefinitionNullable;
                    if (definition != null)
                    {
                        foreach (var group in definition.viewGroups)
                        {
                            var isSelected = group.key == selected;
                            EditorGUI.BeginChangeCheck();
                            EditorGUILayout.ToggleLeft(group.title, isSelected);
                            if (EditorGUI.EndChangeCheck())
                            {
                                selected = group.key;
                            }
                        }
                    }
                }
            
                EditorGUILayout.EndVertical();
            }
            
            serializedObject.ApplyModifiedProperties();

            if (objectToView != null)
            {
                _focusedObjectNullable = objectToView.gameObject;
                ProjectRenderQueue.QueueSize(int.MaxValue);
                ProjectRenderQueue.Roll(cameraRoll * 180);
                ProjectRenderQueue.CounterRotate(CounterRotate);
                ProjectRenderQueue.Camera(referenceCamera);
                ProjectRenderQueue.PostProcessing(PostProcessing);
                ProjectRenderQueue.VerticalDisplacement(VerticalDisplacement);
                ProjectRenderQueue.MuteLightsInsideObject(MuteLightsInsideObject);
                ProjectRenderQueue.EnableDepthTexture(SupportDepthTexture, _depthEnabler);
                ProjectRenderQueue.Selected(selected);
            }
            
            EditorGUILayout.BeginVertical();
            var att = ProjectRenderQueue.Textures();
            if (att.Length != 0)
            {
                var names = ProjectRenderQueue.Names();

                var availableWidth = position.width - (advanced ? SidebarWidth + 10 : 0);
                var availableHeight = position.height - EditorGUIUtility.singleLineHeight * headerLines;

                int columns;
                int rows;
                if (att.Length == 3)
                {
                    columns = 3;
                    rows = 1;
                }
                else
                {
                    columns = Mathf.CeilToInt(Mathf.Sqrt(att.Length));
                    rows = (1 + (att.Length - 1) / columns);
                }

                var padding = 10;
                var actualWidth = SanitizeTextureSize((int) availableWidth / columns - padding);
                var actualHeight = SanitizeTextureSize((int) availableHeight / rows - padding);
                ProjectRenderQueue.Width(actualWidth);
                ProjectRenderQueue.Height(actualHeight);

                var bypassPlaymodeTintOldColor = GUI.color;
                GUI.color = Color.white;
                for (var i = 0; i < columns; i++)
                {
                    EditorGUILayout.BeginHorizontal();
                    for (int k = i * columns; k < Math.Min(att.Length, (i * columns) + columns); k++)
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
                    EditorGUILayout.HelpBox(MsgInvalidLightboxScene, MessageType.Error);
                }
            }
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
        }

        private static void PrefsToggle(string propName, bool value, Action<bool> setterFn, params GUILayoutOption[] options)
        {
            var newValue = EditorGUILayout.Toggle(new GUIContent(ObjectNames.NicifyVariableName(propName)), value, options);
            if (EditorGUI.EndChangeCheck())
            {
                setterFn(newValue);
            }
        }

        private static void PrefsSlider(string propName, float value, Action<float> setterFn, float leftValue, float rightValue)
        {
            var newValue = EditorGUILayout.Slider(new GUIContent(ObjectNames.NicifyVariableName(propName)), value, leftValue, rightValue);
            if (EditorGUI.EndChangeCheck())
            {
                setterFn(newValue);
            }
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
        private float SidebarWidth = 200;
        private string selected;

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
        private const bool WhenInEditMode_DestroyAllMonoBehaviours = true;
        
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
        private bool _muteLightsInsideObject;
        private bool _enableDepthTexture;
        private GameObject _depthEnabler;
        private string _selected;

        public LightboxViewerDefinition DefinitionNullable { get; private set; }

        // Setters
        public void QueueSize(int queueSize) => _queueSize = queueSize;
        public void Roll(float roll) => _roll = roll;
        public void Camera(Camera camera) => _cameraOptional = camera;
        public void PostProcessing(bool postProcessing) => _postProcessing = postProcessing;
        public void Width(int actualWidth) => _width = actualWidth;
        public void Height(int actualHeight) => _height = actualHeight;
        public void CounterRotate(bool counterRotate) => _counterRotate = counterRotate;
        public void VerticalDisplacement(float verticalDisplacement) => _verticalDisplacement = verticalDisplacement;
        public void MuteLightsInsideObject(bool muteLightsInsideObject) => _muteLightsInsideObject = muteLightsInsideObject;
        public void Selected(string selected) => _selected = selected;
        public void EnableDepthTexture(bool enableDepthTexture, GameObject depthEnabler)
        {
            _enableDepthTexture = enableDepthTexture;
            _depthEnabler = depthEnabler;
        }
        
        // Getters
        public bool SceneIsChanged() => _openScene.isDirty;
        public Texture[] Textures() => _textures;
        public string[] Names() => _names;

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
                    if (WhenInEditMode_DestroyAllMonoBehaviours)
                    {
                        // Parent the copy to an inactive object during instantiation, so that we can delete all MonoBehaviours
                        // without triggering their OnEnable and OnDestroy functions
                        copy = new GameObject
                        {
                            transform =
                            {
                                position = originalAvatarGo.transform.position,
                                rotation = originalAvatarGo.transform.rotation,
                                localScale = originalAvatarGo.transform.localScale
                            } 
                        };
                        copy.SetActive(false);
                    
                        var innerCopy = Object.Instantiate(originalAvatarGo, copy.transform, true);
                        var allMonoBehaviours = innerCopy.GetComponentsInChildren<MonoBehaviour>(true);
                        foreach (var monoBehaviourNullable in allMonoBehaviours)
                        {
                            // GetComponentsInChildren may return null MonoBehaviour if their script can't be loaded
                            if (monoBehaviourNullable != null)
                            {
                                Object.DestroyImmediate(monoBehaviourNullable);
                            }
                        }
                        innerCopy.SetActive(true);
                    }
                    else
                    {
                        copy = Object.Instantiate(originalAvatarGo);
                    }
                    
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

            var all = new List<Behaviour>();
            foreach (var that in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (that.isActiveAndEnabled
                    && that.gameObject.scene != _openScene
                    && (_muteLightsInsideObject || !FirstIsAnyParentOfSecond(copy.transform, that.transform)))
                {
                    all.Add(that);
                }
            }
            foreach (var that in Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (that.isActiveAndEnabled && that.gameObject.scene != _openScene) all.Add(that);
            }

            if (DefinitionNullable != null)
            {
                foreach (var hideInRender in DefinitionNullable.hideInRenders)
                {
                    hideInRender.SetActive(false);
                }
            }

            GameObject ourDepthEnabler = null;
            try
            {
                if (_enableDepthTexture) ourDepthEnabler = Object.Instantiate(_depthEnabler);
                foreach (var it in all) it.enabled = false;
                TrueRender(copy);
            }
            finally
            {
                if (DefinitionNullable != null)
                {
                    foreach (var hideInRender in DefinitionNullable.hideInRenders)
                    {
                        hideInRender.SetActive(true);
                    }
                }
                foreach (var it in all) it.enabled = true;
                var lightboxes = AllLightboxes();
                for (var index = 0; index < lightboxes.Length; index++)
                {
                    lightboxes[index].gameObject.SetActive(history[index]);
                }
                Object.DestroyImmediate(ourDepthEnabler);
            }
        }

        private bool FirstIsAnyParentOfSecond(Transform first, Transform second)
        {
            if (first == second) return true;
            if (second.parent == null) return false;
            return FirstIsAnyParentOfSecond(first, second.parent);
        }

        private bool[] RecordDisableLightboxes()
        {
            var lightboxes = AllLightboxes();
            var history = lightboxes
                .Select(o => o.gameObject.activeSelf)
                .ToArray();

            foreach (var lightbox in lightboxes)
            {
                lightbox.gameObject.SetActive(false);
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
                    currentLightbox.gameObject.SetActive(true);
                    viewer.RenderNoAnimator(_lightboxIndexToTexture[lightboxIndex], currentLightbox.gameObject, renderTexture, _referentialVector, _referentialQuaternion, _verticalDisplacement);
                    currentLightbox.gameObject.SetActive(false);

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

        public void LoadLightbox(SceneAsset lightbox)
        {
            _openScene = EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(lightbox), OpenSceneMode.Additive);
            DefinitionNullable = GetDefinitionOrNull();
            LightProbes.Tetrahedralize();
            _sceneLoaded = true;
            ForceRequireRenderAll();
        }

        private LightboxViewerDefinition GetDefinitionOrNull()
        {
            var rootObjects = _openScene.GetRootGameObjects();
            foreach (var obj in rootObjects)
            {
                var definition = obj.GetComponentInChildren<LightboxViewerDefinition>();
                if (definition != null)
                {
                    return definition;
                }
            }
            
            return null;
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
            if (DefinitionNullable != null)
            {
                return DefinitionNullable.lightboxes;
            }
            
            // Below should be legacy behaviour for old custom scenes.
            
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
            if (DefinitionNullable != null)
            {
                foreach (var group in DefinitionNullable.viewGroups)
                {
                    if (group.key == _selected)
                    {
                        return group.members;
                    } 
                }

                return DefinitionNullable.viewGroups[0].members;
            }
            
            // Below should be legacy behaviour for old custom scenes.
            
            return AllLightboxes()
                .Where(lightbox => !lightbox.CompareTag("EditorOnly"))
                .ToArray();
        }
    }
}