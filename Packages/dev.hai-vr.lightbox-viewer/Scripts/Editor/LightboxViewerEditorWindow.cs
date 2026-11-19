using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if LIGHTBOXVIEWER_URP_SUPPORTED
using UnityEngine.Rendering.Universal;
#endif
using UnityEngine.SceneManagement;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    [InitializeOnLoad]
    public class LightboxViewerEditorWindow : EditorWindow
    {
        private const string BIRPLightProbesSceneFolder = "1cef314dbf6e7814a8f2867c36e87835";
        private const string BIRPREDSIMSceneFolder = "1344adbf490e06446bc631910cf2c56d";
        private const string URPAdaptiveProbeVolumesSceneFolder = "785caa78655b9d341aa02f92ba43e800";
        private const string URPLightProbesSceneFolder = "5087027db2a433c47952becea6b721a4";
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
        private const string NdmfPreviewLabel = "NDMF Preview";
        private const string NdmfPreviewTooltip = "Render the result of NDMF Preview instead of the original object. Renderers whose bones are changed by the preview are rendered as they are in the scene.\n\n" +
                                                  "Disabled: render the original object.\n" +
                                                  "Slow updates: only render the complete result that NDMF Preview shows, and do not render while NDMF Preview computes a new result.\n" +
                                                  "Aggressive updates: render the result as soon as it changes, which may show the object without the result for a few frames (e.g. while editing a material).";
        private static readonly System.Collections.Generic.List<LightboxViewerNdmfPreviewMode> NdmfPreviewModes = new System.Collections.Generic.List<LightboxViewerNdmfPreviewMode>
        {
            LightboxViewerNdmfPreviewMode.Disabled,
            LightboxViewerNdmfPreviewMode.SlowUpdates,
            LightboxViewerNdmfPreviewMode.AggressiveUpdates,
        };
        private static string NdmfPreviewModeName(LightboxViewerNdmfPreviewMode mode)
        {
            switch (mode)
            {
                case LightboxViewerNdmfPreviewMode.Disabled: return "Disabled";
                case LightboxViewerNdmfPreviewMode.SlowUpdates: return "Slow updates";
                default: return "Aggressive updates";
            }
        }
        private const string AntiAliasingLabel = "Anti-aliasing";

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
        /// When false (default), lightboxes are only rendered again when something may have changed.
        /// When true, they are rendered on every editor update, which is needed for things that change without notifying the editor,
        /// such as shaders animated over time, or changes made by scripts that do not record Undo.
        public static bool AlwaysRefresh
        {
            get => EditorPrefs.GetBool(PrefsKey(nameof(AlwaysRefresh)), false);
            set => EditorPrefs.SetBool(PrefsKey(nameof(AlwaysRefresh)), value);
        }
        /// When not Disabled, and NDMF is installed and NDMF Preview is enabled, the result of NDMF Preview is rendered. Aggressive updates by default.
        internal static LightboxViewerNdmfPreviewMode NdmfPreviewMode
        {
            get
            {
                var value = (LightboxViewerNdmfPreviewMode)EditorPrefs.GetInt(PrefsKey(nameof(NdmfPreviewMode)), (int)LightboxViewerNdmfPreviewMode.AggressiveUpdates);
                return NdmfPreviewModes.Contains(value) ? value : LightboxViewerNdmfPreviewMode.AggressiveUpdates;
            }
            set => EditorPrefs.SetInt(PrefsKey(nameof(NdmfPreviewMode)), (int)value);
        }
        public static int AntiAliasingSlider
        {
            get => EditorPrefs.GetInt(PrefsKey(nameof(AntiAliasingSlider)), 2); // Slider values map from 0, 1, 2, 3 to 1, 2, 4, 8 
            set => EditorPrefs.SetInt(PrefsKey(nameof(AntiAliasingSlider)), value);
        }

        private static string PrefsKey(string prop) => $"{LightboxViewerPrefsKey}.{prop}";

        private Vector2 _scrollPos;
        private int _generatedSize;
        
        // Special passes
        private GameObject _depthEnabler;
        private bool _isBuiltInRenderPipeline;
        private bool _isUsingLightProbes;
        private bool _isREDSIMAvailable;

        public LightboxViewerEditorWindow()
        {
            titleContent = new GUIContent("LightboxViewer");
            // SceneView.duringSceneGui -= DuringSceneGui;
            // SceneView.duringSceneGui += DuringSceneGui;
            _repaint = Repaint;
        }

        private void OnEnable()
        {
            if (_depthEnabler == null)
            {
                _depthEnabler = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(DepthEnablerAsset));
            }
            
#if LIGHTBOXVIEWER_URP_SUPPORTED
            QualitySettings.activeQualityLevelChanged += OnQualityLevelChanged;
#endif
            ReevaluateRenderPipelineSettings();

            if (lightboxScene == null)
            {
                var path = SelectScenePathOrNull();
                if (path != null)
                {
                    lightboxScene = AssetDatabase.LoadAssetAtPath<SceneAsset>($"{path}.unity");
                }
            }
        }

        private void OnQualityLevelChanged(int arg1, int arg2)
        {
            ReevaluateRenderPipelineSettings();

            if (!Application.isPlaying && !_isBuiltInRenderPipeline)
            {
                var wasEnabled = enabled;
                if (wasEnabled)
                {
                    Disable();
                    var path = SelectScenePathOrNull();
                    if (path != null)
                    {
                        lightboxScene = AssetDatabase.LoadAssetAtPath<SceneAsset>($"{path}.unity");
                    }
                    Enable();
                }
            }
        }

        private string SelectScenePathOrNull()
        {
            string sceneToUse;
            if (_isBuiltInRenderPipeline)
            {
                sceneToUse = _isREDSIMAvailable ? BIRPREDSIMSceneFolder : BIRPLightProbesSceneFolder;
            }
            else
            {
                sceneToUse = !_isUsingLightProbes ? URPAdaptiveProbeVolumesSceneFolder : URPLightProbesSceneFolder;
            }
            
            return AssetDatabase.GUIDToAssetPath(sceneToUse);
        }

        private void ReevaluateRenderPipelineSettings()
        {
            _isBuiltInRenderPipeline = GraphicsSettings.currentRenderPipeline == null;
            if (!_isBuiltInRenderPipeline)
            {
#if LIGHTBOXVIEWER_URP_APV_SUPPORTED // lightProbeSystem only exists since URP 17 (Unity 6). Older versions only have Light Probes.
                if (QualitySettings.renderPipeline is UniversalRenderPipelineAsset urp)
                {
                    _isUsingLightProbes = urp.lightProbeSystem == LightProbeSystem.LegacyLightProbes;
                }
                else
                {
                    _isUsingLightProbes = true;
                }
#else
                _isUsingLightProbes = true;
#endif
            }
            else
            {
                _isUsingLightProbes = true;
            }
            
#if LIGHTBOXVIEWER_REDSIM_SUPPORTED
            _isREDSIMAvailable = true;
#else
            _isREDSIMAvailable = false;
#endif
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

            Profiler.BeginSample("LightboxViewer.Update.UpdateAny");
            UpdateAny();
            Profiler.EndSample();
        }

        private void OnDisable()
        {
            if (enabled && !ProjectRenderQueue.SceneIsChanged() && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Disable();
            }
#if LIGHTBOXVIEWER_URP_SUPPORTED
            QualitySettings.activeQualityLevelChanged -= OnQualityLevelChanged;
#endif
        }

        // private void DuringSceneGui(SceneView obj)
        // {
            // if (!_enabled) return;
            // Handles.TransformHandle(ref _referentialVector, ref _referentialQuaternion);
            // _projectRenderQueue.Referential(_referentialVector, _referentialQuaternion);
        // }

        private void OnGUI()
        {
            Profiler.BeginSample("LightboxViewer.EditorWindow.OnGUI");
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
                    VerticalDisplacement = 0;
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

            if (_isBuiltInRenderPipeline && PplType == null)
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
                if (LightboxViewerNdmfPreview.IsAvailable)
                {
                    EditorGUILayout.LabelField(new GUIContent(NdmfPreviewLabel, NdmfPreviewTooltip));
                    var currentIndex = NdmfPreviewModes.IndexOf(NdmfPreviewMode);
                    EditorGUI.BeginChangeCheck();
                    var newIndex = EditorGUILayout.Popup(currentIndex, NdmfPreviewModes.Select(NdmfPreviewModeName).ToArray());
                    if (EditorGUI.EndChangeCheck())
                    {
                        NdmfPreviewMode = NdmfPreviewModes[newIndex];
                    }
                }
                PrefsToggle(nameof(AlwaysRefresh), AlwaysRefresh, newValue => AlwaysRefresh = newValue);
                EditorGUI.BeginChangeCheck();
                var aaSlider = AntiAliasingSlider;
                EditorGUILayout.LabelField($"{AntiAliasingLabel} ({RemapAntiAliasing(aaSlider)})", GUILayout.Width(EditorGUIUtility.labelWidth - 2));
                var newAaSlider = Mathf.RoundToInt(GUILayout.HorizontalSlider(aaSlider, 0, 3, GUILayout.Height(EditorGUIUtility.singleLineHeight)));
                if (EditorGUI.EndChangeCheck())
                {
                    AntiAliasingSlider = newAaSlider;
                }
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
                ProjectRenderQueue.AntiAliasing(RemapAntiAliasing(AntiAliasingSlider));
                ProjectRenderQueue.VerticalDisplacement(VerticalDisplacement);
                ProjectRenderQueue.MuteLightsInsideObject(MuteLightsInsideObject);
                ProjectRenderQueue.EnableDepthTexture(SupportDepthTexture, _depthEnabler);
                ProjectRenderQueue.NdmfPreviewMode(NdmfPreviewMode);
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
            Profiler.EndSample();
        }

        private int RemapAntiAliasing(int i)
        {
            return i switch
            {
                1 => 2,
                2 => 4,
                3 => 8,
                _ => 1
            };
        }

        private static void PrefsToggle(string propName, bool value, Action<bool> setterFn, params GUILayoutOption[] options)
        {
            EditorGUI.BeginChangeCheck();
            var newValue = EditorGUILayout.Toggle(new GUIContent(ObjectNames.NicifyVariableName(propName)), value, options);
            if (EditorGUI.EndChangeCheck())
            {
                setterFn(newValue);
            }
        }

        private static void PrefsSlider(string propName, float value, Action<float> setterFn, float leftValue, float rightValue)
        {
            EditorGUI.BeginChangeCheck();
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

            if (LightboxViewerRenderQueue.WhenInEditMode_ReuseCachedCopy)
            {
                ObjectChangeEvents.changesPublished -= OnObjectChange;
                ObjectChangeEvents.changesPublished += OnObjectChange;
            }
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

            if (LightboxViewerRenderQueue.WhenInEditMode_ReuseCachedCopy)
            {
                ObjectChangeEvents.changesPublished -= OnObjectChange;
            }
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

        // Post-processing (com.unity.postprocessing) is an optional dependency, so its PostProcessLayer is accessed through reflection.
        // It is searched for on first access, then remembered (even if not found) until the next domain reload.
        // Installing the package causes a domain reload, so the result cannot become outdated.
        private const string PostProcessLayerTypeName = "UnityEngine.Rendering.PostProcessing.PostProcessLayer";
        private const string PostProcessLayerAssemblyName = "Unity.Postprocessing.Runtime";
        private static bool _pplSearched;
        private static Type _pplType;
        private static FieldInfo _pplVolumeLayerField;
        private static FieldInfo _pplVolumeTriggerField;

        /// Null if post-processing is not installed, or if its PostProcessLayer does not have the expected fields.
        internal static Type PplType { get { EnsurePostProcessLayerSearched(); return _pplType; } }
        internal static FieldInfo PplVolumeLayerField { get { EnsurePostProcessLayerSearched(); return _pplVolumeLayerField; } }
        internal static FieldInfo PplVolumeTriggerField { get { EnsurePostProcessLayerSearched(); return _pplVolumeTriggerField; } }

        private static void EnsurePostProcessLayerSearched()
        {
            if (_pplSearched) return;
            _pplSearched = true;

            Profiler.BeginSample("LightboxViewer.SearchPostProcessLayer");
            var type = Type.GetType($"{PostProcessLayerTypeName}, {PostProcessLayerAssemblyName}", false)
                       ?? FindTypeInLoadedAssembliesOrNull(PostProcessLayerTypeName);
            Profiler.EndSample();
            if (type == null) return;

            var volumeLayerField = type.GetField("volumeLayer", BindingFlags.Instance | BindingFlags.Public);
            var volumeTriggerField = type.GetField("volumeTrigger", BindingFlags.Instance | BindingFlags.Public);
            if (volumeLayerField == null || volumeTriggerField == null) return; // Not the version we know how to set up.

            _pplType = type;
            _pplVolumeLayerField = volumeLayerField;
            _pplVolumeTriggerField = volumeTriggerField;
        }

        private static Type FindTypeInLoadedAssembliesOrNull(string fullTypeName)
        {
            // Fallback in case the assembly is not named as expected. Looking up a type by its full name in each assembly
            // does not need to load all of their types, unlike Assembly.GetTypes(), which can throw ReflectionTypeLoadException.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullTypeName, false);
                    if (type != null) return type;
                }
                catch (Exception)
                {
                    // Some assemblies (e.g. dynamic ones) may not support this. Ignore them.
                }
            }

            return null;
        }
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
            
            // While NDMF Preview computes a new result, nothing is rendered, so that the lightboxes do not show the object without it.
            if (ProjectRenderQueue.ShouldWaitForNdmfPreview()) return;

            // In Play Mode, animations and scripts change the object all the time, so always render.
            var needsRerender = ProjectRenderQueue.ConsumeNeedsRerender(_focusedObjectNullable);
            var rerenderExisting = AlwaysRefresh || EditorApplication.isPlaying || needsRerender;

            Profiler.BeginSample("LightboxViewer.ForceRequireRenderAll");
            ProjectRenderQueue.ForceRequireRenderAll(rerenderExisting);
            Profiler.EndSample();
            
            Profiler.BeginSample("LightboxViewer.Rerender");
            var didRerender = Rerender();
            Profiler.EndSample();
            
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

        private void OnObjectChange(ref ObjectChangeEventStream stream)
        {
            ProjectRenderQueue.ObjectChangeEvent(ref stream);
        }
    }
}