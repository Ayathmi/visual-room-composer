using UnityEngine;

namespace VisualRoomComposer.Presentation
{
    /// <summary>Stable selection identity; never uses display name as an authoring key.</summary>
    public sealed class PlacementMarker : MonoBehaviour
    {
        public string PlacementId { get; set; }
    }
}
