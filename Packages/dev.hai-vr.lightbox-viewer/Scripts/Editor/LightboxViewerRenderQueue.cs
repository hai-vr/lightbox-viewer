using System.Collections.Generic;
using System.Linq;
using Hai.LightboxViewer.Scripts.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    public class LightboxViewerRenderQueue
    {
        private const string DefaultLightingDataAssetPath = "Resources/unity_builtin_extra";
        
        // TODO: We may need to set this to false on older versions of Unity, as ObjectChangeEvent was added in Unity 2021.
        internal const bool WhenInEditMode_ReuseCachedCopy = true;
        internal const bool WhenInEditMode_DestroyAllMonoBehaviours = true;

        internal const bool WhenInEditMode_DoNotDisableMainAvatar = true;

        private readonly Dictionary<int, Texture> _lightboxIndexToTexture;
        private readonly Queue<int> _queue;
        private int _queueSize;
        private Scene _openScene;
        private bool _sceneLoaded;
        private bool _wasUsingDefaultLightingData;
        private LightingDataAsset _previousLightingData;
        private Texture[] _textures = new Texture[1];
        private string[] _names = new string[1];
        private int _width = 512;
        private int _height = 512;
        private float _roll;
        private LightboxViewerGenerator _previousViewer;
        private bool _counterRotate;
        private Vector3 _referentialVector;
        private Quaternion _referentialQuaternion;
        private Camera _cameraOptional;
        private Camera _sceneCamera;
        private bool _postProcessing;
        private int _antiAliasing = 2;
        private float _verticalDisplacement;
        private bool _muteLightsInsideObject;
        private bool _enableDepthTexture;
        private GameObject _depthEnabler;
        private string _selected;

        private bool _searchedForDefinition;
        private bool _foundDefinition;
        private GameObject _previousOriginalObject;
        private GameObject _copiedObject;
        private readonly Dictionary<Renderer, Renderer> _copiedRenderers = new Dictionary<Renderer, Renderer>();

        // Reused across renders, instead of being created and destroyed for every render.
        private Camera _reusableCamera;
        private bool _reusableCameraIsCopyOfReference;
        private Camera _reusableCameraReference;
        private bool _reusableCameraHasPostProcessing;
        private bool _reusableCameraNeedsRecreation;
        private GameObject _reusableDepthEnabler;
        private Material _reusableCounterRollMaterial;

        // When false, the lightboxes are only rendered again when something may have changed.
        private bool _needsRerender = true;
        private GameObject _lastRequestedRoot;
        private SceneViewpoint _lastViewpoint;

        // All lights and reflection probes in the open scenes, including inactive ones. Whether they are active and enabled
        // is checked for every render, so the cache only needs refreshing when objects are created, destroyed, or loaded.
        private const double ConflictCandidatesMaxAgeSeconds = 1.0;
        private readonly List<Light> _lightCandidates = new List<Light>();
        private readonly List<ReflectionProbe> _reflectionProbeCandidates = new List<ReflectionProbe>();
        private bool _conflictCandidatesNeedRefresh = true;
        private double _conflictCandidatesRefreshTime;

        public LightboxViewerDefinition DefinitionNullable { get; private set; }

        // Setters. Settings that affect the render request a new render when their value changes.
        // Width, height, and anti-aliasing do not need to, because a change creates new textures, which are always rendered.
        public void QueueSize(int queueSize) => _queueSize = queueSize;
        public void Roll(float roll) => SetAndRequestRerender(ref _roll, roll);
        public void Camera(Camera camera) => SetAndRequestRerender(ref _cameraOptional, camera);
        public void PostProcessing(bool postProcessing) => SetAndRequestRerender(ref _postProcessing, postProcessing);
        public void AntiAliasing(int antiAliasing) => _antiAliasing = antiAliasing;
        public void Width(int actualWidth) => _width = actualWidth;
        public void Height(int actualHeight) => _height = actualHeight;
        public void CounterRotate(bool counterRotate) => SetAndRequestRerender(ref _counterRotate, counterRotate);
        public void VerticalDisplacement(float verticalDisplacement) => SetAndRequestRerender(ref _verticalDisplacement, verticalDisplacement);
        public void MuteLightsInsideObject(bool muteLightsInsideObject) => SetAndRequestRerender(ref _muteLightsInsideObject, muteLightsInsideObject);
        public void Selected(string selected) => SetAndRequestRerender(ref _selected, selected);
        public void EnableDepthTexture(bool enableDepthTexture, GameObject depthEnabler)
        {
            SetAndRequestRerender(ref _enableDepthTexture, enableDepthTexture);
            SetAndRequestRerender(ref _depthEnabler, depthEnabler);
        }

        private void SetAndRequestRerender<T>(ref T field, T value)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;

            field = value;
            _needsRerender = true;
        }

        public void RequestRerender() => _needsRerender = true;

        /// Returns true if the lightboxes need to be rendered again because something may have changed since the last call:
        /// a setting, an object or asset change, an undo or redo, the object to view, or the Scene view camera.
        public bool ConsumeNeedsRerender(GameObject root)
        {
            if (root != _lastRequestedRoot)
            {
                _lastRequestedRoot = root;
                _needsRerender = true;
            }

            var sceneCamera = LightboxViewerGenerator.FindSceneViewCameraOrNull();
            if (sceneCamera != null)
            {
                var viewpoint = new SceneViewpoint(sceneCamera);
                if (!viewpoint.IsSameAs(_lastViewpoint))
                {
                    _lastViewpoint = viewpoint;
                    _needsRerender = true;
                }
            }

            var result = _needsRerender;
            _needsRerender = false;
            return result;
        }

        private readonly struct SceneViewpoint
        {
            private readonly bool _isValid;
            private readonly Vector3 _position;
            private readonly Quaternion _rotation;
            private readonly float _fieldOfView;
            private readonly bool _orthographic;
            private readonly float _orthographicSize;
            private readonly float _nearClipPlane;
            private readonly float _farClipPlane;
            private readonly int _pixelWidth;
            private readonly int _pixelHeight;

            public SceneViewpoint(Camera camera)
            {
                _isValid = true;
                var t = camera.transform;
                _position = t.position;
                _rotation = t.rotation;
                _fieldOfView = camera.fieldOfView;
                _orthographic = camera.orthographic;
                _orthographicSize = camera.orthographicSize;
                _nearClipPlane = camera.nearClipPlane;
                _farClipPlane = camera.farClipPlane;
                _pixelWidth = camera.pixelWidth;
                _pixelHeight = camera.pixelHeight;
            }

            public bool IsSameAs(SceneViewpoint other)
            {
                // Exact comparisons are intended: any movement of the Scene view camera should cause a new render.
                return _isValid && other._isValid
                       && _position == other._position
                       && _rotation == other._rotation
                       && _fieldOfView == other._fieldOfView
                       && _orthographic == other._orthographic
                       && _orthographicSize == other._orthographicSize
                       && _nearClipPlane == other._nearClipPlane
                       && _farClipPlane == other._farClipPlane
                       && _pixelWidth == other._pixelWidth
                       && _pixelHeight == other._pixelHeight;
            }
        }

        // Getters
        public bool SceneIsChanged() => _openScene.isDirty;
        public Texture[] Textures() => _textures;
        public string[] Names() => _names;

        public LightboxViewerRenderQueue()
        {
            _lightboxIndexToTexture = new Dictionary<int, Texture>();
            _queue = new Queue<int>();

            // The reusable objects are not saved with the scene, so they would survive a domain reload as leaked objects
            // once this instance is lost. Destroy them before it happens.
            AssemblyReloadEvents.beforeAssemblyReload += DestroyReusableObjects;

            // Other changes are detected through object change events. Changes that publish no event at all are caught by
            // the maximum age of the cache. EditorApplication.hierarchyChanged is not used, because the toggling of
            // objects during each render might raise it, which would defeat the cache.
            EditorApplication.playModeStateChanged += _ => InvalidateConflictCandidates();

            // Undo and redo, and changes to assets on disk (e.g. a shader or a texture being reimported), may change the render.
            Undo.undoRedoPerformed += RequestRerender;
            EditorApplication.projectChanged += RequestRerender;
            EditorSceneManager.sceneOpened += (_, _) => InvalidateConflictCandidates();
            EditorSceneManager.sceneClosed += _ => InvalidateConflictCandidates();
            SceneManager.sceneLoaded += (_, _) => InvalidateConflictCandidates();
            SceneManager.sceneUnloaded += _ => InvalidateConflictCandidates();
        }

        private void InvalidateConflictCandidates()
        {
            _conflictCandidatesNeedRefresh = true;
        }

        private void RefreshConflictCandidatesIfNeeded()
        {
            var now = EditorApplication.timeSinceStartup;

            // Scripts can create lights, or add Light components to existing objects, without publishing any event.
            if (now - _conflictCandidatesRefreshTime > ConflictCandidatesMaxAgeSeconds)
            {
                _conflictCandidatesNeedRefresh = true;
            }

            if (!_conflictCandidatesNeedRefresh) return;

            Profiler.BeginSample("LightboxViewer.Render.RefreshConflictCandidates");
            _lightCandidates.Clear();
            _reflectionProbeCandidates.Clear();
#if UNITY_6000_5_OR_NEWER
            _lightCandidates.AddRange(Object.FindObjectsByType<Light>(FindObjectsInactive.Include));
            _reflectionProbeCandidates.AddRange(Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include));
