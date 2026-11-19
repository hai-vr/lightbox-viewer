using UnityEngine;
using UnityEngine.UIElements;

namespace Hai.LightboxViewer.Scripts.Editor
{
    /// Draws a texture stretched over the element, in true colors.
    /// In Play Mode, the editor tints everything it draws (the "Playmode tint" preference). UI Toolkit applies that tint when elements such as
    /// Image generate their own mesh. The meshes that are allocated with MeshGenerationContext.Allocate are not tinted, so this element uses
    /// that to show the rendered lightboxes without the tint, like the IMGUI version did by setting GUI.color to white.
    internal class LightboxViewerUntintedImage : VisualElement
    {
        private Texture _texture;

        public Texture texture
        {
            get => _texture;
            set
            {
                if (_texture == value) return;
                _texture = value;
                MarkDirtyRepaint();
            }
        }

        public LightboxViewerUntintedImage()
        {
            generateVisualContent += OnGenerateVisualContent;
        }

        private void OnGenerateVisualContent(MeshGenerationContext mgc)
        {
            var rect = contentRect;
            if (_texture == null || rect.width < 1 || rect.height < 1) return;

            var white = new Color32(255, 255, 255, 255);
            var vertices = new Vertex[4];
            vertices[0] = new Vertex { position = new Vector3(rect.xMin, rect.yMax, Vertex.nearZ), tint = white, uv = new Vector2(0, 0) };
            vertices[1] = new Vertex { position = new Vector3(rect.xMin, rect.yMin, Vertex.nearZ), tint = white, uv = new Vector2(0, 1) };
            vertices[2] = new Vertex { position = new Vector3(rect.xMax, rect.yMin, Vertex.nearZ), tint = white, uv = new Vector2(1, 1) };
            vertices[3] = new Vertex { position = new Vector3(rect.xMax, rect.yMax, Vertex.nearZ), tint = white, uv = new Vector2(1, 0) };

            var mesh = mgc.Allocate(4, 6, _texture);
            mesh.SetAllVertices(vertices);
            mesh.SetAllIndices(new ushort[] { 0, 1, 2, 2, 3, 0 });
        }
    }
}
