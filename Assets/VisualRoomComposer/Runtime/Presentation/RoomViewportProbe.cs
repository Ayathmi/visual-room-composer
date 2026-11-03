using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;

namespace VisualRoomComposer.Presentation
{
    [Serializable] public sealed class ViewportCheck { public string Id; public bool Passed; public string Detail; }
    /// <summary>Real native-object checks against independent world coordinates, geometry holes and publication boundaries.</summary>
    public static class RoomViewportProbe
    {
        public static RoomVisualPlan[] Plans(RoomDocument document, Func<PinnedReference, LibraryDefinition> resolve,
            Func<LibraryDefinition, ModelScene> model) => document.Placements.Select(p => {
                var d = resolve(p.Definition);
                var scene = model(d) ?? (d.ModelReference == null
                    ? new ModelScene(d.Id, new[] { new ModelNode(d.Id, d.Name, null, Matrix4d.Identity) }, Array.Empty<ModelMesh>())
                    : throw new InvalidOperationException("Pinned model is unavailable: " + d.ModelReference.Key));
                return new RoomVisualPlan(p.Id, d.Reference.Key, scene, d.MeshTransform, RoomGeometryFunctions.PlacementTransform(d, p), !p.HiddenInEditor);
            }).ToArray();

        public static async Task<ViewportCheck[]> RunAsync(IncrementalRoomViewport viewport, RoomViewportCamera camera)
        {
            var checks = new List<ViewportCheck>();
            var sample = RoomSampleFixture.Create();
            var plans = Plans(sample.Document, sample.Resolve, d => sample.Models[d.Reference.Key]);
            await viewport.ReconcileAsync(plans);
            Check("VIEW-INITIAL-SHARED-WALL", () => viewport.PlacementCount == 28 && viewport.DefinitionCount == 6 &&
                viewport.ContentRoot.GetComponentsInChildren<PlacementMarker>().Count(p => p.PlacementId == "shared-door-wall") == 1);
            Check("VIEW-SHARED-MESH", () => ReferenceEquals(viewport.FindPlacement("floor-0-0").GetComponentInChildren<MeshFilter>().sharedMesh,
                viewport.FindPlacement("floor-1-0").GetComponentInChildren<MeshFilter>().sharedMesh));
            Physics.SyncTransforms();
            var door = viewport.FindPlacement("shared-door-wall").GetComponentsInChildren<MeshCollider>();
            Check("VIEW-DOOR-HOLE-REAL-PICKING", () => !door.Any(c => c.Raycast(new Ray(new Vector3(2, 1, 1.5f), Vector3.right), out _, 2)) &&
                door.Any(c => c.Raycast(new Ray(new Vector3(2, 1, .5f), Vector3.right), out _, 2)));
            var point = camera.Camera.WorldToViewportPoint(new Vector3(1, 0, 1));
            Check("VIEW-FLOOR-PROJECTION", () => camera.Floor(new Vector2(point.x, 1 - point.y), out var cell) && cell.X == 4 && cell.Z == 4);
            var seat = viewport.FindPlacement("seat"); var instanceId = seat.GetEntityId(); var before = seat.transform.position;
            var old = sample.Document.Placements.Single(p => p.Id == "seat");
            var moved = old.WithTransform(new GridPosition(old.Position.X + 1, old.Position.Y, old.Position.Z), old.QuarterTurns, old.Scale, old.PivotOffset);
            var doc = sample.Document.With(placements: sample.Document.Placements.Select(p => p.Id == old.Id ? moved : p).ToArray());
            plans = Plans(doc, sample.Resolve, d => sample.Models[d.Reference.Key]);
            await viewport.ReconcileAsync(plans);
            Check("VIEW-MOVE-RETAINS-INSTANCE", () => viewport.LastCreatedInstances == 0 && viewport.FindPlacement("seat").GetEntityId().Equals(instanceId) &&
                Vector3.Distance(viewport.FindPlacement("seat").transform.position, before + Vector3.right * .25f) < .00001f);

            var geometry = new ModelMesh("triangle", "Independent affine triangle", new[] { Double3.Zero, Double3.UnitX, Double3.UnitY },
                new[] { new ModelSubmesh(-1, new[] { 0, 1, 2 }) });
            var model = new ModelScene("affine-definition", new[] { new ModelNode("triangle-node", "Triangle", null, Matrix4d.Identity, geometry.Id) }, new[] { geometry });
            var definitionTransform = new Matrix4d(1, .5, 0, 1, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
            var placement = new PlacementTransform(new GridPosition(4, 0, 8), 1, new Double3(2, 1, 1), Double3.Zero, 0, definitionTransform);
            var affine = new RoomVisualPlan("affine-placement", "affine-definition-v1", model, definitionTransform, placement);
            var withAffine = plans.Concat(new[] { affine }).ToArray();
            await viewport.ReconcileAsync(withAffine);
            Check("VIEW-DEFINITION-TRANSFORM-ONCE", () => {
                var f = viewport.FindPlacement("affine-placement").GetComponentInChildren<MeshFilter>();
                return Vector3.Distance(f.transform.TransformPoint(f.sharedMesh.vertices[2]), new Vector3(1, 1, -1)) < .00001f;
            });
            var many = withAffine.Concat(Enumerable.Range(0, 400).Select(i => new RoomVisualPlan("pending-" + i, affine.DefinitionKey, model,
                definitionTransform, new PlacementTransform(new GridPosition(i, 0, 0), 0, Double3.One, Double3.Zero, 0, definitionTransform)))).ToArray();
            var cts = new CancellationTokenSource();
            var pending = viewport.ReconcileAsync(many, cts.Token); cts.Cancel();
            bool cancelled = false;
            try { await pending; } catch (OperationCanceledException) { cancelled = true; }
            Check("VIEW-CANCEL-NO-PARTIAL-PUBLISH", () => cancelled && viewport.PlacementCount == 29 && viewport.FindPlacement("pending-0") == null && viewport.FindPlacement("seat").GetEntityId().Equals(instanceId));
            cts.Dispose();
            var late = viewport.ReconcileAsync(many);
            await viewport.ReconcileAsync(withAffine);
            bool stale = false;
            try { await late; } catch (OperationCanceledException) { stale = true; }
            Check("VIEW-STALE-JOB-CANNOT-REPLACE", () => stale && viewport.PlacementCount == 29 && viewport.FindPlacement("pending-399") == null);
            var meshes = viewport.ContentRoot.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).Distinct().ToArray();
            var materials = viewport.ContentRoot.GetComponentsInChildren<Renderer>().SelectMany(r => r.sharedMaterials).Distinct().ToArray();
            await viewport.ReconcileAsync(Array.Empty<RoomVisualPlan>());
            for (var i = 0; i < 3; i++) await Task.Yield();
            Check("VIEW-EMPTY-RELEASES-NATIVE-RESOURCES", () => viewport.PlacementCount == 0 && viewport.DefinitionCount == 0 && meshes.All(m => m == null) && materials.All(m => m == null));
            await viewport.ReconcileAsync(Plans(sample.Document, sample.Resolve, d => sample.Models[d.Reference.Key]));
            return checks.ToArray();

            void Check(string id, Func<bool> action)
            {
                var item = new ViewportCheck { Id = id };
                try { item.Passed = action(); } catch (Exception error) { item.Detail = error.ToString(); }
                checks.Add(item);
            }
        }
    }
}
