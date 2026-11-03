using System;
using System.IO;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace VisualRoomComposer.Tests
{
    /// <summary>Retains the full NUnit XML across Editor/PlayMode domain reloads.</summary>
    [InitializeOnLoad]
    public sealed class RoomBatchTestRunner : ICallbacks
    {
        private const string OutputKey = "VRC02.TestOutput";
        static RoomBatchTestRunner() { TestRunnerApi.RegisterTestCallback(new RoomBatchTestRunner()); }
        public static string Begin(string mode, string outputDirectory)
        {
            if (!string.IsNullOrEmpty(SessionState.GetString(OutputKey, ""))) throw new InvalidOperationException("A VRC test run is still pending.");
            if (Directory.Exists(outputDirectory)) throw new IOException("Use a fresh test evidence directory.");
            Directory.CreateDirectory(outputDirectory);
            SessionState.SetString(OutputKey, Path.GetFullPath(outputDirectory));
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            try { return api.Execute(new ExecutionSettings(new Filter { testMode = mode == "edit" ? TestMode.EditMode : TestMode.PlayMode, categoryNames = new[] { mode == "edit" ? "VRC02Edit" : "VRC02Play" } })); }
            catch { SessionState.EraseString(OutputKey); throw; }
            finally { UnityEngine.Object.DestroyImmediate(api); }
        }
        public void RunStarted(ITestAdaptor testsToRun) { }
        public void TestStarted(ITestAdaptor test) { }
        public void TestFinished(ITestResultAdaptor result) { }
        public void RunFinished(ITestResultAdaptor result)
        {
            var output = SessionState.GetString(OutputKey, "");
            if (string.IsNullOrEmpty(output)) return;
            TestRunnerApi.SaveResultToFile(result, Path.Combine(output, "TestResults.xml"));
            File.WriteAllText(Path.Combine(output, "completed.txt"), "state=" + result.ResultState + "\npassed=" + result.PassCount + "\nfailed=" + result.FailCount + "\nskipped=" + result.SkipCount);
            SessionState.EraseString(OutputKey);
        }
    }
}
