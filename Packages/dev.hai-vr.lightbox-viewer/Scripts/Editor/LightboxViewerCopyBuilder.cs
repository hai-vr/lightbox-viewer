using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    /// Creates the copy of the object that is rendered in Edit Mode.
    ///
    /// The copy is not an instantiation of the whole object. It only contains the renderers (and their mesh filters) and the lights of the object,
    /// and the transforms that they need: the transform of each renderer and light, the bones, the root bone and the anchor override
    /// (any transform referenced by a copied component is found by looking at its serialized references), along with their parents.
    /// Nothing else is copied, so no script, animator, constraint or other component of the object runs in the copy.
    internal class LightboxViewerCopyBuilder
    {
        internal const string HolderName = "LightboxAvatarHolder";

        private const HideFlags CopyHideFlags = HideFlags.HideAndDontSave;

        private readonly Transform _originalRoot;
        private readonly Dictionary<Transform, Transform> _mirrors = new Dictionary<Transform, Transform>();
        private readonly Dictionary<Renderer, Renderer> _copiedRenderers;

        private LightboxViewerCopyBuilder(Transform originalRoot, Dictionary<Renderer, Renderer> copiedRenderers)
        {
            _originalRoot = originalRoot;
            _copiedRenderers = copiedRenderers;
        }

        /// Returns the inactive holder of the copy. If copiedRenderers is not null, it receives the renderer of the copy for each original renderer.
        internal static GameObject Build(GameObject originalAvatarGo, Dictionary<Renderer, Renderer> copiedRenderers)
        {
            copiedRenderers?.Clear();
            return new LightboxViewerCopyBuilder(originalAvatarGo.transform, copiedRenderers).Build();
        }

        private GameObject Build()
        {
            var holder = new GameObject(HolderName) { hideFlags = CopyHideFlags };
            holder.transform.position = _originalRoot.position;
            holder.transform.rotation = _originalRoot.rotation;
            holder.transform.localScale = _originalRoot.localScale;
            // The copy must not be active until it is complete, so that it is not seen by anything else before that.
            holder.SetActive(false);

            // Same world position, rotation and scale as the original root.
            var rootCopy = new GameObject(_originalRoot.name) { hideFlags = CopyHideFlags };
            rootCopy.transform.position = _originalRoot.position;
            rootCopy.transform.rotation = _originalRoot.rotation;
            rootCopy.transform.localScale = _originalRoot.lossyScale;
            rootCopy.transform.SetParent(holder.transform, true);
            _mirrors[_originalRoot] = rootCopy.transform;

            foreach (var original in _originalRoot.GetComponentsInChildren<Renderer>(true))
            {
                CopyRenderer(original);
            }

            foreach (var original in _originalRoot.GetComponentsInChildren<Light>(true))
            {
                CopyComponent(Mirror(original.transform).gameObject, original);
            }

            // The root of the object is always active, even if the original is not.
            rootCopy.SetActive(true);

            return holder;
        }

        private void CopyRenderer(Renderer original)
        {
            // A particle system renderer is not usable without its particle system, which is not copied.
            if (original is ParticleSystemRenderer) return;

            var target = Mirror(original.transform).gameObject;

            var meshFilter = original.GetComponent<MeshFilter>();
            if (meshFilter != null && target.GetComponent<MeshFilter>() == null)
            {
                CopyComponent(target, meshFilter);
            }

            var copy = CopyComponent(target, original) as Renderer;
            if (copy != null && _copiedRenderers != null)
            {
                _copiedRenderers[original] = copy;
            }
        }

        /// Adds a component of the same type to the target, with the same values.
        /// References to the transforms of the original object are replaced with the equivalent transforms of the copy.
        private Component CopyComponent(GameObject target, Component original)
        {
            var copy = target.AddComponent(original.GetType());
            if (copy == null) return null; // Not all components can be added through a script.

            EditorUtility.CopySerialized(original, copy);
            RemapReferences(copy);
            return copy;
        }

        private void RemapReferences(Component copy)
        {
            var serializedObject = new SerializedObject(copy);
            var iterator = serializedObject.GetIterator();
            var modified = false;
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference) continue;

                var value = iterator.objectReferenceValue;
                if (value == null) continue;

                var remapped = Remap(value);
                if (remapped != value)
                {
                    iterator.objectReferenceValue = remapped;
                    modified = true;
                }
            }

            if (modified)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        private Object Remap(Object value)
        {
            if (value is Transform transform)
            {
                return IsPartOfOriginal(transform) ? Mirror(transform) : value;
            }

            if (value is GameObject gameObject)
            {
                if (!IsPartOfOriginal(gameObject.transform)) return value;

                var mirror = Mirror(gameObject.transform);
                // Renderers can use a Light Probe Proxy Volume that is located on another object.
#pragma warning disable CS0618 // Light Probe Proxy Volumes are deprecated in recent versions of Unity, but objects can still use them.
                var proxyVolume = gameObject.GetComponent<LightProbeProxyVolume>();
                if (proxyVolume != null && mirror.GetComponent<LightProbeProxyVolume>() == null)
                {
                    CopyComponent(mirror.gameObject, proxyVolume);
                }
#pragma warning restore CS0618
                return mirror.gameObject;
            }

            // Another component of the original object does not exist in the copy.
            if (value is Component component && IsPartOfOriginal(component.transform)) return null;

            return value;
        }

        private bool IsPartOfOriginal(Transform transform) => transform.IsChildOf(_originalRoot);

        /// Returns the transform of the copy that is equivalent to a transform of the original object. It is created if it does not exist yet.
        private Transform Mirror(Transform original)
        {
            if (_mirrors.TryGetValue(original, out var existing)) return existing;

            var parentMirror = Mirror(original.parent);
            var go = new GameObject(original.name) { hideFlags = CopyHideFlags };
            var mirror = go.transform;
            mirror.SetParent(parentMirror, false);
            mirror.localPosition = original.localPosition;
            mirror.localRotation = original.localRotation;
            mirror.localScale = original.localScale;

            // The visibility of the Scene view (eye icon) is not part of the object, so it has to be carried over.
            go.SetActive(original.gameObject.activeSelf && !SceneVisibilityManager.instance.IsHidden(original.gameObject));

            _mirrors[original] = mirror;
            return mirror;
        }
    }
}
