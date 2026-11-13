using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace PuppyPi
{
    // Unity serializes on its main thread. Only strings cross the bounded queue.
    public sealed class StudyJournal : IDisposable
    {
        private readonly BlockingCollection<string> _queue;
        private readonly Thread _worker;
        private long _dropped;
        private long _written;
        private volatile string _error;
        private volatile bool _closed;
        public long DroppedCount => Interlocked.Read(ref _dropped);
        public long WrittenCount => Interlocked.Read(ref _written);
        public string Error => _error;
        public bool Healthy => !_closed && _error == null && DroppedCount == 0;
        public bool IsComplete => !_worker.IsAlive && _error == null && DroppedCount == 0;
        public string Path { get; }

        public StudyJournal(string path, int capacity = 1024)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            Path = path;
            _queue = new BlockingCollection<string>(capacity);
            // Open synchronously once so a missing/writable log path fails before readiness.
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)));
            var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _worker = new Thread(() => WriteLoop(writer)) { IsBackground = true, Name = "PuppyPi study journal" };
            _worker.Start();
        }

        public bool Enqueue(string json)
        {
            if (_closed || _error != null || string.IsNullOrEmpty(json)) return false;
            try
            {
                if (_queue.TryAdd(json)) return true;
            }
            catch (InvalidOperationException) { }
            Interlocked.Increment(ref _dropped);
            return false;
        }

        private void WriteLoop(StreamWriter writer)
        {
            try
            {
                using (writer)
                {
                    foreach (string line in _queue.GetConsumingEnumerable())
                    {
                        writer.WriteLine(line);
                        Interlocked.Increment(ref _written);
                        if (WrittenCount % 20 == 0) writer.Flush();
                    }
                    writer.Flush();
                }
            }
            catch (Exception e) { _error = e.GetType().Name + ": " + e.Message; }
        }

        public bool Close(int waitMilliseconds = 100)
        {
            if (!_closed)
            {
                _closed = true;
                _queue.CompleteAdding();
            }
            // A slow filesystem never makes the control/main thread wait indefinitely.
            return _worker.Join(Math.Max(0, Math.Min(1000, waitMilliseconds))) && IsComplete;
        }

        public void Dispose() { Close(); }
    }
}
