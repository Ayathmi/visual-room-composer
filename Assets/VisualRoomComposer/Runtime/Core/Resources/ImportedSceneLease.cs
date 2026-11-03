using System;

namespace VisualRoomComposer.Core
{
    /// <summary>An immutable model lease. Each owner disposes its own retained handle exactly once.</summary>
    public sealed class ImportedSceneLease : IDisposable
    {
        private sealed class SharedState
        {
            internal readonly object Gate = new object();
            internal ModelScene Scene;
            internal Action OnLastRelease;
            internal int Count = 1;
            internal SharedState(ModelScene scene, Action onLastRelease) { Scene = scene; OnLastRelease = onLastRelease; }
        }
        private readonly SharedState state;
        private bool disposed;
        private ImportedSceneLease(SharedState state) { this.state = state; }
        public static ImportedSceneLease Create(ModelScene scene, Action onLastRelease = null)
        {
            if (scene == null) throw new ArgumentNullException(nameof(scene));
            return new ImportedSceneLease(new SharedState(scene, onLastRelease));
        }
        public ModelScene Scene
        {
            get { lock (state.Gate) { Require(); return state.Scene; } }
        }
        public bool IsDisposed { get { lock (state.Gate) return disposed; } }
        public ImportedSceneLease Retain()
        {
            lock (state.Gate)
            {
                Require();
                if (state.Count == int.MaxValue) throw new InvalidOperationException("Too many model leases.");
                state.Count++;
                return new ImportedSceneLease(state);
            }
        }
        public void Dispose()
        {
            Action release = null;
            lock (state.Gate)
            {
                if (disposed) return;
                disposed = true; state.Count--;
                if (state.Count == 0)
                {
                    release = state.OnLastRelease;
                    state.Scene = null;
                    state.OnLastRelease = null;
                }
            }
            // A Unity adapter must marshal its release callback to the main thread.
            release?.Invoke();
        }
        private void Require()
        {
            if (disposed) throw new ObjectDisposedException(nameof(ImportedSceneLease));
        }
    }
}