#else
            _lightCandidates.AddRange(Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            _reflectionProbeCandidates.AddRange(Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None));
#endif
            Profiler.EndSample();

            _conflictCandidatesNeedRefresh = false;
            _conflictCandidatesRefreshTime = now;
        }

        private Texture RequireRender(int lightboxIndex, int width, int height, bool rerenderExisting)
        {
            if (_lightboxIndexToTexture.ContainsKey(lightboxIndex)
                && _lightboxIndexToTexture[lightboxIndex] != null // Can happen when the texture is destroyed (Unity invalid object)
                && _lightboxIndexToTexture[lightboxIndex].width == width
                && _lightboxIndexToTexture[lightboxIndex].height == height
                && ((RenderTexture)_lightboxIndexToTexture[lightboxIndex]).antiAliasing == _antiAliasing)
            {
                if (rerenderExisting && !_queue.Contains(lightboxIndex))
                {
                    _queue.Enqueue(lightboxIndex);
                }
                return _lightboxIndexToTexture[lightboxIndex];
            }

            if (_lightboxIndexToTexture.ContainsKey(lightboxIndex))
            {
                Object.DestroyImmediate(_lightboxIndexToTexture[lightboxIndex]);
            }
            
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            texture.antiAliasing = _antiAliasing;
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

            // Without a Scene view, there is no viewpoint to render from. Keep the queue, so that rendering resumes
            // once a Scene view is open.
            _sceneCamera = LightboxViewerGenerator.FindSceneViewCameraOrNull();
            if (_sceneCamera == null) return false;

            // Handle weird case of exiting Play Mode while activated
            if (!_searchedForDefinition || _foundDefinition && DefinitionNullable == null)
            {
                DefinitionNullable = GetDefinitionOrNull();
                _foundDefinition = DefinitionNullable != null;
                _searchedForDefinition = true;
            }

            if (Application.isPlaying)
            {
                PlayModeRender(root);
            }
            else
            {
                EditModeRender(root);
            }

            return true;
        }

        private void PlayModeRender(GameObject root)
        {
            var pos = root.transform.position;
            var rot = root.transform.rotation;
            var scale = root.transform.localScale;
            try
            {
                Profiler.BeginSample("LightboxViewer.Render");
                Render(root);
                Profiler.EndSample();
            }
            finally
            {
                root.transform.position = pos;
                root.transform.rotation = rot;
                root.transform.localScale = scale;
            }
        }

        private void EditModeRender(GameObject originalAvatarGo)
        {
            if (WhenInEditMode_ReuseCachedCopy)
            {
                if (_copiedObject == null || _previousOriginalObject != originalAvatarGo)
                {
                    DisposeOfCopiedObject();

                    _copiedObject = CreateCopy(originalAvatarGo);
                    InvalidateConflictCandidates(); // Lights inside the copy must be known when muting lights inside the object.

                    _previousOriginalObject = originalAvatarGo;
                }
            }

            GameObject copy;
            var wasActive = originalAvatarGo.activeSelf;
            try
            {
                Profiler.BeginSample("LightboxViewer.TryRender.PreventiveCopying");
                if (WhenInEditMode_ReuseCachedCopy)
                {
                    copy = _copiedObject;
                }
                else if (WhenInEditMode_DestroyAllMonoBehaviours)
                {
                    // Parent the copy to an inactive object during instantiation, so that we can delete all MonoBehaviours
                    // without triggering their OnEnable and OnDestroy functions
                    copy = CreateCopy(originalAvatarGo);
                }
                else
                {
                    copy = Object.Instantiate(originalAvatarGo);
                }
                Profiler.EndSample();

                Profiler.BeginSample("LightboxViewer.TryRender.SettingCopyActive");
                copy.SetActive(true);
                Profiler.EndSample();

                if (!WhenInEditMode_DoNotDisableMainAvatar)
                {
                    Profiler.BeginSample("LightboxViewer.TryRender.SettingMainInactive");
                    originalAvatarGo.SetActive(false);
                    Profiler.EndSample();
                }

                Profiler.BeginSample("LightboxViewer.Render");
                Render(copy);
                Profiler.EndSample();
            }
            finally
            {
                if (!WhenInEditMode_DoNotDisableMainAvatar)
                {
                    Profiler.BeginSample("LightboxViewer.TryRender.SettingMainActive");
                    if (wasActive) originalAvatarGo.SetActive(true);
                    Profiler.EndSample();
                }

                if (!WhenInEditMode_ReuseCachedCopy)
                {
                    Profiler.BeginSample("LightboxViewer.TryRender.DestroyingCopy");
                    if (copy != null) Object.DestroyImmediate(copy);
                    Profiler.EndSample();
                }
            }
        }

        public void ObjectChangeEvent(ref ObjectChangeEventStream stream)
        {
            // Any change, including to material properties, may change the render.
            if (stream.length > 0) _needsRerender = true;

            var needsDispose = ShouldDisposeDueToChange(stream);
            if (needsDispose)
            {
                DisposeOfCopiedObject();
            }

            if (ShouldRefreshConflictCandidatesDueToChange(stream))
            {
                InvalidateConflictCandidates();
            }

            if (ShouldRecreateCameraDueToChange(stream))
            {
                _reusableCameraNeedsRecreation = true;
            }
        }

        private bool ShouldRecreateCameraDueToChange(ObjectChangeEventStream stream)
        {
            // A camera that is not a copy of a reference camera has nothing to carry over.
            if (_reusableCamera == null || !_reusableCameraIsCopyOfReference || _reusableCameraReference == null) return false;

            var referenceRoot = _reusableCameraReference.transform;
            for (var i = 0; i < stream.length; i++)
            {
                var eventType = stream.GetEventType(i);
                if (eventType == ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                    var instance = InstanceOf(data);
                    var isPartOfReference = instance is GameObject go && FirstIsAnyParentOfSecond(referenceRoot, go.transform)
                                            || instance is Component component && FirstIsAnyParentOfSecond(referenceRoot, component.transform);
                    if (isPartOfReference) return true;
                }
                else if (eventType == ObjectChangeKind.ChangeAssetObjectProperties)
                {
                    // Assets are shared between the reference camera and its copy (e.g. post-processing profiles).
                }
                else
                {
                    // Structural changes may have added or removed components on the reference camera.
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldRefreshConflictCandidatesDueToChange(ObjectChangeEventStream stream)
        {
            for (var i = 0; i < stream.length; i++)
            {
                var eventType = stream.GetEventType(i);

                // Property changes, including enabling or disabling lights and objects, are already taken into account
                // when rendering. Other changes, such as adding or removing components, may change which lights exist.
                if (eventType != ObjectChangeKind.ChangeGameObjectOrComponentProperties
                    && eventType != ObjectChangeKind.ChangeAssetObjectProperties)
                {
                    return true;
                }
            }

            return false;
        }

        private static Object InstanceOf(ChangeGameObjectOrComponentPropertiesEventArgs data)
        {
#if UNITY_6000_2_OR_NEWER
            return EditorUtility.EntityIdToObject(data.entityId);
#else
            return EditorUtility.InstanceIDToObject(data.instanceId);
#endif
        }

        private bool ShouldDisposeDueToChange(ObjectChangeEventStream stream)
        {
            if (_previousOriginalObject == null) return false; // Happens in Play Mode
            
            var originalAvatarRoot = _previousOriginalObject.transform;
            for (var i = 0; i < stream.length; i++)
            {
                var eventType = stream.GetEventType(i);
                if (eventType == ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                    var instance = InstanceOf(data);

                    var isPartOfAvatar = instance is GameObject go && FirstIsAnyParentOfSecond(originalAvatarRoot, go.transform)
                                         || instance is Component component && FirstIsAnyParentOfSecond(originalAvatarRoot, component.transform);

                    if (isPartOfAvatar)
                    {
                        if (instance is not Transform)
                        {
                            return true;
                        }
                        if (instance != originalAvatarRoot)
                        {
                            // TODO: Figure out a way to carry over changes in child transforms without having to dispose.
                            return true;
                        }
                    }
                }
                else if (eventType == ObjectChangeKind.ChangeAssetObjectProperties)
                {
                    // The copy references the same assets as the original (materials, textures, meshes...),
                    // so changes to assets are already visible in the copy, and there is no need to dispose of it.
                    // This is important because editing material properties publishes this event every time.
                    continue;
                }
                else
                {
                    // TODO: This also causes dispose if things happen outside of the avatar. This could be narrowed down.
                    return true;
                }
            }

            return false;
        }

        private void DisposeOfCopiedObject()
        {
            if (_copiedObject == null) return;

            Object.DestroyImmediate(_copiedObject);
            _copiedObject = null;
            _copiedRenderers.Clear();
            InvalidateConflictCandidates();
            _previousOriginalObject = null;
        }

        /// Creates the copy of the object that is rendered in Edit Mode. See LightboxViewerCopyBuilder.
        private GameObject CreateCopy(GameObject originalAvatarGo)
        {
            return LightboxViewerCopyBuilder.Build(originalAvatarGo, _copiedRenderers);
        }

        private void Render(GameObject copy)
        {
            if (!Application.isPlaying && WhenInEditMode_ReuseCachedCopy)
            {
                copy.transform.localPosition = _previousOriginalObject.transform.localPosition;
                copy.transform.localRotation = _previousOriginalObject.transform.localRotation;
                copy.transform.localScale = _previousOriginalObject.transform.localScale;
            }

            Profiler.BeginSample("LightboxViewer.Render.DisableLightboxes");
            var history = RecordDisableLightboxes();
            Profiler.EndSample();

            Profiler.BeginSample("LightboxViewer.Render.ResolveConflictingObjects");
            RefreshConflictCandidatesIfNeeded();
            var all = new List<Behaviour>();
            foreach (var that in _lightCandidates)
            {
                if (that != null // The cache may contain lights destroyed since the last refresh.
                    && that.isActiveAndEnabled
                    && that.gameObject.scene != _openScene
                    && (_muteLightsInsideObject || !FirstIsAnyParentOfSecond(copy.transform, that.transform)))
                {
                    all.Add(that);
                }
            }
            foreach (var that in _reflectionProbeCandidates)
            {
                if (that != null && that.isActiveAndEnabled && that.gameObject.scene != _openScene) all.Add(that);
            }
            Profiler.EndSample();

            // Only objects that we deactivate get reactivated afterwards, so that objects which were already inactive
            // stay inactive. Null entries (e.g. a deleted object still listed in the definition) are skipped, otherwise
            // the exception would prevent the scene lights and lightboxes from being restored.
            var deactivatedHideInRenders = new List<GameObject>();
            if (DefinitionNullable != null)
            {
                foreach (var hideInRender in DefinitionNullable.hideInRenders)
                {
                    if (hideInRender != null && hideInRender.activeSelf)
                    {
                        hideInRender.SetActive(false);
                        deactivatedHideInRenders.Add(hideInRender);
                    }
                }
            }

            // The depth enabler is a light, so it must only be active during the render. Otherwise, it would affect
            // the scene. It was inactive when the conflicting lights were collected, so it will not be disabled with them.
            var depthEnabler = ObtainDepthEnablerOrNull();
            try
            {
                if (depthEnabler != null) depthEnabler.SetActive(true);

                Profiler.BeginSample("LightboxViewer.Render.DisableConflictingObjects");
                foreach (var it in all) it.enabled = false;
                Profiler.EndSample();

                Profiler.BeginSample("LightboxViewer.Render.TrueRender");
                TrueRender(copy);
                Profiler.EndSample();
            }
            finally
            {
                Profiler.BeginSample("LightboxViewer.Render.RestoreConflictingObjects");
                foreach (var hideInRender in deactivatedHideInRenders)
                {
                    if (hideInRender != null) hideInRender.SetActive(true);
                }
                foreach (var it in all) it.enabled = true;
                var lightboxes = AllLightboxes();
                for (var index = 0; index < lightboxes.Length; index++)
                {
                    lightboxes[index].gameObject.SetActive(history[index]);
                }
                Profiler.EndSample();
                if (depthEnabler != null) depthEnabler.SetActive(false);
            }

            if (!Application.isPlaying && WhenInEditMode_ReuseCachedCopy)
            {
                copy.transform.localPosition = Vector3.down * 10_000;
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
                var camera = ObtainReusableCamera();
                var counterRollMaterial = LightboxViewerGenerator.NeedsCounterRoll(_roll, _counterRotate) ? ObtainCounterRollMaterial() : null;
                viewer.Begin(copy, camera, _sceneCamera, _roll, _counterRotate, counterRollMaterial, _reusableCameraIsCopyOfReference, _antiAliasing);

                if (!Application.isPlaying)
                {
                    Profiler.BeginSample("LightboxViewer.TrueRender.PreventFlickering");
                    // Fixes a problem where the avatar is flickering
                    copy.gameObject.SetActive(false);
                    copy.gameObject.SetActive(true);
                    Profiler.EndSample();
                }

                var itemCount = 0;
                var allApplicableLightboxes = AllApplicableLightboxes();
                while (_queue.Count > 0 && itemCount < _queueSize)
                {
                    var lightboxIndex = _queue.Dequeue();
                    if (allApplicableLightboxes.Length > lightboxIndex)
                    {
                        var currentLightbox = allApplicableLightboxes[lightboxIndex];
                        currentLightbox.gameObject.SetActive(true);
                        viewer.RenderNoAnimator(_lightboxIndexToTexture[lightboxIndex], currentLightbox.gameObject, _referentialVector, _referentialQuaternion, _verticalDisplacement);
                        currentLightbox.gameObject.SetActive(false);

                        itemCount++;
                    }
                }
            }
            finally
            {
                viewer.Terminate();
            }

            _previousViewer = viewer;
        }

        private Camera ObtainReusableCamera()
        {
            var wantsCopyOfReference = _cameraOptional != null;
            var wantsPostProcessing = _postProcessing && LightboxViewerEditorWindow.PplType != null;

            var needsRecreation = _reusableCamera == null
                                  || _reusableCameraNeedsRecreation
                                  || _reusableCameraIsCopyOfReference != wantsCopyOfReference
                                  || _reusableCameraReference != _cameraOptional
                                  || _reusableCameraHasPostProcessing != wantsPostProcessing
                                  // In Play Mode, scripts may change the reference camera without publishing change events,
                                  // so the copy is recreated for every render, like it was before.
                                  || Application.isPlaying && wantsCopyOfReference;

            if (needsRecreation)
            {
                DestroyReusableCamera();
                _reusableCamera = LightboxViewerGenerator.CreateReusableCamera(_cameraOptional, wantsPostProcessing);
                _reusableCameraIsCopyOfReference = wantsCopyOfReference;
                _reusableCameraReference = _cameraOptional;
                _reusableCameraHasPostProcessing = wantsPostProcessing;
                _reusableCameraNeedsRecreation = false;
            }

            return _reusableCamera;
        }

        private Material ObtainCounterRollMaterial()
        {
            if (_reusableCounterRollMaterial == null)
            {
                _reusableCounterRollMaterial = LightboxViewerGenerator.CreateCounterRollMaterial();
            }

            return _reusableCounterRollMaterial;
        }

        private GameObject ObtainDepthEnablerOrNull()
        {
            if (!_enableDepthTexture || _depthEnabler == null)
            {
                DestroyReusableDepthEnabler();
                return null;
            }

            if (_reusableDepthEnabler == null)
            {
                _reusableDepthEnabler = Object.Instantiate(_depthEnabler);
                _reusableDepthEnabler.SetActive(false);
                foreach (var t in _reusableDepthEnabler.GetComponentsInChildren<Transform>(true))
                {
                    t.gameObject.hideFlags = HideFlags.HideAndDontSave;
                }
            }

            return _reusableDepthEnabler;
        }

        private void DestroyReusableCamera()
        {
            if (_reusableCamera != null) Object.DestroyImmediate(_reusableCamera.gameObject);
            _reusableCamera = null;
        }

        private void DestroyReusableDepthEnabler()
        {
            if (_reusableDepthEnabler != null) Object.DestroyImmediate(_reusableDepthEnabler);
            _reusableDepthEnabler = null;
        }

        private void DestroyReusableObjects()
        {
            DestroyReusableCamera();
            DestroyReusableDepthEnabler();
            if (_reusableCounterRollMaterial != null) Object.DestroyImmediate(_reusableCounterRollMaterial);
            _reusableCounterRollMaterial = null;
        }

        public void LoadLightbox(SceneAsset lightbox)
        {
            _wasUsingDefaultLightingData = IsUsingDefaultLightingData();
            if (_wasUsingDefaultLightingData)
            {
                _previousLightingData = Lightmapping.lightingDataAsset;
                Lightmapping.lightingDataAsset = null;
            }
            _openScene = EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(lightbox), OpenSceneMode.Additive);
            InvalidateConflictCandidates();
            DefinitionNullable = GetDefinitionOrNull();
            _foundDefinition = DefinitionNullable != null;
            _searchedForDefinition = true;
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
                if (_wasUsingDefaultLightingData)
                {
                    Lightmapping.lightingDataAsset = _previousLightingData;
                }
                EditorSceneManager.CloseScene(_openScene, true);
                LightProbes.Tetrahedralize();
                _sceneLoaded = false;
                _openScene = default;
            }

            DisposeOfCopiedObject();
            DestroyAllTextures();
            DestroyReusableObjects();
            _lightCandidates.Clear();
            _reflectionProbeCandidates.Clear();
            InvalidateConflictCandidates();
        }

        private void DestroyAllTextures()
        {
            foreach (var texture in _lightboxIndexToTexture.Values)
            {
                if (texture != null) Object.DestroyImmediate(texture);
            }
            _lightboxIndexToTexture.Clear();
            _queue.Clear();
            _textures = new Texture[0];
            _names = new string[0];
        }

        private void DestroyTexturesFromIndex(int count)
        {
            // Keys are always contiguous from 0, so this only happens when switching to a group with fewer lightboxes.
            if (_lightboxIndexToTexture.Count <= count) return;

            foreach (var index in _lightboxIndexToTexture.Keys.Where(index => index >= count).ToList())
            {
                var texture = _lightboxIndexToTexture[index];
                if (texture != null) Object.DestroyImmediate(texture);
                _lightboxIndexToTexture.Remove(index);
            }
        }

        /// Ensures there is a texture of the current size for each lightbox. Lightboxes with a new texture are always queued for
        /// rendering. Lightboxes that already have a texture are only queued when rerenderExisting is true.
        public void ForceRequireRenderAll(bool rerenderExisting = true)
        {
            var lightboxes = AllApplicableLightboxes();
            if (_textures.Length != lightboxes.Length)
            {
                _textures = new Texture[lightboxes.Length];
                _names = new string[lightboxes.Length];
            }
            for (var i = 0; i < lightboxes.Length; i++)
            {
                _textures[i] = RequireRender(i, _width, _height, rerenderExisting);
                _names[i] = lightboxes[i].name;
            }
            DestroyTexturesFromIndex(lightboxes.Length);
        }

        public void SaveLightbox()
        {
            EditorSceneManager.SaveScene(_openScene);
        }

        public void Referential(Vector3 referentialVector, Quaternion referentialQuaternion)
        {
            _referentialVector = referentialVector;
            _referentialQuaternion = referentialQuaternion;
            _needsRerender = true;
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

        private static bool IsUsingDefaultLightingData()
        {
            var lightingDataAsset = Lightmapping.lightingDataAsset;
            if (lightingDataAsset != null)
            {
                var assetPath = AssetDatabase.GetAssetPath(lightingDataAsset);
                return assetPath == DefaultLightingDataAssetPath;
            }

            return false;
        }
    }
}
