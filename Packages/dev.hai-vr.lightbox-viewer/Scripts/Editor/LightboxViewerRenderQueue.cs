﻿using System.Collections.Generic;
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
        // TODO: We may need to set this to false on older versions of Unity, as ObjectChangeEvent was added in Unity 2021.
        internal const bool WhenInEditMode_ReuseCachedCopy = true;
        internal const bool WhenInEditMode_DestroyAllMonoBehaviours = true;
        
        internal const bool WhenInEditMode_DoNotDisableMainAvatar = true;
        
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

        private bool _searchedForDefinition;
        private bool _foundDefinition;
        private GameObject _previousOriginalObject;
        private GameObject _copiedObject;
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
            var needsDispose = ShouldDisposeDueToChange(stream);
            if (needsDispose)
            {
                DisposeOfCopiedObject();
            }
        }

        private bool ShouldDisposeDueToChange(ObjectChangeEventStream stream)
        {
            var originalAvatarRoot = _previousOriginalObject.transform;
            for (var i = 0; i < stream.length; i++)
            {
                var eventType = stream.GetEventType(i);
                if (eventType == ObjectChangeKind.ChangeGameObjectOrComponentProperties)
                {
                    stream.GetChangeGameObjectOrComponentPropertiesEvent(i, out var data);
                    var instance = EditorUtility.InstanceIDToObject(data.instanceId);

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
            _previousOriginalObject = null;
        }

        private static GameObject CreateCopy(GameObject originalAvatarGo)
        {
            var copy = new GameObject
            {
                name = "LightboxAvatarHolder",
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
            
            // Animators are slow because they rebind when the object is re-enabled. Don't bother doing this.
            var allAnimators = innerCopy.GetComponentsInChildren<Animator>(true);
            foreach (var animator in allAnimators)
            {
                Object.DestroyImmediate(animator);
            }
            
            innerCopy.SetActive(true);

            copy.hideFlags = HideFlags.HideAndDontSave;
            
            return copy;
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
            Profiler.EndSample();

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
                Profiler.EndSample();
                Object.DestroyImmediate(ourDepthEnabler);
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
                viewer.Begin(copy, _roll, _counterRotate, _cameraOptional, _postProcessing);

                if (!Application.isPlaying)
                {
                    Profiler.BeginSample("LightboxViewer.TrueRender.PreventFlickering");
                    // Fixes a problem where the avatar is flickering
                    copy.gameObject.SetActive(false);
                    copy.gameObject.SetActive(true);
                    Profiler.EndSample();
                }

                var itemCount = 0;
                var renderTexture = RenderTexture.GetTemporary(_lightboxIndexToTexture[0].width, _lightboxIndexToTexture[0].height, 24);
                var allApplicableLightboxes = AllApplicableLightboxes();
                while (_queue.Count > 0 && itemCount < _queueSize)
                {
                    var lightboxIndex = _queue.Dequeue();
                    if (allApplicableLightboxes.Length > lightboxIndex)
                    {
                        var currentLightbox = allApplicableLightboxes[lightboxIndex];
                        currentLightbox.gameObject.SetActive(true);
                        viewer.RenderNoAnimator(_lightboxIndexToTexture[lightboxIndex], currentLightbox.gameObject, renderTexture, _referentialVector, _referentialQuaternion, _verticalDisplacement);
                        currentLightbox.gameObject.SetActive(false);

                        itemCount++;
                    }
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
                EditorSceneManager.CloseScene(_openScene, true);
                LightProbes.Tetrahedralize();
                _sceneLoaded = false;
                _openScene = default;
            }

            DisposeOfCopiedObject();
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