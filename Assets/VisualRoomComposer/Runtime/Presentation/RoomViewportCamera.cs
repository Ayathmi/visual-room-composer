using System;
using UnityEngine;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Presentation
{
    /// <summary>Owns the render target and grid, with floor projection in the same canonical meter space as authoring.</summary>
    public sealed class RoomViewportCamera : IDisposable
    {
        private readonly Camera camera;
        private readonly GameObject grid;
        private readonly Mesh gridMesh;
        private readonly Material gridMaterial;
        private Vector3 center = new Vector3(3, 0.8f, 1.5f);
        private float yaw = -32, size = 5;
        public RenderTexture Texture { get; private set; }
        public Camera Camera => camera;
        public RoomViewportCamera(Camera camera)
        {
            this.camera = camera;
            camera.orthographic = true; camera.nearClipPlane = 0.05f; camera.farClipPlane = 200;
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.07f, .075f, .08f);
            grid = new GameObject("Authoring grid");
            gridMesh = new Mesh { name = "Authoring quarter-meter grid" };
            var vertices = new Vector3[4 * 81];
            var indices = new int[vertices.Length];
            for (var i = 0; i <= 80; i++)
            {
                var p = -10 + i * .25f;
                vertices[4 * i] = new Vector3(p, -.004f, -10); vertices[4 * i + 1] = new Vector3(p, -.004f, 10);
                vertices[4 * i + 2] = new Vector3(-10, -.004f, p); vertices[4 * i + 3] = new Vector3(10, -.004f, p);
            }
            for (var i = 0; i < indices.Length; i++) indices[i] = i;
            gridMesh.vertices = vertices; gridMesh.SetIndices(indices, MeshTopology.Lines, 0);
            gridMaterial = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            gridMaterial.SetColor("_BaseColor", new Color(.16f, .17f, .18f));
            grid.AddComponent<MeshFilter>().sharedMesh = gridMesh;
            grid.AddComponent<MeshRenderer>().sharedMaterial = gridMaterial;
            Resize(1280, 900); Position();
        }
        public bool Resize(int width, int height)
        {
            width = Mathf.Clamp(width, 128, 4096); height = Mathf.Clamp(height, 128, 4096);
            if (Texture != null && Texture.width == width && Texture.height == height) return false;
            var previous = Texture;
            Texture = new RenderTexture(width, height, 24) { name = "Room editor viewport" };
            Texture.Create(); camera.targetTexture = Texture;
            if (previous != null) { previous.Release(); UnityEngine.Object.Destroy(previous); }
            return true;
        }
        public bool Floor(Vector2 normalizedTopLeft, out GridPosition cell)
        {
            var ray = camera.ViewportPointToRay(new Vector3(normalizedTopLeft.x, 1 - normalizedTopLeft.y));
            if (!new Plane(Vector3.up, Vector3.zero).Raycast(ray, out var distance)) { cell = default; return false; }
            var p = ray.GetPoint(distance);
            if (Math.Abs(p.x) > 16000 || Math.Abs(p.z) > 16000) { cell = default; return false; }
            cell = new GridPosition((int)Math.Round(p.x * 4, MidpointRounding.AwayFromZero), 0, (int)Math.Round(p.z * 4, MidpointRounding.AwayFromZero));
            return true;
        }
        public void Zoom(float delta) { size = Mathf.Clamp(size * Mathf.Pow(1.07f, delta), 1.5f, 25); Position(); }
        public void Pan(Vector2 delta) { center += Quaternion.Euler(0, yaw, 0) * new Vector3(-delta.x, 0, delta.y) * size * .002f; Position(); }
        public void Orbit(float delta) { yaw += delta * .3f; Position(); }
        public void Frame(Bounds bounds) { center = bounds.center; size = Mathf.Clamp(bounds.extents.magnitude * 1.15f, 2, 25); Position(); }
        private void Position()
        {
            camera.orthographicSize = size;
            camera.transform.position = center + Quaternion.Euler(0, yaw, 0) * new Vector3(0, 18, -14);
            camera.transform.LookAt(center);
        }
        public void Dispose()
        {
            camera.targetTexture = null;
            if (Texture != null) { Texture.Release(); UnityEngine.Object.Destroy(Texture); Texture = null; }
            UnityEngine.Object.Destroy(grid); UnityEngine.Object.Destroy(gridMesh); UnityEngine.Object.Destroy(gridMaterial);
        }
    }
}
