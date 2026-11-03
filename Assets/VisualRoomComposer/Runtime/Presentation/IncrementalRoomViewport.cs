using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using VisualRoomComposer.Core;

namespace VisualRoomComposer.Presentation
{
    /// <summary>Stages additions offscreen, atomically publishes a revision and retains unchanged objects/resources.</summary>
    public sealed class IncrementalRoomViewport : IDisposable
    {
        private sealed class Visual
        {
            internal GameObject Root;
            internal string Key;
        }
        private readonly Dictionary<string, Visual> visuals = new Dictionary<string, Visual>(StringComparer.Ordinal);
        private readonly Dictionary<string, UnityModelRealizer> definitions = new Dictionary<string, UnityModelRealizer>(StringComparer.Ordinal);
        private readonly GameObject root = new GameObject("Room viewport content");
        private readonly GameObject templates = new GameObject("Room viewport definitions");
        private readonly int layer;
        private long epoch;
        private bool disposed;
        public int PlacementCount => visuals.Count;
        public int DefinitionCount => definitions.Count;
        public int LastCreatedInstances { get; private set; }
        public Transform ContentRoot => root.transform;
        public IncrementalRoomViewport(int previewLayer = 0)
        {
            if (previewLayer < 0 || previewLayer > 31) throw new ArgumentOutOfRangeException(nameof(previewLayer));
            layer = previewLayer;
            templates.SetActive(false);
        }

        /// <summary>Must run on the Unity main thread; a late or cancelled job cannot replace current visuals.</summary>
        public async Task ReconcileAsync(IEnumerable<RoomVisualPlan> source, CancellationToken cancellation = default)
        {
            if (disposed) throw new ObjectDisposedException(nameof(IncrementalRoomViewport));
            var version = ++epoch;
            var plans = source.ToArray();
            if (plans.Length > 100000 || plans.Select(p => p.PlacementId).Distinct(StringComparer.Ordinal).Count() != plans.Length)
                throw new ArgumentException("Placement identity/count is invalid.");
            foreach (var plan in plans) RequirePlacement(plan.Placement);
            var staging = new GameObject("Room viewport pending revision");
            staging.SetActive(false);
            var added = new Dictionary<string, Visual>(StringComparer.Ordinal);
            var prepared = new Dictionary<string, UnityModelRealizer>(StringComparer.Ordinal);
            var created = 0;
            try
            {
                var clock = Stopwatch.StartNew();
                var batchCount = 0;
                foreach (var plan in plans)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (disposed || version != epoch) throw new OperationCanceledException("A newer viewport revision superseded this projection.");
                    if (visuals.TryGetValue(plan.PlacementId, out var existing) && existing.Key == plan.DefinitionKey) continue;
                    if (!definitions.TryGetValue(plan.DefinitionKey, out var definition) && !prepared.TryGetValue(plan.DefinitionKey, out definition))
                    {
                        var baked = ModelGeometry.BakeSceneToWorld(plan.Model, plan.MeshTransform);
                        definition = UnityModelRealizer.Create(baked);
                        definition.Root.SetActive(false);
                        definition.Root.transform.SetParent(templates.transform, false);
                        prepared.Add(plan.DefinitionKey, definition);
                    }
                    var instance = UnityEngine.Object.Instantiate(definition.Root, staging.transform, false);
                    instance.name = plan.PlacementId;
                    foreach (var child in instance.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
                    instance.AddComponent<PlacementMarker>().PlacementId = plan.PlacementId;
                    AddPickingGeometry(instance);
                    instance.SetActive(plan.Visible);
                    Apply(instance, plan.Placement);
                    added.Add(plan.PlacementId, new Visual { Root = instance, Key = plan.DefinitionKey });
                    created++;
                    if (++batchCount >= 64 || clock.ElapsedMilliseconds >= 5) { await Task.Yield(); clock.Restart(); batchCount = 0; }
                }
                cancellation.ThrowIfCancellationRequested();
                if (disposed || version != epoch) throw new OperationCanceledException("A newer viewport revision superseded this projection.");
                var ids = plans.Select(p => p.PlacementId).ToHashSet(StringComparer.Ordinal);
                foreach (var id in visuals.Keys.Where(id => !ids.Contains(id) || added.ContainsKey(id)).ToArray())
                {
                    visuals[id].Root.SetActive(false);
                    UnityEngine.Object.Destroy(visuals[id].Root);
                    visuals.Remove(id);
                }
                foreach (var pair in prepared) definitions.Add(pair.Key, pair.Value);
                prepared.Clear();
                foreach (var plan in plans)
                {
                    if (added.TryGetValue(plan.PlacementId, out var next))
                    {
                        next.Root.transform.SetParent(root.transform, false);
                        visuals.Add(plan.PlacementId, next);
                    }
                    var visual = visuals[plan.PlacementId];
                    Apply(visual.Root, plan.Placement);
                    visual.Root.SetActive(plan.Visible);
                }
                added.Clear();
                var used = plans.Select(p => p.DefinitionKey).ToHashSet(StringComparer.Ordinal);
                foreach (var key in definitions.Keys.Where(key => !used.Contains(key)).ToArray()) { definitions[key].Dispose(); definitions.Remove(key); }
                LastCreatedInstances = created;
            }
            finally
            {
                foreach (var visual in added.Values) UnityEngine.Object.Destroy(visual.Root);
                foreach (var definition in prepared.Values) definition.Dispose();
                UnityEngine.Object.Destroy(staging);
            }
        }

        public GameObject FindPlacement(string id) => visuals.TryGetValue(id, out var value) ? value.Root : null;
        public string Pick(Camera camera, Vector2 normalized)
        {
            Physics.SyncTransforms();
            var ray = camera.ViewportPointToRay(new Vector3(normalized.x, 1 - normalized.y));
            var hits = Physics.RaycastAll(ray, 1000, 1 << layer).OrderBy(h => h.distance);
            foreach (var hit in hits)
            {
                var marker = hit.transform.GetComponentInParent<PlacementMarker>();
                if (marker != null && visuals.ContainsKey(marker.PlacementId)) return marker.PlacementId;
            }
            return null;
        }

        private static void AddPickingGeometry(GameObject instance)
        {
            var filters = instance.GetComponentsInChildren<MeshFilter>(true);
            foreach (var filter in filters)
                filter.gameObject.AddComponent<MeshCollider>().sharedMesh = filter.sharedMesh;
        }
        private static void Apply(GameObject instance, PlacementTransform placement)
        {
            var p = PlacementTransformResolver.ResolvePlacementRoot(placement).TranslationPart;
            instance.transform.localPosition = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            instance.transform.localRotation = Quaternion.Euler(0, placement.QuarterTurns * 90, 0);
            instance.transform.localScale = new Vector3((float)placement.Scale.X, (float)placement.Scale.Y, (float)placement.Scale.Z);
        }
        private static void RequirePlacement(PlacementTransform placement)
        {
            var m = PlacementTransformResolver.ResolvePlacementRoot(placement);
            foreach (var value in m.ToRowMajorArray()) if (Math.Abs(value) > float.MaxValue) throw new ArgumentException("Placement exceeds the Unity coordinate range.");
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; ++epoch;
            root.SetActive(false);
            UnityEngine.Object.Destroy(root); UnityEngine.Object.Destroy(templates);
            foreach (var definition in definitions.Values) definition.Dispose();
            definitions.Clear(); visuals.Clear();
        }
    }
}
