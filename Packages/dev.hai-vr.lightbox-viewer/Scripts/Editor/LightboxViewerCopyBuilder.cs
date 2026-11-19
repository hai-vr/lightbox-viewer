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
    /// The equivalent object of the copy for each original object that was copied.
    internal class LightboxViewerCopyMaps
    {
        /// The renderer of the copy for each original renderer.
        public readonly Dictionary<Renderer, Renderer> Renderers = new Dictionary<Renderer, Renderer>();
        /// The transform of the copy for each original transform that was copied, including the root.
        public readonly Dictionary<Transform, Transform> Transforms = new Dictionary<Transform, Transform>();
        /// The component of the copy for each original component that was copied (renderers, mesh filters, lights, Light Probe Proxy Volumes).
        public readonly Dictionary<Component, Component> Components = new Dictionary<Component, Component>();

        public void Clear()
        {
            Renderers.Clear();
            Transforms.Clear();
            Components.Clear();
        }

        /// True if an original object that was copied has been destroyed since.
        public bool AnyOriginalDestroyed()
        {
            foreach (var original in Transforms.Keys) if (original == null) return true;
            foreach (var original in Components.Keys) if (original == null) return true;
            return false;
        }
    }

    internal class LightboxViewerCopyBuilder
    {
        internal const string HolderName = "LightboxAvatarHolder";

        private const HideFlags CopyHideFlags = HideFlags.HideAndDontSave;

        private readonly Transform _originalRoot;
        private readonly LightboxViewerCopyMaps _maps;
        private Dictionary<Transform, Transform> _mirrors => _maps.Transforms;

        private LightboxViewerCopyBuilder(Transform originalRoot, LightboxViewerCopyMaps maps)
        {
            _originalRoot = originalRoot;
            _maps = maps;
        }

        /// Returns the inactive holder of the copy. The maps receive the equivalent object of the copy for each original object that was copied.
        internal static GameObject Build(GameObject originalAvatarGo, LightboxViewerCopyMaps maps)
        {
            maps.Clear();
            return new LightboxViewerCopyBuilder(originalAvatarGo.transform, maps).Build();
        }

        /// Copies the values of these original components onto their equivalent component of the existing copy, as when the copy was created.
        /// Components that were not copied are ignored. Transforms that the new values reference are created if they do not exist yet.
        internal static void UpdateComponents(Transform originalRoot, LightboxViewerCopyMaps maps, IEnumerable<Component> originals)
        {
            var builder = new LightboxViewerCopyBuilder(originalRoot, maps);
            foreach (var original in originals)
            {
                if (original == null || !maps.Components.TryGetValue(original, out var copy) || copy == null) continue;

                EditorUtility.CopySerialized(original, copy);
                builder.RemapReferences(copy);
            }
        }

        /// Copies the local position, rotation and scale of these original transforms onto their equivalent transform of the existing copy.
        /// The root is not concerned, as the copy is placed according to the original root on every render.
        internal static void UpdateTransforms(Transform originalRoot, LightboxViewerCopyMaps maps, IEnumerable<Transform> originals)
        {
            foreach (var original in originals)
            {
                if (original == null || original == originalRoot || !maps.Transforms.TryGetValue(original, out var copy) || copy == null) continue;

                copy.localPosition = original.localPosition;
                copy.localRotation = original.localRotation;
                copy.localScale = original.localScale;
            }
        }

        /// Makes the objects of the copy active or inactive like the original objects, where an object hidden in the Scene view (eye icon of the
        /// Hierarchy) is inactive, and puts them on the same layer. The root of the copy is always active, even if the original is not.
        /// This must be applied after anything that changes the renderers of the copy (such as the NDMF Preview result): an object hidden in
        /// the Scene view is never rendered, even if its renderer was changed.
        internal static void ApplyGameObjectStates(LightboxViewerCopyMaps maps, Transform originalRoot)
        {
            foreach (var pair in maps.Transforms)
            {
                var original = pair.Key;
                var copy = pair.Value;
                if (original == null || copy == null) continue;

                var copyGo = copy.gameObject;
                var originalGo = original.gameObject;
                if (copyGo.layer != originalGo.layer) copyGo.layer = originalGo.layer;
                if (original == originalRoot) continue;

                var active = IsActiveAndVisible(originalGo);
                if (copyGo.activeSelf != active) copyGo.SetActive(active);
            }
        }

        // The visibility of the Scene view (eye icon) is not part of the object, so it has to be carried over.
        private static bool IsActiveAndVisible(GameObject original) => original.activeSelf && !SceneVisibilityManager.instance.IsHidden(original);

        private GameObject Build()
        {
            var holder = new GameObject(HolderName) { hideFlags = CopyHideFlags };
            holder.transform.position = _originalRoot.position;
            holder.transform.rotation = _originalRoot.rotation;
            holder.transform.localScale = _originalRoot.localScale;
            // The copy must not be active until it is complete, so that it is not seen by anything else before that.
            holder.SetActive(false);

            // Same world position, rotation and scale as the original root.
            // The layer matters for rendering: lights and cameras can have a culling mask.
            var rootCopy = new GameObject(_originalRoot.name) { hideFlags = CopyHideFlags, layer = _originalRoot.gameObject.layer };
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
            if (copy != null)
            {
                _maps.Renderers[original] = copy;
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
            _maps.Components[original] = copy;
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
            var go = new GameObject(original.name) { hideFlags = CopyHideFlags, layer = original.gameObject.layer };
            var mirror = go.transform;
            mirror.SetParent(parentMirror, false);
            mirror.localPosition = original.localPosition;
            mirror.localRotation = original.localRotation;
            mirror.localScale = original.localScale;

            go.SetActive(IsActiveAndVisible(original.gameObject));

            _mirrors[original] = mirror;
            return mirror;
        }
    }
}
