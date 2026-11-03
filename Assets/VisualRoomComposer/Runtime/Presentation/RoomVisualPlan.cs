using System;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Presentation
{
    /// <summary>A fixed CPU projection item. Definition geometry is baked once, placement remains an independent TRS.</summary>
    public sealed class RoomVisualPlan
    {
        public string PlacementId { get; }
        public string DefinitionKey { get; }
        public ModelScene Model { get; }
        public Matrix4d MeshTransform { get; }
        public PlacementTransform Placement { get; }
        public bool Visible { get; }
        public RoomVisualPlan(string placementId, string definitionKey, ModelScene model, Matrix4d meshTransform, PlacementTransform placement, bool visible = true)
        {
            if (string.IsNullOrWhiteSpace(placementId) || string.IsNullOrWhiteSpace(definitionKey)) throw new ArgumentException("Stable placement and pinned definition keys are required.");
            meshTransform.InverseAffine();
            PlacementTransformResolver.ResolvePlacementRoot(placement).InverseAffine();
            PlacementId = placementId; DefinitionKey = definitionKey; Model = model ?? throw new ArgumentNullException(nameof(model));
            MeshTransform = meshTransform; Placement = placement; Visible = visible;
        }
    }
}
