using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

// ReSharper disable once CheckNamespace
namespace Hai.LightboxViewer.Scripts.Editor
{
    public class LightboxViewerGenerator
    {

        private GameObject _animatedRoot;
        private Camera _camera;
        private Material _material;
        private bool _needsCounterRoll;

        /// The viewpoint is taken from the last active Scene view. If there is none (e.g. the layout only has a Game view),
        /// fall back to any open Scene view. Returns null if no Scene view is open.
        internal static Camera FindSceneViewCameraOrNull()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
            {
                sceneView = SceneView.sceneViews[0] as SceneView;
            }

            return sceneView != null ? sceneView.camera : null;
        }

        /// The camera is owned by the caller (LightboxViewerRenderQueue), and is reused across renders.
        /// If the camera is a copy of a reference camera, its clip planes are kept as-is.
        /// The counter-roll material is also owned by the caller, and is only needed when NeedsCounterRoll returns true.
        public void Begin(GameObject animatedRoot, Camera camera, Camera sceneCamera, float customRoll, bool counterRotate, Material counterRollMaterialOptional, bool isCopyOfReferenceCamera, int antiAliasing)
        {
            _animatedRoot = animatedRoot;
            _camera = camera;
            // The camera is disabled between renders, so that it does not render on its own. Rendering happens
            // synchronously between Begin and Terminate, with the camera enabled like a freshly created camera would be.
            _camera.enabled = true;
            var isCustomRollSlanted = IsSlanted(customRoll);
            _needsCounterRoll = NeedsCounterRoll(customRoll, counterRotate);
            _material = _needsCounterRoll ? counterRollMaterialOptional : null;
            if (_material != null)
            {
                _material.SetFloat("_CounterRoll", -customRoll / 180f);
            }

            _camera.transform.position = sceneCamera.transform.position;
            _camera.transform.rotation = sceneCamera.transform.rotation;
            if (isCustomRollSlanted)
            {
                var euler = _camera.transform.rotation.eulerAngles;
                euler.z = customRoll;
                _camera.transform.rotation = Quaternion.Euler(euler);
            }
            var whRatio = (1f * sceneCamera.pixelWidth / sceneCamera.pixelHeight);
            _camera.fieldOfView = whRatio < 1 ? sceneCamera.fieldOfView * whRatio : sceneCamera.fieldOfView;
            _camera.orthographic = sceneCamera.orthographic;
            _camera.orthographicSize = sceneCamera.orthographicSize;
            if (!isCopyOfReferenceCamera)
            {
                _camera.nearClipPlane = sceneCamera.nearClipPlane;
                _camera.farClipPlane = sceneCamera.farClipPlane;
            }
            _camera.allowMSAA = antiAliasing > 1;
        }

        private static bool IsSlanted(float customRoll) => customRoll != 0 && customRoll != 360 && customRoll != -360;

        internal static bool NeedsCounterRoll(float customRoll, bool counterRotate) => IsSlanted(customRoll) && counterRotate;

