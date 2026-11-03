namespace VisualRoomComposer.Core
{
    /// <summary>Original asymmetric development fixture with independently calculated world-space expectations.</summary>
    public static class AffineProbeFixture
    {
        public const string FixtureId = "vrc-original-affine-1";
        public static ModelScene Create()
        {
            var positions = new[]
            {
                new Double3(0, 0, 0), new Double3(2, 0, 0), new Double3(0, 1, 0),
                new Double3(0, 0, 1), new Double3(0, 1, 1), new Double3(1, 0, 1)
            };
            var normals = new[] { Double3.UnitZ, Double3.UnitZ, Double3.UnitZ, -Double3.UnitZ, -Double3.UnitZ, -Double3.UnitZ };
            var tangents = new[] { new Double4(1, 0, 0, 1), new Double4(1, 0, 0, 1), new Double4(1, 0, 0, 1), new Double4(1, 0, 0, -1), new Double4(1, 0, 0, -1), new Double4(1, 0, 0, -1) };
            var uv0 = new[] { new Double2(0, 0), new Double2(1, 0), new Double2(0, 1), new Double2(0.1, 0.2), new Double2(0.1, 0.8), new Double2(0.9, 0.2) };
            var uv1 = new[] { new Double2(0.1, 0.1), new Double2(0.4, 0.1), new Double2(0.1, 0.4), new Double2(0.6, 0.6), new Double2(0.6, 0.9), new Double2(0.9, 0.6) };
            var colors = new[] { new Double4(1, 0, 0, 1), new Double4(0, 1, 0, 1), new Double4(0, 0, 1, 1), new Double4(1, 1, 0, 1), new Double4(0, 1, 1, 1), new Double4(1, 0, 1, 1) };
            var mesh = new ModelMesh("asymmetric", "Original asymmetric two surfaces", positions,
                new[] { new ModelSubmesh(0, new[] { 0, 1, 2 }), new ModelSubmesh(1, new[] { 3, 4, 5 }) }, normals, tangents, uv0, uv1, colors);
            var materials = new[] { new ModelMaterial("red", "Red", new Double4(0.8, 0.1, 0.1, 1)), new ModelMaterial("blue", "Blue", new Double4(0.1, 0.2, 0.8, 1)) };
            var shear = new Matrix4d(1, 0.5, 0, 0.25, 0, 1, 0, 0.5, 0, 0, 1, -0.25, 0, 0, 0, 1);
            var nodes = new[]
            {
                new ModelNode("root", "Original fixture", null, Matrix4d.Translation(new Double3(3, 2, -1)) * Matrix4d.Scale(new Double3(2, 1, 3))),
                new ModelNode("shear", "Sheared surface", "root", shear, mesh.Id, new[] { "red", "blue" }),
                new ModelNode("mirror", "Mirrored instance", "root", Matrix4d.Translation(new Double3(-2, 0, 1)) * Matrix4d.Scale(new Double3(-1, 2, 0.5)), mesh.Id, new[] { "blue", "red" }),
                new ModelNode("nested", "Nested surface", "shear", Matrix4d.Translation(new Double3(0, 0, 2)), mesh.Id, new[] { "red", "blue" })
            };
            return new ModelScene(FixtureId, nodes, new[] { mesh }, materials);
        }
        public static Double3[] ExpectedShearedWorldVertices() => new[]
        {
            new Double3(3.5, 2.5, -1.75), new Double3(7.5, 2.5, -1.75), new Double3(4.5, 3.5, -1.75),
            new Double3(3.5, 2.5, 1.25), new Double3(4.5, 3.5, 1.25), new Double3(5.5, 2.5, 1.25)
        };
        public static Double3[] ExpectedMirroredWorldVertices() => new[]
        {
            new Double3(-1, 2, 2), new Double3(-5, 2, 2), new Double3(-1, 4, 2),
            new Double3(-1, 2, 3.5), new Double3(-1, 4, 3.5), new Double3(-3, 2, 3.5)
        };
    }
}
