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

        public void Begin(GameObject animatedRoot, float customRoll, bool counterRotate, Camera cameraOptional, bool usePostProcessing)
        {
            _animatedRoot = animatedRoot;
            var isCustomRollSlanted = customRoll != 0 && customRoll != 360 && customRoll != -360;
            if (isCustomRollSlanted && counterRotate)
            {
                _material = new Material(Shader.Find("Hai/LightboxViewerCounterRoll"));
                _material.SetFloat("_CounterRoll", -customRoll / 180f);
            }
            _needsCounterRoll = isCustomRollSlanted && counterRotate;

            Profiler.BeginSample("LightboxViewer.Generator.Begin.AddCameraComponent");
            _camera = cameraOptional != null ? Object.Instantiate(cameraOptional) : new GameObject().AddComponent<Camera>();
            Profiler.EndSample();

            var sceneCamera = SceneView.lastActiveSceneView.camera;
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
            if (cameraOptional == null)
            {
                _camera.nearClipPlane = sceneCamera.nearClipPlane;
                _camera.farClipPlane = sceneCamera.farClipPlane;
            }
            if (usePostProcessing && LightboxViewerEditorWindow.PplType != null)
            {
                var ppl = _camera.gameObject.AddComponent(LightboxViewerEditorWindow.PplType);
                LightboxViewerEditorWindow.PplVolumeLayerField.SetValue(ppl, new LayerMask { value = -1 });
                LightboxViewerEditorWindow.PplVolumeTriggerField.SetValue(ppl, _camera.transform);
            }
            _camera.allowMSAA = true;
        }

        public void Terminate()
        {
            Object.DestroyImmediate(_camera.gameObject);
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