        /// Creates the counter-roll material that is reused across renders.
        internal static Material CreateCounterRollMaterial()
        {
            return new Material(Shader.Find("Hai/LightboxViewerCounterRoll"))
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        /// Creates the camera that is reused across renders. It is hidden, is not saved with the scene,
        /// and its Camera component is disabled so that it only renders when Camera.Render() is called.
        internal const string CameraHolderName = "LightboxViewerCameraHolder";

        internal static Camera CreateReusableCamera(Camera cameraOptional, bool usePostProcessing)
        {
            Profiler.BeginSample("LightboxViewer.Generator.CreateReusableCamera");
            var camera = cameraOptional != null ? Object.Instantiate(cameraOptional) : new GameObject().AddComponent<Camera>();
            var go = camera.gameObject;
            go.name = "LightboxViewerCamera";
            camera.enabled = false;

            // A copy of a reference camera may carry an Audio Listener, which would conflict with the scene's one
            // now that the copy outlives the render.
            foreach (var audioListener in go.GetComponentsInChildren<AudioListener>(true))
            {
                audioListener.enabled = false;
            }

            if (usePostProcessing && LightboxViewerEditorWindow.PplType != null)
            {
                var ppl = go.AddComponent(LightboxViewerEditorWindow.PplType);
                LightboxViewerEditorWindow.PplVolumeLayerField.SetValue(ppl, new LayerMask { value = -1 });
                LightboxViewerEditorWindow.PplVolumeTriggerField.SetValue(ppl, camera.transform);
            }

            // NDMF Preview swaps its proxies in for every camera that has no parent or no target texture. The copy that we render
            // already carries the preview result, so our camera is given a parent (it always renders into a target texture).
            var holder = new GameObject(CameraHolderName) { hideFlags = HideFlags.HideAndDontSave };
            camera.transform.SetParent(holder.transform, true);

            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.hideFlags = HideFlags.HideAndDontSave;
            }
            Profiler.EndSample();

            return camera;
        }

        public void Terminate()
        {
            // The camera and the counter-roll material are owned by the caller, and are not destroyed here.
            if (_camera != null) _camera.enabled = false;
            _camera = null;
            _material = null;
        }

        public void RenderNoAnimator(Texture element, GameObject currentLightbox, Vector3 referentialVector, Quaternion referentialQuaternion, float verticalDisplacement)
        {
            var rootTransform = _animatedRoot.transform;
            var camTransform = _camera.transform;

            var initPos = rootTransform.position;
            var initRot = rootTransform.rotation;
            var camPos = camTransform.position;
            var camRot = camTransform.rotation;
            try
            {
                var targetPos = currentLightbox.transform.position + Vector3.up * verticalDisplacement;
                rootTransform.position = targetPos + (initPos - referentialVector);
                var relativeVector = camPos - referentialVector;
                camTransform.position = currentLightbox.transform.rotation * referentialQuaternion * relativeVector + targetPos;
                camTransform.rotation = currentLightbox.transform.rotation * referentialQuaternion * camTransform.rotation;
                rootTransform.rotation = currentLightbox.transform.rotation * referentialQuaternion * rootTransform.rotation;

                if (element is RenderTexture rt)
                {
                    RenderTexture.active = rt;
                    GL.Clear(true, true, Color.clear);
                    RenderTexture.active = null;

                    RenderCamera(rt, _camera);
                    if (_needsCounterRoll && _material != null)
                    {
                        var diff = RenderTexture.GetTemporary(rt.width, rt.height, 24, RenderTextureFormat.ARGB32);
                        Graphics.Blit(rt, diff);

                        _material.SetTexture("_MainTex", diff);
                        var ratio = rt.width / (float)rt.height;
                        _material.SetFloat("_Ratio", ratio);
                        Graphics.Blit(diff, rt, _material);

                        RenderTexture.ReleaseTemporary(diff);
                    }
                }
            }
            finally
            {
                rootTransform.position = initPos;
                rootTransform.rotation = initRot;
                camTransform.position = camPos;
                camTransform.rotation = camRot;
            }
        }

        private static void RenderCamera(RenderTexture renderTexture, Camera camera)
        {
            var originalRenderTexture = camera.targetTexture;
            var originalAspect = camera.aspect;
            var originalColor = camera.backgroundColor;
            var originalClearFlags = camera.clearFlags;
            try
            {
                camera.targetTexture = renderTexture;
                camera.aspect = (float) renderTexture.width / renderTexture.height;
                camera.backgroundColor = Color.black;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.Render();
            }
            finally
            {
                camera.targetTexture = originalRenderTexture;
                camera.aspect = originalAspect;
                camera.backgroundColor = originalColor;
                camera.clearFlags = originalClearFlags;
            }
        }

        public int IsStillRendering()
        {
            return 0;
        }
    }
}
