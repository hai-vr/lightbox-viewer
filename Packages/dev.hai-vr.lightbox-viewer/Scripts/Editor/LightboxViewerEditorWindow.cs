using System;
using System.Linq;
using System.Reflection;
using Hai.LightboxViewer.Scripts.Runtime;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditor.PackageManager;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
#if LIGHTBOXVIEWER_URP_SUPPORTED
using UnityEngine.Rendering.Universal;
#endif
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

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
        
        private const string LightboxViewerPrefsKey = "LightboxViewer.";
        private const string LocaleFolder = "Packages/dev.hai-vr.lightbox-viewer/Scripts/Editor/Locale";

        private static HaiEFLoc localize
        {
            get
            {
                _localize ??= NewLoc();
                return _localize;
            }
        }
        private static HaiEFLoc _localize;
        private static HaiEFLoc NewLoc() => new("dev.hai-vr.lightbox-viewer", LocaleFolder);
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
                case LightboxViewerNdmfPreviewMode.Disabled: return localize.Text(Phrases.ndmf_preview_mode_disabled);
                case LightboxViewerNdmfPreviewMode.SlowUpdates: return localize.Text(Phrases.ndmf_preview_mode_slow_updates);
                default: return localize.Text(Phrases.ndmf_preview_mode_aggressive_updates);
            }
        }

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
            _repaint = MarkTexturesDirty;
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
            RefreshLocalization();
            RefreshUI();

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

        // UI Toolkit. The elements are created once in CreateGUI, and RefreshUI (called every editor update) brings them in line with the state.
        private const float CellGap = 2;
        private static readonly Color ActiveRed = new Color(0.8f, 0.25f, 0.25f);
        private static readonly Color ActiveGreen = new Color(0.3f, 0.7f, 0.3f);

        private SerializedObject _so;
        private VisualElement _uiRoot;
        private Button _activateButton;
        private VisualElement _activateRow;
        private VisualElement _playModeRow;
        private VisualElement _modifiedRow;
        private Button _modifiedActivateButton;
        private Toggle _counterRotateToggle;
        private Button _rollResetButton;
        private Foldout _advancedFoldout;
        private Toggle _collapsedPostProcessingToggle;
        private Slider _verticalDisplacementSlider;
        private VisualElement _postProcessingMissingRow;
        private Button _installPostProcessingButton;
        private VisualElement _sidebar;
        private Toggle _postProcessingToggle;
        private Toggle _muteLightsToggle;
        private Toggle _depthTextureToggle;
        private VisualElement _ndmfPreviewGroup;
        private PopupField<LightboxViewerNdmfPreviewMode> _ndmfPreviewPopup;
        private Toggle _alwaysRefreshToggle;
        private Label _antiAliasingLabel;
        private SliderInt _antiAliasingSlider;
        private Button _realignButton;
        private VisualElement _collectionsContainer;
        private VisualElement _collectionsList;
        private LightboxViewerDefinition _collectionsDefinition;
        private readonly System.Collections.Generic.List<(string key, Toggle toggle)> _collectionToggles = new System.Collections.Generic.List<(string, Toggle)>();
        private VisualElement _textureArea;
        private VisualElement _textureGrid;
        private HelpBox _invalidSceneHelpBox;
        private readonly System.Collections.Generic.List<LightboxViewerUntintedImage> _cells = new System.Collections.Generic.List<LightboxViewerUntintedImage>();
        private Texture[] _shownTextures = new Texture[0];
        private int _shownCellWidth;
        private int _shownCellHeight;

        private void CreateGUI()
        {
            _so = new SerializedObject(this);
            _cells.Clear();
            _collectionToggles.Clear();
            _tinted.Clear();
            _collectionsDefinition = null;
            _shownTextures = new Texture[0];

            var root = rootVisualElement;
            root.Clear();
            _uiRoot = root;
            titleContent = new GUIContent(localize.Text(Phrases.window_title));
            _localizationSignature = LocalizationSignature();

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            scroll.contentContainer.style.flexGrow = 1;
            root.Add(scroll);
            var content = scroll.contentContainer;

            // Object and scene
            var objectRow = Row();
            var objectField = new ObjectField(localize.Text(Phrases.object_to_view)) { objectType = typeof(Transform), allowSceneObjects = true, bindingPath = nameof(objectToView) };
            var sceneField = new ObjectField(localize.Text(Phrases.lightbox_scene)) { objectType = typeof(SceneAsset), allowSceneObjects = false, bindingPath = nameof(lightboxScene) };
            Grow(objectField);
            Grow(sceneField);
            objectRow.Add(objectField);
            objectRow.Add(sceneField);
            content.Add(objectRow);
            _sceneField = sceneField;

            // Camera roll
            var rollRow = Row();
            var rollSlider = new Slider(localize.Text(Phrases.camera_roll), -1f, 1f) { showInputField = true, bindingPath = nameof(cameraRoll) };
            rollSlider.style.flexGrow = 1;
            rollRow.Add(rollSlider);
            var counterRotateLabel = new Label(localize.Text(Phrases.counter_rotate)) { style = { width = 100, unityTextAlign = TextAnchor.MiddleLeft } };
            rollRow.Add(counterRotateLabel);
            _counterRotateToggle = new Toggle { style = { width = EditorGUIUtility.singleLineHeight } }; // No label
            _counterRotateToggle.RegisterValueChangedCallback(e => CounterRotate = e.newValue);
            rollRow.Add(_counterRotateToggle);
            _rollResetButton = new Button(() =>
            {
                _so.Update();
                _so.FindProperty(nameof(cameraRoll)).floatValue = 0;
                _so.ApplyModifiedProperties();
            }) { text = localize.Text(Phrases.reset), style = { width = 150 } };
            rollRow.Add(_rollResetButton);
            content.Add(rollRow);

            // Activation
            _activateRow = Row();
            _activateButton = new Button(ToggleLightboxViewer) { text = localize.Text(Phrases.activate) };
            Grow(_activateButton);
            _activateRow.Add(_activateButton);
            content.Add(_activateRow);

            _playModeRow = Row();
            var playModeHelp = new HelpBox(localize.Text(Phrases.msg_play_mode_requires_pre_activation), HelpBoxMessageType.Warning);
            Grow(playModeHelp);
            _playModeRow.Add(playModeHelp);
            _playModeRow.Add(new Button(() =>
            {
                EditorApplication.isPlaying = false;
                EditorApplication.delayCall += RestartPlayMode;
            }) { text = localize.Text(Phrases.restart_play_mode), style = { width = Length.Percent(50), height = EditorGUIUtility.singleLineHeight * 2 } });
            content.Add(_playModeRow);

            _modifiedRow = Row();
            _modifiedActivateButton = new Button { text = localize.Text(Phrases.activate), style = { height = EditorGUIUtility.singleLineHeight * 2 } };
            _modifiedActivateButton.SetEnabled(false);
            Grow(_modifiedActivateButton);
            _modifiedRow.Add(_modifiedActivateButton);
            var modifiedHelp = new HelpBox(localize.Text(Phrases.msg_lightbox_scene_modified), HelpBoxMessageType.Warning);
            Grow(modifiedHelp);
            _modifiedRow.Add(modifiedHelp);
            _modifiedRow.Add(new Button(() => ProjectRenderQueue.SaveLightbox()) { text = localize.Text(Phrases.save), style = { width = 100, height = EditorGUIUtility.singleLineHeight * 2 } });
            _modifiedRow.Add(new Button(Disable) { text = localize.Text(Phrases.discard), style = { width = 100, height = EditorGUIUtility.singleLineHeight * 2 } });
            content.Add(_modifiedRow);

            // Advanced
            var advancedRow = Row();
            _advancedFoldout = new Foldout { text = localize.Text(Phrases.advanced), value = advanced };
            _advancedFoldout.style.flexGrow = 1;
            _advancedFoldout.RegisterValueChangedCallback(e =>
            {
                if (e.target != _advancedFoldout) return; // Value change events bubble up from children
                advanced = e.newValue;
            });
            advancedRow.Add(_advancedFoldout);
            _collapsedPostProcessingToggle = new Toggle { text = localize.Text(Phrases.post_processing) };
            _collapsedPostProcessingToggle.RegisterValueChangedCallback(e => PostProcessing = e.newValue);
            advancedRow.Add(_collapsedPostProcessingToggle);
            content.Add(advancedRow);


            _postProcessingMissingRow = Row();
            var postProcessingHelp = new HelpBox(localize.Text(Phrases.msg_post_processing_missing), HelpBoxMessageType.Warning);
            Grow(postProcessingHelp);
            _postProcessingMissingRow.Add(postProcessingHelp);
            _installPostProcessingButton = new Button(() =>
            {
                _ppInstall = true;
                Client.Add("com.unity.postprocessing");
            }) { text = localize.Text(Phrases.install_post_processing), style = { width = 200, height = EditorGUIUtility.singleLineHeight * 2 } };
            _postProcessingMissingRow.Add(_installPostProcessingButton);
            content.Add(_postProcessingMissingRow);

            // Sidebar and textures
            var mainRow = Row();
            mainRow.style.flexGrow = 1;
            content.Add(mainRow);

            _sidebar = new VisualElement { style = { width = SidebarWidth, flexShrink = 0 } };
            mainRow.Add(_sidebar);
            // Same style as Anti-aliasing: a label above a slider, without the value.
            _sidebar.Add(new Label(localize.Text(Phrases.vertical_displacement)));
            _verticalDisplacementSlider = new Slider(0f, 2f);
            _verticalDisplacementSlider.RegisterValueChangedCallback(e => VerticalDisplacement = e.newValue);
            _sidebar.Add(_verticalDisplacementSlider);
            _sidebar.Add(new Label(localize.Text(Phrases.rendering)) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            _postProcessingToggle = PrefsToggle(Phrases.post_processing, newValue => PostProcessing = newValue);
            _muteLightsToggle = PrefsToggle(Phrases.mute_lights_inside_object, newValue => MuteLightsInsideObject = newValue);
            _depthTextureToggle = PrefsToggle(Phrases.support_depth_texture, newValue => SupportDepthTexture = newValue);
            _alwaysRefreshToggle = PrefsToggle(Phrases.always_refresh, newValue => AlwaysRefresh = newValue);
            // Same style as Anti-aliasing: a label above the field.
            _ndmfPreviewGroup = new VisualElement { tooltip = localize.Text(Phrases.ndmf_preview_tooltip) };
            _ndmfPreviewGroup.Add(new Label(localize.Text(Phrases.ndmf_preview)));
            _ndmfPreviewPopup = new PopupField<LightboxViewerNdmfPreviewMode>(NdmfPreviewModes, NdmfPreviewMode, NdmfPreviewModeName, NdmfPreviewModeName);
            _ndmfPreviewPopup.RegisterValueChangedCallback(e => NdmfPreviewMode = e.newValue);
            _ndmfPreviewGroup.Add(_ndmfPreviewPopup);
            _sidebar.Add(_ndmfPreviewGroup);
            _antiAliasingLabel = new Label();
            _sidebar.Add(_antiAliasingLabel);
            _antiAliasingSlider = new SliderInt(0, 3);
            _antiAliasingSlider.RegisterValueChangedCallback(e => AntiAliasingSlider = e.newValue);
            _sidebar.Add(_antiAliasingSlider);
            var referenceCameraField = new ObjectField(localize.Text(Phrases.reference_camera)) { objectType = typeof(Camera), allowSceneObjects = true, bindingPath = nameof(referenceCamera) };
            _sidebar.Add(referenceCameraField);
            _realignButton = new Button(Realign) { text = localize.Text(Phrases.realign) };
            _sidebar.Add(_realignButton);

            _collectionsContainer = new VisualElement { style = { marginTop = 6 } };
            _collectionsContainer.Add(new Label(localize.Text(Phrases.collections)) { style = { unityFontStyleAndWeight = FontStyle.Bold } });
            _collectionsList = new VisualElement();
            _collectionsContainer.Add(_collectionsList);
            _sidebar.Add(_collectionsContainer);

            _textureArea = new VisualElement { style = { flexGrow = 1, flexBasis = 0, overflow = Overflow.Hidden } };
            _textureGrid = new VisualElement();
            _textureArea.Add(_textureGrid);
            _invalidSceneHelpBox = new HelpBox(localize.Text(Phrases.msg_invalid_lightbox_scene), HelpBoxMessageType.Error);
            _textureArea.Add(_invalidSceneHelpBox);
            mainRow.Add(_textureArea);

            // A horizontal scrollbar used to appear for no useful reason. The rows at the top (the two object fields, the camera roll slider) contain
            // labels and inputs with a minimum width, next to fixed-width elements (language selector, buttons), so they could be wider than the window,
            // and the ScrollView then offered to scroll to the part that was cut off. Let those fields shrink with the window, and never show the
            // horizontal scrollbar: the layout always adapts to the width of the window.
            content.Query<Label>(className: BaseField<float>.labelUssClassName).ForEach(label => label.style.minWidth = 0);
            objectField.style.minWidth = 0;
            sceneField.style.minWidth = 0;
            rollSlider.style.minWidth = 0;
            scroll.horizontalScrollerVisibility = ScrollerVisibility.Hidden;

            // The language selector is shared with the other tools that use HaiEFLoc, and is drawn with IMGUI.
            content.Add(new IMGUIContainer(() => localize.Selector(() => _localize = NewLoc())));

            root.Bind(_so);
            RefreshUI();
        }

        private ObjectField _sceneField;

        private static VisualElement Row()
        {
            return new VisualElement { style = { flexDirection = FlexDirection.Row } };
        }

        private static void Grow(VisualElement element)
        {
            element.style.flexGrow = 1;
            element.style.flexBasis = 0;
        }

        private Toggle PrefsToggle(string phrase, Action<bool> setterFn)
        {
            // Checkbox on the left and text on its right, like the Collections toggles.
            var toggle = new Toggle { text = localize.Text(phrase) };
            toggle.RegisterValueChangedCallback(e => setterFn(e.newValue));
            _sidebar.Add(toggle);
            return toggle;
        }

        // Writing a style or an enabled state marks the panel as needing a repaint, even when the value is the same,
        // so RefreshUI (called on every editor update) must only write when the value changes.
        private readonly System.Collections.Generic.Dictionary<Button, bool> _tinted = new System.Collections.Generic.Dictionary<Button, bool>();

        private void Tint(Button button, bool active, Color color)
        {
            if (_tinted.TryGetValue(button, out var previous) && previous == active) return;

            _tinted[button] = active;
            if (active) button.style.backgroundColor = color;
            else button.style.backgroundColor = StyleKeyword.Null;
        }

        private static void SetValue<T>(BaseField<T> field, T value)
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(field.value, value)) field.SetValueWithoutNotify(value);
        }

        private static void Enable(VisualElement element, bool enable)
        {
            if (element.enabledSelf != enable) element.SetEnabled(enable);
        }

        private static void Show(VisualElement element, bool visible)
        {
            var display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (element.style.display != display) element.style.display = display;
        }

        /// Brings the elements in line with the state, and passes the settings to the render queue. Called on every editor update.
        private void RefreshUI()
        {
            if (_uiRoot == null || _so == null) return;
            Profiler.BeginSample("LightboxViewer.EditorWindow.RefreshUI");

            Enable(_sceneField, !enabled);

            var rollIsZero = cameraRoll == 0;
            SetValue(_counterRotateToggle, CounterRotate);
            Enable(_rollResetButton, !rollIsZero);
            Tint(_rollResetButton, !rollIsZero, ActiveGreen);

            var invalidTargets = objectToView == null || lightboxScene == null || SceneManager.GetSceneAt(0).path == AssetDatabase.GetAssetPath(lightboxScene);
            var showPlayModeWarning = !invalidTargets && Application.isPlaying && !enabled;
            var showModified = !invalidTargets && !showPlayModeWarning && !Application.isPlaying && enabled && ProjectRenderQueue.SceneIsChanged();
            var showActivate = !showPlayModeWarning && !showModified;
            Show(_activateRow, showActivate);
            Show(_playModeRow, showPlayModeWarning);
            Show(_modifiedRow, showModified);
            Enable(_activateButton, !invalidTargets && !(objectToView == null || Application.isPlaying));
            Tint(_activateButton, enabled, ActiveRed);
            Tint(_modifiedActivateButton, enabled, ActiveRed);

            if (_advancedFoldout.value != advanced) _advancedFoldout.SetValueWithoutNotify(advanced);
            Show(_collapsedPostProcessingToggle, !advanced);
            SetValue(_collapsedPostProcessingToggle, PostProcessing);
            SetValue(_verticalDisplacementSlider, VerticalDisplacement);

            Show(_postProcessingMissingRow, _isBuiltInRenderPipeline && PplType == null);
            Enable(_installPostProcessingButton, !_ppInstall);

            Show(_sidebar, advanced);
            SetValue(_postProcessingToggle, PostProcessing);
            SetValue(_muteLightsToggle, MuteLightsInsideObject);
            SetValue(_depthTextureToggle, SupportDepthTexture);
            Show(_ndmfPreviewGroup, LightboxViewerNdmfPreview.IsAvailable);
            SetValue(_ndmfPreviewPopup, NdmfPreviewMode);
            SetValue(_alwaysRefreshToggle, AlwaysRefresh);
            var aaSlider = AntiAliasingSlider;
            var antiAliasingText = $"{localize.Text(Phrases.anti_aliasing)} ({RemapAntiAliasing(aaSlider)})";
            if (_antiAliasingLabel.text != antiAliasingText) _antiAliasingLabel.text = antiAliasingText;
            SetValue(_antiAliasingSlider, aaSlider);
            Enable(_realignButton, enabled);
            RefreshCollections();

            PassSettingsToRenderQueue();
            RefreshTextures();
            Profiler.EndSample();
        }

        private void RefreshCollections()
        {
            Show(_collectionsContainer, enabled);
            if (!enabled) return;

            var definition = ProjectRenderQueue.DefinitionNullable;
            if (definition != _collectionsDefinition)
            {
                _collectionsDefinition = definition;
                _collectionsList.Clear();
                _collectionToggles.Clear();
                if (definition != null)
                {
                    foreach (var group in definition.viewGroups)
                    {
                        var key = group.key;
                        var toggle = new Toggle { text = group.title };
                        toggle.RegisterValueChangedCallback(e => selected = key);
                        _collectionsList.Add(toggle);
                        _collectionToggles.Add((key, toggle));
                    }
                }
            }

            foreach (var (key, toggle) in _collectionToggles)
            {
                SetValue(toggle, key == selected);
            }
        }

        private void PassSettingsToRenderQueue()
        {
            if (objectToView == null) return;

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

        private void RefreshTextures()
        {
            var textures = ProjectRenderQueue.Textures();
            Show(_invalidSceneHelpBox, textures.Length == 0 && enabled);

            if (textures.Length == 0)
            {
                if (_shownTextures.Length != 0) ClearTextureGrid();
                return;
            }

            var availableWidth = _textureArea.contentRect.width;
            var availableHeight = _textureArea.contentRect.height;
            if (float.IsNaN(availableWidth) || float.IsNaN(availableHeight) || availableWidth <= 0 || availableHeight <= 0) return; // Not laid out yet

            int columns;
            int rows;
            if (textures.Length == 3)
            {
                columns = 3;
                rows = 1;
            }
            else
            {
                columns = Mathf.CeilToInt(Mathf.Sqrt(textures.Length));
                rows = 1 + (textures.Length - 1) / columns;
            }

            // The cells fill the whole area, with a gap between them but none on the right and bottom edges.
            // The render textures have sizes that are multiples of 8, so they can be a few pixels smaller than the cells they are stretched into.
            var actualWidth = Mathf.Max(16, Mathf.FloorToInt((availableWidth - CellGap * (columns - 1)) / columns));
            var actualHeight = Mathf.Max(16, Mathf.FloorToInt((availableHeight - CellGap * (rows - 1)) / rows));
            ProjectRenderQueue.Width(SanitizeTextureSize(actualWidth));
            ProjectRenderQueue.Height(SanitizeTextureSize(actualHeight));

            var unchanged = _shownCellWidth == actualWidth && _shownCellHeight == actualHeight && _shownTextures.Length == textures.Length;
            if (unchanged)
            {
                for (var i = 0; i < textures.Length; i++)
                {
                    if (_shownTextures[i] != textures[i]) { unchanged = false; break; }
                }
            }
            if (unchanged) return;

            var names = ProjectRenderQueue.Names();
            ClearTextureGrid();
            _shownTextures = (Texture[]) textures.Clone();
            _shownCellWidth = actualWidth;
            _shownCellHeight = actualHeight;
            for (var row = 0; row < rows; row++)
            {
                var rowElement = Row();
                for (var k = row * columns; k < Math.Min(textures.Length, row * columns + columns); k++)
                {
                    // Not an Image: it would be tinted by the Play Mode tint of the editor, and the lightboxes must be shown in true colors.
                    var cell = new LightboxViewerUntintedImage
                    {
                        texture = textures[k],
                        tooltip = names[k],
                        style =
                        {
                            width = actualWidth, height = actualHeight, flexShrink = 0,
                            marginLeft = k > row * columns ? CellGap : 0, marginTop = row > 0 ? CellGap : 0,
                            borderTopWidth = 1, borderBottomWidth = 1, borderLeftWidth = 1, borderRightWidth = 1,
                            borderTopColor = new Color(0.15f, 0.15f, 0.15f), borderBottomColor = new Color(0.15f, 0.15f, 0.15f),
                            borderLeftColor = new Color(0.15f, 0.15f, 0.15f), borderRightColor = new Color(0.15f, 0.15f, 0.15f)
                        }
                    };
                    _cells.Add(cell);
                    rowElement.Add(cell);
                }
                _textureGrid.Add(rowElement);
            }
        }

        private void ClearTextureGrid()
        {
            _textureGrid.Clear();
            _cells.Clear();
            _shownTextures = new Texture[0];
            _shownCellWidth = 0;
            _shownCellHeight = 0;
        }

        /// The render textures were rendered again: their content changed, but not the texture objects.
        private void MarkTexturesDirty()
        {
            foreach (var cell in _cells)
            {
                cell.MarkDirtyRepaint();
            }
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

        private int _localizationSignature;

        /// The UI is built once, so it is built again when the language changes: from the selector of this window, from the selector of
        /// another tool that shares the same language preference, or when the localization files are reloaded with the refresh button.
        private void RefreshLocalization()
        {
            localize.RefreshIfNecessary();
            if (_uiRoot == null) return;
            if (LocalizationSignature() != _localizationSignature) CreateGUI();
        }

        private static readonly string[] AllPhrases = typeof(Phrases)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue())
            .ToArray();

        private static int LocalizationSignature()
        {
            var signature = 17;
            foreach (var phrase in AllPhrases)
            {
                signature = signature * 31 + localize.Text(phrase).GetHashCode();
            }
            return signature;
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