using UnityEditor;
using UnityEngine;
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

        public void Begin(GameObject animatedRoot, float customRoll, bool counterRotate, Camera cameraOptional, bool usePostProcessing)
        {
            _animatedRoot = animatedRoot;
            var isCustomRollSlanted = customRoll != 0 && customRoll != 360 && customRoll != -360;
            if (isCustomRollSlanted && counterRotate)
            {
                _material = new Material(Shader.Find("Hai/LightboxViewerCounterRoll"));
                _material.SetFloat("_CounterRoll", -customRoll / 180f);
            }

            _camera = cameraOptional != null ? Object.Instantiate(cameraOptional) : new GameObject().AddComponent<Camera>();

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
        }

        public void Terminate()
        {
            Object.DestroyImmediate(_camera.gameObject);
        }

        public void RenderNoAnimator(Texture2D element, GameObject currentLightbox, RenderTexture renderTexture, Vector3 referentialVector, Quaternion referentialQuaternion, float verticalDisplacement)
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

                renderTexture.wrapMode = TextureWrapMode.Clamp;

                RenderCamera(renderTexture, _camera);
                AsyncRenderTextureTo(renderTexture, element);
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
            try
            {
                camera.targetTexture = renderTexture;
                camera.aspect = (float) renderTexture.width / renderTexture.height;
                camera.Render();
            }
            finally
            {
                camera.targetTexture = originalRenderTexture;
                camera.aspect = originalAspect;
            }
        }

        private void AsyncRenderTextureTo(RenderTexture renderTexture, Texture2D texture2D)
        {
            AsyncGPUReadback.Request(renderTexture, 0, TextureFormat.RGB24, request => OnCompleteReadback(request, texture2D));
        }

        private void SyncRenderTextureTo(RenderTexture renderTexture, Texture2D texture2D)
        {
            RenderTexture.active = renderTexture;
            texture2D.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture2D.Apply();
            RenderTexture.active = null;
        }

        private void OnCompleteReadback(AsyncGPUReadbackRequest request, Texture2D texture2D)
        {
            texture2D.LoadRawTextureData(request.GetData<uint>());
            texture2D.Apply();

            if (_material != null)
            {
                _material.SetTexture("_MainTex", texture2D);
                var ratio = texture2D.width / (float)texture2D.height;
                _material.SetFloat("_Ratio", ratio);
                var diff = RenderTexture.GetTemporary(texture2D.width, texture2D.height, 24);
                Graphics.Blit(texture2D, diff, _material);
                RenderTexture.ReleaseTemporary(diff);
                SyncRenderTextureTo(diff, texture2D);
            }
        }

        public int IsStillRendering()
        {
            return 0;
        }
    }
}