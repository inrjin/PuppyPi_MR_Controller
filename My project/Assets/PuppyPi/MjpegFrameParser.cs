using System;

namespace PuppyPi
{
    // Multipart headers are discarded rather than buffered or trusted. JPEG segment
    // lengths distinguish metadata (which may contain thumbnails) from scan data.
    public sealed class MjpegFrameParser
    {
        private enum State { SeekingStart, MarkerPrefix, Marker, LengthHigh, LengthLow, Segment, Scan, ScanMarker }
        private readonly object _sync = new object();
        private readonly byte[] _buffer;
        private State _state;
        private bool _sawPrefix;
        private bool _closed;
        private int _length;
        private int _segmentLength;
        private int _remaining;
        private byte _marker;
        private byte[] _latest;
        private long _completed;
        private long _discarded;

        public MjpegFrameParser(int maximumFrameBytes = 2 * 1024 * 1024)
        {
            if (maximumFrameBytes < 4) throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));
            _buffer = new byte[maximumFrameBytes];
        }

        public long CompletedFrames { get { lock (_sync) return _completed; } }
        public long DiscardedFrames { get { lock (_sync) return _discarded; } }
        public int BufferedByteCount { get { lock (_sync) return _length + (_latest == null ? 0 : _latest.Length); } }

        public void Append(byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset > data.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            lock (_sync)
            {
                if (_closed) return;
                for (int index = offset; index < offset + count; index++) Consume(data[index]);
            }
        }

        public bool TryTakeLatest(out byte[] frame)
        {
            lock (_sync)
            {
                frame = _latest;
                _latest = null;
                return frame != null;
            }
        }

        public void Reset()
        {
            lock (_sync)
            {
                Clear();
                _closed = false;
            }
        }

        // An aborted handler must never publish a late frame into a new connection.
        public void Close()
        {
            lock (_sync)
            {
                Clear();
                _closed = true;
            }
        }

        private void Clear()
        {
            _state = State.SeekingStart;
            _sawPrefix = false;
            _length = 0;
            _remaining = 0;
            _latest = null;
        }

        private void StartFrame()
        {
            _buffer[0] = 0xff;
            _buffer[1] = 0xd8;
            _length = 2;
            _state = State.MarkerPrefix;
            _sawPrefix = false;
        }

        private void Reject(byte current)
        {
            _discarded++;
            _length = 0;
            _state = State.SeekingStart;
            _sawPrefix = current == 0xff;
        }

        private void Complete()
        {
            var frame = new byte[_length];
            Buffer.BlockCopy(_buffer, 0, frame, 0, _length);
            _latest = frame;
            _completed++;
            _length = 0;
            _state = State.SeekingStart;
            _sawPrefix = false;
        }

        private void Consume(byte value)
        {
            if (_state == State.SeekingStart)
            {
                if (_sawPrefix && value == 0xd8) StartFrame();
                else _sawPrefix = value == 0xff;
                return;
            }
            if (_length == _buffer.Length)
            {
                bool nextStart = _buffer[_length - 1] == 0xff && value == 0xd8;
                Reject(value);
                if (nextStart) StartFrame();
                return;
            }
            _buffer[_length++] = value;
            switch (_state)
            {
                case State.MarkerPrefix:
                    if (value == 0xff) _state = State.Marker;
                    else Reject(value);
                    break;
                case State.Marker:
                case State.ScanMarker:
                    bool inScan = _state == State.ScanMarker;
                    if (value == 0xff) break;
                    if (inScan && (value == 0x00 || value >= 0xd0 && value <= 0xd7))
                        _state = State.Scan;
                    else if (value == 0xd9) Complete();
                    else if (value == 0xd8)
                    {
                        _discarded++;
                        StartFrame();
                    }
                    else if (value == 0x01 || value >= 0xd0 && value <= 0xd7)
                        _state = inScan ? State.Scan : State.MarkerPrefix;
                    else if (value == 0x00) Reject(value);
                    else
                    {
                        _marker = value;
                        _state = State.LengthHigh;
                    }
                    break;
                case State.LengthHigh:
                    _segmentLength = value << 8;
                    _state = State.LengthLow;
                    break;
                case State.LengthLow:
                    _segmentLength |= value;
                    if (_segmentLength < 2) Reject(value);
                    else
                    {
                        _remaining = _segmentLength - 2;
                        _state = _remaining == 0
                            ? (_marker == 0xda ? State.Scan : State.MarkerPrefix) : State.Segment;
                    }
                    break;
                case State.Segment:
                    if (--_remaining == 0) _state = _marker == 0xda ? State.Scan : State.MarkerPrefix;
                    break;
                case State.Scan:
                    if (value == 0xff) _state = State.ScanMarker;
                    break;
            }
        }
    }

    public static class JpegFrameInfo
    {
        public const int MaximumHeaderBytes = 128 * 1024;

        public static bool TryGetDimensions(byte[] jpeg, int maximumDimension, long maximumPixels,
            out int width, out int height)
        {
            width = height = 0;
            if (jpeg == null || jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8 ||
                jpeg[jpeg.Length - 2] != 0xff || jpeg[jpeg.Length - 1] != 0xd9) return false;
            int position = 2;
            while (position < jpeg.Length && position < MaximumHeaderBytes)
            {
                if (jpeg[position++] != 0xff) return false;
                while (position < jpeg.Length && jpeg[position] == 0xff) position++;
                if (position >= jpeg.Length) return false;
                byte marker = jpeg[position++];
                if (marker == 0x00 || marker == 0xd8 || marker == 0xd9 || marker == 0xda) return false;
                if (marker == 0x01 || marker >= 0xd0 && marker <= 0xd7) continue;
                if (position + 1 >= jpeg.Length) return false;
                int length = jpeg[position] << 8 | jpeg[position + 1];
                if (length < 2 || length > jpeg.Length - position || position + length > MaximumHeaderBytes) return false;
                bool isSof = marker >= 0xc0 && marker <= 0xcf && marker != 0xc4 && marker != 0xc8 && marker != 0xcc;
                if (isSof)
                {
                    if (length < 11 || jpeg[position + 2] != 8) return false;
                    int components = jpeg[position + 7];
                    if ((components != 1 && components != 3) || length != 8 + components * 3) return false;
                    height = jpeg[position + 3] << 8 | jpeg[position + 4];
                    width = jpeg[position + 5] << 8 | jpeg[position + 6];
                    return width > 0 && height > 0 && width <= maximumDimension && height <= maximumDimension &&
                        (long)width * height <= maximumPixels;
                }
                position += length;
            }
            return false;
        }
    }
}
