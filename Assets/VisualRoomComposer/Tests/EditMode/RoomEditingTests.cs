using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using VisualRoomComposer.Application.Diagnostics;
using VisualRoomComposer.Infrastructure.Diagnostics;

namespace VisualRoomComposer.Tests
{
    /// <summary>Runs the independent state/oracle checks under the actual Editor BCL and test runner.</summary>
    [Category("VRC02Edit")]
    public sealed class RoomEditingTests
    {
        [Test] public async Task HistoriesRemainIndependentAndTransactional()
        {
            var report = await EditingProbe.RunAsync();
            Assert.That(report.Checks.Count, Is.GreaterThanOrEqualTo(26));
            Assert.That(report.Failed, Is.Zero, string.Join("\n", report.Checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail)));
        }
        [Test] public void GeometryAndAttachmentCommandsMatchOriginalExpectations()
        {
            var checks = RoomGeometryProbe.Run();
            Assert.That(checks.Count, Is.GreaterThanOrEqualTo(32));
            Assert.That(checks.Where(c => !c.Passed), Is.Empty, string.Join("\n", checks.Where(c => !c.Passed).Select(c => c.Id + ": " + c.Detail)));
        }
        [Test] public void SavedBundlesAndImmutableLibrariesSurviveReopen()
        {
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Validation", "VRC-02", "EditorTests", "storage-" + Guid.NewGuid().ToString("N"));
            var seed = StorageProbe.RunSeed(path);
            Assert.That(seed.failed, Is.Zero, string.Join("\n", seed.checks.Where(c => c.status != "Passed").Select(c => c.id + ": " + c.detail)));
            Assert.That(seed.blocked, Is.Zero, "Actual OS reparse checks must execute.");
            var reopened = StorageProbe.RunReopen(path);
            Assert.That(reopened.failed, Is.Zero);
            Assert.That(reopened.blocked, Is.Zero);
            Assert.That(reopened.projectHash, Is.EqualTo(seed.projectHash));
        }
    }
}
