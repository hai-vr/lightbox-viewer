using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
#if LIGHTBOXVIEWER_NDMF_SUPPORTED
using System.Reflection;
using nadena.dev.ndmf.preview;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
#endif

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    /// Uses the result of NDMF Preview (if NDMF is installed, and NDMF Preview is enabled) as the thing to render.
    ///
    /// NDMF Preview does not modify the objects of the scene. It creates proxy renderers in a hidden scene, and hides the original renderers
    /// when a camera draws. Lightbox Viewer renders a copy of the object, so the proxies are never used by it.
    /// To use the preview result, the mesh and the materials of each proxy are applied onto the equivalent renderer of the copy.
    /// How the result of NDMF Preview is rendered. The values are stored in the preferences, do not change them.
    internal enum LightboxViewerNdmfPreviewMode
    {
        /// The original object is rendered, without the result of NDMF Preview.
        Disabled = 0,
        /// Only the complete result that NDMF Preview shows is rendered, and nothing is rendered while NDMF Preview computes a new result.
        SlowUpdates = 1,
        /// The proxies of NDMF Preview are rendered as soon as they change, including those of a result that is still being computed.
        AggressiveUpdates = 2,
    }

    internal static class LightboxViewerNdmfPreview
    {
#if LIGHTBOXVIEWER_NDMF_SUPPORTED
        internal static bool IsInstalled => true;

        /// NDMF Preview is enabled when it has a current preview session (it has none when disabled in the NDM Framework menu, or in Play Mode).
        internal static bool IsPreviewEnabled => PreviewSession.Current != null;

        private static readonly List<Renderer> ProxyRenderers = new List<Renderer>();

        /// Returns a value that changes when the preview result of the objects under the root changes.
        internal static int ComputeSignature(GameObject originalRoot, bool activeResultOnly)
        {
            var signature = 17;
            ForEachProxy(originalRoot, activeResultOnly, (original, proxy) =>
            {
                signature = signature * 31 + HashOf(original);
                signature = signature * 31 + HashOf(GetMesh(proxy));
                foreach (var material in proxy.sharedMaterials)
                {
                    signature = signature * 31 + HashOf(material);
                }
            });
            return signature;
        }

        /// Applies the preview result of the objects under the original root onto the equivalent renderers of the copy.
        /// The copied renderers are the renderer of the copy for each original renderer.
        internal static void Apply(GameObject originalRoot, IReadOnlyDictionary<Renderer, Renderer> copiedRenderers, bool activeResultOnly)
        {
            ForEachProxy(originalRoot, activeResultOnly, (original, proxy) =>
            {
                var originalRenderer = original.GetComponent<Renderer>();
                if (originalRenderer == null || !copiedRenderers.TryGetValue(originalRenderer, out var target) || target == null) return;

                if (proxy is SkinnedMeshRenderer proxySmr && target is SkinnedMeshRenderer targetSmr)
                {
                    ApplySkinned(proxySmr, targetSmr);
                }
                else if (proxy is MeshRenderer && target is MeshRenderer)
                {
                    var targetFilter = target.GetComponent<MeshFilter>();
                    var proxyFilter = proxy.GetComponent<MeshFilter>();
                    if (targetFilter == null || proxyFilter == null) return;
                    targetFilter.sharedMesh = proxyFilter.sharedMesh;
                    target.sharedMaterials = proxy.sharedMaterials;
                }
            });
        }

        private static void ApplySkinned(SkinnedMeshRenderer proxy, SkinnedMeshRenderer target)
        {
            // The bones of the proxy are only usable if they can be told apart from the bones of the original.
            // When the preview adds or removes bones, the mesh does not match the bones of the copy, so the renderer is left as it is.
            if (proxy.bones.Length != target.bones.Length) return;

            target.sharedMesh = proxy.sharedMesh;
            target.sharedMaterials = proxy.sharedMaterials;
            var blendShapeCount = proxy.sharedMesh != null ? proxy.sharedMesh.blendShapeCount : 0;
            for (var i = 0; i < blendShapeCount; i++)
            {
                target.SetBlendShapeWeight(i, proxy.GetBlendShapeWeight(i));
            }
            target.localBounds = proxy.localBounds;
        }

        /// While the returned object is not disposed of, NDMF does not react to scenes being opened or closed.
        ///
        /// NDMF throws away every preview result when any scene other than its own is opened or closed, then computes the whole preview
        /// again, which can take seconds on a complex avatar. The lightbox scene contains nothing that NDMF previews, so opening and
        /// closing it does not need to do that.
        /// NDMF is not modified: the scene opened and scene closed callbacks that its ObjectWatcher registers are removed from the
        /// EditorSceneManager events, and registered again on dispose. If they cannot be found (e.g. in another version of NDMF),
        /// nothing is removed, and NDMF behaves as usual.
        internal static IDisposable SuppressSceneInvalidation()
        {
            var opened = FindObjectWatcherCallbacks<EditorSceneManager.SceneOpenedCallback>();
            var closed = FindObjectWatcherCallbacks<EditorSceneManager.SceneClosedCallback>();
            foreach (var callback in opened) EditorSceneManager.sceneOpened -= callback;
            foreach (var callback in closed) EditorSceneManager.sceneClosed -= callback;
            return new SceneInvalidationSuppression(opened, closed);
        }

        private sealed class SceneInvalidationSuppression : IDisposable
        {
            private List<EditorSceneManager.SceneOpenedCallback> _opened;
            private List<EditorSceneManager.SceneClosedCallback> _closed;

            public SceneInvalidationSuppression(List<EditorSceneManager.SceneOpenedCallback> opened, List<EditorSceneManager.SceneClosedCallback> closed)
            {
                _opened = opened;
                _closed = closed;
            }

            public void Dispose()
            {
                if (_opened == null) return;
                foreach (var callback in _opened) EditorSceneManager.sceneOpened += callback;
                foreach (var callback in _closed) EditorSceneManager.sceneClosed += callback;
                _opened = null;
                _closed = null;
            }
        }

        private const string ObjectWatcherCallbacksTypeName = "nadena.dev.ndmf.cs.ObjectWatcher+<>c";

        /// The callbacks are lambdas that capture nothing, so the compiler stores each of them in a static field of the nested class
        /// that holds the lambdas of ObjectWatcher. The fields are found by type, not by name. A field is null if NDMF has not
        /// registered that callback yet.
        private static List<T> FindObjectWatcherCallbacks<T>() where T : Delegate
        {
            var result = new List<T>();
            try
            {
                var type = typeof(NDMFPreview).Assembly.GetType(ObjectWatcherCallbacksTypeName);
                if (type == null) return result;

                foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType == typeof(T) && field.GetValue(null) is T callback)
                    {
                        result.Add(callback);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"(LightboxViewer) Could not find the scene callbacks of NDMF, NDMF Preview will be computed again when the lightbox scene is opened or closed: {e.Message}");
                result.Clear();
            }
            return result;
        }

        // GetInstanceID is not available in recent versions of Unity.
        private static int HashOf(Object obj) => obj != null ? obj.GetHashCode() : 0;

        private static Mesh GetMesh(Renderer proxy)
        {
            if (proxy is SkinnedMeshRenderer smr) return smr.sharedMesh;
            var filter = proxy.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// If activeResultOnly is true, only the proxies of the result that NDMF Preview shows are used, when they can be found.
        /// Otherwise, all the proxies of the preview scene are used, which includes those of a result that is still being computed.
        private static void ForEachProxy(GameObject originalRoot, bool activeResultOnly, System.Action<GameObject, Renderer> action)
        {
            // NDMF Preview keeps showing its active result until the next one is complete. The proxies of a result that is still being
            // computed are in the preview scene too.
            var activeProxies = activeResultOnly ? ActiveOriginalToProxyRenderer() : null;
            if (activeProxies != null)
            {
                foreach (var pair in activeProxies)
                {
                    var original = pair.Key;
                    var proxy = pair.Value;
                    if (original == null || proxy == null || !original.transform.IsChildOf(originalRoot.transform)) continue;

                    action(original.gameObject, proxy);
                }
                return;
            }

            var previewScene = NDMFPreviewSceneManager.GetPreviewScene();
            if (!previewScene.IsValid() || !previewScene.isLoaded) return;

            ProxyRenderers.Clear();
            foreach (var root in previewScene.GetRootGameObjects())
            {
                root.GetComponentsInChildren(true, ProxyRenderers);
                foreach (var proxy in ProxyRenderers)
                {
                    var original = NDMFPreview.GetOriginalObjectForProxy(proxy.gameObject);
                    if (original == null || !original.transform.IsChildOf(originalRoot.transform)) continue;

                    action(original, proxy);
                }
                ProxyRenderers.Clear();
            }
        }

        // The state of the preview is not part of the public API of NDMF, so it is read with reflection (NDMF 1.14):
        // PreviewSession.OriginalToProxyRenderer (the proxies of the active result), PreviewSession._proxySession, ProxySession._active and
        // ProxySession._next (the result being computed), ProxyPipeline.IsReady and ProxyPipeline.IsInvalidated.
        // If any of them cannot be found, the proxies are found in the preview scene, and the preview is never considered to be processing.
        private static bool _reflectionResolved;
        private static PropertyInfo _sessionOriginalToProxyRenderer;
        private static FieldInfo _sessionProxySession;
        private static FieldInfo _proxySessionActive;
        private static FieldInfo _proxySessionNext;
        private static PropertyInfo _pipelineIsReady;
        private static PropertyInfo _pipelineIsInvalidated;

        private static void ResolveReflection()
        {
            if (_reflectionResolved) return;
            _reflectionResolved = true;
            try
            {
                const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _sessionOriginalToProxyRenderer = typeof(PreviewSession).GetProperty("OriginalToProxyRenderer", instance);
                _sessionProxySession = typeof(PreviewSession).GetField("_proxySession", instance);
                var proxySessionType = _sessionProxySession?.FieldType;
                _proxySessionActive = proxySessionType?.GetField("_active", instance);
                _proxySessionNext = proxySessionType?.GetField("_next", instance);
                var pipelineType = _proxySessionActive?.FieldType;
                _pipelineIsReady = pipelineType?.GetProperty("IsReady", instance);
                _pipelineIsInvalidated = pipelineType?.GetProperty("IsInvalidated", instance);
                if (_pipelineIsReady?.PropertyType != typeof(bool)) _pipelineIsReady = null;
                if (_pipelineIsInvalidated?.PropertyType != typeof(bool)) _pipelineIsInvalidated = null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"(LightboxViewer) Could not find the state of NDMF Preview: {e.Message}");
                _sessionOriginalToProxyRenderer = null;
                _sessionProxySession = null;
            }
        }

        private static IEnumerable<KeyValuePair<Renderer, Renderer>> ActiveOriginalToProxyRenderer()
        {
            ResolveReflection();
            var session = PreviewSession.Current;
            if (session == null || _sessionOriginalToProxyRenderer == null) return null;
            try
            {
                return _sessionOriginalToProxyRenderer.GetValue(session) as IEnumerable<KeyValuePair<Renderer, Renderer>>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// True when NDMF Preview currently has an active result that is complete. It may be out of date (a new result is being computed),
        /// but its proxies can still be read.
        internal static bool HasActiveResult
        {
            get
            {
                ResolveReflection();
                var session = PreviewSession.Current;
                if (session == null || _sessionProxySession == null || _proxySessionActive == null || _pipelineIsReady == null) return false;
                try
                {
                    var proxySession = _sessionProxySession.GetValue(session);
                    if (proxySession == null) return false;
                    var active = _proxySessionActive.GetValue(proxySession);
                    return active != null && (bool)_pipelineIsReady.GetValue(active);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// True while NDMF Preview has a result that is not complete yet: the active result is not ready or is out of date, or the next result
        /// is still being computed. NDMF only starts computing, and switches to, the next result when the Scene view renders.
        internal static bool IsProcessing
        {
            get
            {
                ResolveReflection();
                var session = PreviewSession.Current;
                if (session == null || _sessionProxySession == null || _proxySessionActive == null || _proxySessionNext == null
                    || _pipelineIsReady == null || _pipelineIsInvalidated == null) return false;
                try
                {
                    var proxySession = _sessionProxySession.GetValue(session);
                    if (proxySession == null) return false;
                    if (_proxySessionNext.GetValue(proxySession) != null) return true;

                    var active = _proxySessionActive.GetValue(proxySession);
                    if (active == null) return true;
                    return !(bool)_pipelineIsReady.GetValue(active) || (bool)_pipelineIsInvalidated.GetValue(active);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
#else
        internal static bool IsInstalled => false;
        internal static bool IsPreviewEnabled => false;
        internal static int ComputeSignature(GameObject originalRoot, bool activeResultOnly) => 0;
        internal static void Apply(GameObject originalRoot, IReadOnlyDictionary<Renderer, Renderer> copiedRenderers, bool activeResultOnly) { }
        internal static IDisposable SuppressSceneInvalidation() => null;
        internal static bool IsProcessing => false;
        internal static bool HasActiveResult => false;
#endif

        /// True when NDMF is installed and NDMF Preview is enabled.
        internal static bool IsAvailable => IsInstalled && IsPreviewEnabled;

        /// Gives back to each renderer of the copy what Apply may have changed (mesh, materials, blend shape weights, bounds), taken from
        /// the original renderer. The copied renderers are the renderer of the copy for each original renderer.
        internal static void RestoreFromOriginals(IReadOnlyDictionary<Renderer, Renderer> copiedRenderers)
        {
            foreach (var pair in copiedRenderers)
            {
                var original = pair.Key;
                var target = pair.Value;
                if (original == null || target == null) continue;

                if (original is SkinnedMeshRenderer originalSmr && target is SkinnedMeshRenderer targetSmr)
                {
                    targetSmr.sharedMesh = originalSmr.sharedMesh;
                    targetSmr.sharedMaterials = originalSmr.sharedMaterials;
                    var blendShapeCount = originalSmr.sharedMesh != null ? originalSmr.sharedMesh.blendShapeCount : 0;
                    for (var i = 0; i < blendShapeCount; i++)
                    {
                        targetSmr.SetBlendShapeWeight(i, originalSmr.GetBlendShapeWeight(i));
                    }
                    targetSmr.localBounds = originalSmr.localBounds;
                }
                else if (original is MeshRenderer && target is MeshRenderer)
                {
                    var originalFilter = original.GetComponent<MeshFilter>();
                    var targetFilter = target.GetComponent<MeshFilter>();
                    if (originalFilter != null && targetFilter != null) targetFilter.sharedMesh = originalFilter.sharedMesh;
                    target.sharedMaterials = original.sharedMaterials;
                }
            }
        }
    }
}
