using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VisualRoomComposer.Formats.Fbx
{
    [Serializable] public sealed class FbxGuardResult
    {
        public string Id;
        public string Status;
        public string ObservedCode;
        public string ExpectedCode;
    }
    /// <summary>Portable negative-case probe. This does not exercise the future user-facing publish transaction.</summary>
    public static class FbxGuardProbe
    {
        public static FbxGuardResult[] Run(string validInput, string negativeDirectory, string newScratchDirectory)
        {
            if (Directory.Exists(newScratchDirectory)) throw new IOException("Guard scratch directory must be new.");
            Directory.CreateDirectory(newScratchDirectory); var results = new List<FbxGuardResult>();
            var cases = new Dictionary<string, string> {
                { "truncated.fbx", "fbx.truncated" }, { "offset-outside-source.fbx", "fbx.record.bounds" },
                { "unsupported-version.fbx", "fbx.version.not-implemented" }, { "unsupported-inheritance.fbx", "fbx.NotImplemented" },
                { "unsupported-rotation-order.fbx", "fbx.NotImplemented" }, { "texture-path-escape.fbx", "fbx.path.denied" }
            };
            foreach (var item in cases) results.Add(Expect(item.Key, item.Value, () => FbxModelReader.Read(Path.Combine(negativeDirectory, item.Key))));
            results.Add(Expect("source-byte-budget", "fbx.budget.source", () => FbxModelReader.Read(validInput, new FbxReadOptions { MaximumSourceBytes = 1 })));
            results.Add(Expect("decoded-byte-budget", "fbx.budget.bytes", () => FbxModelReader.Read(validInput, new FbxReadOptions { MaximumDecodedBytes = 1 })));
            var canceled = new CancellationToken(true);
            results.Add(Expect("reader-canceled-before-open", "OperationCanceledException", () => FbxModelReader.Read(validInput, new FbxReadOptions { Cancellation = canceled })));
            var scene = FbxModelReader.Read(validInput);
            string canceledOutput = Path.Combine(newScratchDirectory, "canceled"); Directory.CreateDirectory(canceledOutput);
            results.Add(Expect("writer-canceled-before-create", "OperationCanceledException", () => FbxBinaryWriter.Write(scene, Path.Combine(canceledOutput, "out.fbx"), new FbxWriteOptions { Cancellation = canceled })));
            string budgetOutput = Path.Combine(newScratchDirectory, "budget"); Directory.CreateDirectory(budgetOutput);
            results.Add(Expect("writer-working-budget", "fbx.budget.working", () => FbxBinaryWriter.Write(scene, Path.Combine(budgetOutput, "out.fbx"), new FbxWriteOptions { MaximumWorkingBytes = 1 })));
            results.Add(Expect("writer-output-budget", "fbx.budget.output", () => FbxBinaryWriter.Write(scene, Path.Combine(budgetOutput, "out.fbx"), new FbxWriteOptions { MaximumOutputBytes = 1 })));
            string existing = Path.Combine(newScratchDirectory, "existing"); Directory.CreateDirectory(existing);
            string sentinel = Path.Combine(existing, "out.fbx"); File.WriteAllText(sentinel, "Keep existing bytes.");
            results.Add(Expect("writer-refuses-existing-output", "IOException", () => FbxBinaryWriter.Write(scene, sentinel)));
            results.Add(new FbxGuardResult { Id = "existing-output-preserved", ExpectedCode = "Unchanged", ObservedCode = File.ReadAllText(sentinel) == "Keep existing bytes." ? "Unchanged" : "Changed",
                Status = File.ReadAllText(sentinel) == "Keep existing bytes." ? "Passed" : "Failed" });
            bool empty = Directory.GetFileSystemEntries(canceledOutput).Length == 0 && Directory.GetFileSystemEntries(budgetOutput).Length == 0;
            results.Add(new FbxGuardResult { Id = "precommit-failure-no-files", ExpectedCode = "Empty", ObservedCode = empty ? "Empty" : "NotEmpty", Status = empty ? "Passed" : "Failed" });
            return results.ToArray();
        }
        private static FbxGuardResult Expect(string id, string expectedCode, Action action)
        {
            string observed = "NoRejection";
            try { action(); }
            catch (Exception exception) when (!(exception is OutOfMemoryException))
            { observed = exception is FbxFormatException fbx ? fbx.Code : exception.GetType().Name; }
            return new FbxGuardResult { Id = id, ExpectedCode = expectedCode, ObservedCode = observed, Status = observed == expectedCode ? "Passed" : "Failed" };
        }
    }
}
