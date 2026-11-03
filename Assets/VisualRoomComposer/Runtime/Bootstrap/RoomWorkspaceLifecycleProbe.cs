using System;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using VisualRoomComposer.Core;
using VisualRoomComposer.Core.Documents;
using VisualRoomComposer.Core.RoomGeometry;
using VisualRoomComposer.Infrastructure.Persistence;

namespace VisualRoomComposer.Bootstrap
{
    public sealed partial class RoomWorkspaceController
    {
        private async Task ValidateWorkspaceLifecycleAsync()
        {
            var original = session;
            session.Apply("Lifecycle draft change", d => d.With(title: d.Title + " lifecycle"));
            var state = session.Document.StateId;
            var cursor = session.ProjectHistory.CurrentPosition;
            var meshes = viewport.ContentRoot.GetComponentsInChildren<MeshFilter>().Select(f => f.sharedMesh).Distinct().ToArray();
            var target = cameraView.Texture;
            var oldFont = font;
            var atlases = font.atlasTextures.ToArray();
            enabled = false;
            for (var i = 0; i < 3; i++) await Task.Yield();
            Add("UI-DISABLE-RELEASES-PRESENTATION", meshes.All(m => m == null) && target == null && oldFont == null && atlases.All(a => a == null));
            enabled = true;
            await AwaitUiProbeVisualAsync();
            Add("UI-REENABLE-PRESERVES-HISTORY", ReferenceEquals(original, session) && session.Document.StateId == state && session.ProjectHistory.CurrentPosition == cursor && session.ProjectHistory.IsDirty && viewport.PlacementCount == 28 && font != null && cameraView.Texture != null);
            session.ProjectHistory.Undo(); Refresh(); await AwaitUiProbeVisualAsync();

            var baseline = new ProjectBundle(session.Document, bundle.Definitions, bundle.Models, session.LibraryDraft);
            var bounds = new Bounds3d(Double3.Zero, Double3.One);
            var definition = FixedContent.SealDefinition(new LibraryDefinition(new PinnedReference("logical-marker", 1, new string('0', 64)), "Logical marker", DefinitionCategory.Prop, PlacementPolicy.Suspended, bounds, bounds, Matrix4d.Identity));
            var placement = new RoomPlacement("logical-marker", definition.Reference, new GridPosition(32, 4, 0));
            var document = session.Document.With(placements: session.Document.Placements.Concat(new[] { placement }).ToArray(), pinnedDependencies: session.Document.PinnedDependencies.Concat(new[] { definition.Reference }).ToArray());
            Adopt(new ProjectBundle(document, baseline.Definitions.Concat(new[] { definition }), baseline.Models, baseline.Draft));
            Refresh(); await AwaitUiProbeVisualAsync();
            selected.Add(placement.Id);
            Add("UI-NO-MODEL-DEFINITION", viewport.FindPlacement(placement.Id) != null && viewport.FindPlacement(placement.Id).GetComponentInChildren<MeshFilter>() == null && selected.Contains(placement.Id) && session.Document.Placements.Count == 29,
                "Logical definitions remain in the outline and document; no invented geometry is persisted.");
            Adopt(baseline); Refresh(); await AwaitUiProbeVisualAsync(); Frame();
        }

        private bool MatchesNativeGeometry()
        {
            var checkedVertices = 0;
            foreach (var placement in session.Document.Placements)
            {
                var definition = Resolve(placement.Definition);
                if (definition.ModelReference == null) continue;
                var world = PlacementTransformResolver.ResolveModelRoot(RoomGeometryFunctions.PlacementTransform(definition, placement));
                var expected = ModelGeometry.BakeSceneToWorld(Model(definition), world);
                var native = viewport.FindPlacement(placement.Id).GetComponentsInChildren<MeshFilter>(true);
                if (native.Length != expected.Meshes.Count) return false;
                for (var m = 0; m < native.Length; m++)
                {
                    var vertices = native[m].sharedMesh.vertices;
                    if (vertices.Length != expected.Meshes[m].Positions.Count) return false;
                    for (var v = 0; v < vertices.Length; v++)
                    {
                        var actual = native[m].transform.TransformPoint(vertices[v]);
                        var wanted = expected.Meshes[m].Positions[v];
                        if (Vector3.Distance(actual, new Vector3((float)wanted.X, (float)wanted.Y, (float)wanted.Z)) > .0001f) return false;
                        checkedVertices++;
                    }
                }
            }
            return checkedVertices > 0;
        }
    }
}
