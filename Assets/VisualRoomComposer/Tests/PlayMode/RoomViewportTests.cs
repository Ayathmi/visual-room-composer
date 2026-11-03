using System;
using System.Collections;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using VisualRoomComposer.Presentation;

namespace VisualRoomComposer.Tests
{
    [Category("VRC02Play")]
    public sealed class RoomViewportTests
    {
        [UnityTest] public IEnumerator NativeViewportCancellationPickingAndRelease()
        {
            var cameraObject = new GameObject("Viewport lifecycle test camera");
            var camera = new RoomViewportCamera(cameraObject.AddComponent<Camera>());
            var viewport = new IncrementalRoomViewport();
            try
            {
                var task = RoomViewportProbe.RunAsync(viewport, camera);
                var deadline = Time.realtimeSinceStartup + 30;
                while (!task.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
                Assert.That(task.IsCompleted, Is.True, "Viewport checks timed out.");
                Assert.That(task.IsFaulted, Is.False, task.Exception?.ToString());
                var checks = task.GetAwaiter().GetResult();
                var directory = Path.Combine(Directory.GetCurrentDirectory(), "Validation", "VRC-02", "EditorTests");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "viewport-" + Guid.NewGuid().ToString("N") + ".json"), JsonUtility.ToJson(new Result { Checks = checks }, true));
                Assert.That(checks.Length, Is.GreaterThanOrEqualTo(9));
                Assert.That(checks.Where(c => !c.Passed), Is.Empty, string.Join("\n", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail)));
            }
            finally
            {
                viewport.Dispose(); camera.Dispose(); UnityEngine.Object.Destroy(cameraObject);
            }
            yield return null;
        }
        [Serializable] private sealed class Result { public ViewportCheck[] Checks; }
    }
}
