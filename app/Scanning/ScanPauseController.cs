using System;
using System.Threading;

namespace DiskCleanupAssistant.Scanning
{
    public sealed class ScanPauseController : IDisposable
    {
        private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(true);
        public bool IsPaused { get; private set; }

        public void Pause()
        {
            IsPaused = true;
            _gate.Reset();
        }

        public void Resume()
        {
            IsPaused = false;
            _gate.Set();
        }

        public void Wait(CancellationToken token)
        {
            _gate.Wait(token);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }
    }
}
