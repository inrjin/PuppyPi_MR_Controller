using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace PuppyPi.Tests
{
    public class CameraStreamTests
    {
        [TestCase(1)]
        [TestCase(7)]
        [TestCase(64)]
        public void MultipartFramesSurviveFragmentedHeadersAndJpegMarkers(int fragmentSize)
        {
            var frame = Jpeg(0x21);
            var bytes = Multipart(frame, frame.Length.ToString());
            var parser = new MjpegFrameParser(1024);
            for (int offset = 0; offset < bytes.Length; offset += fragmentSize)
                parser.Append(bytes, offset, Math.Min(fragmentSize, bytes.Length - offset));
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(frame));
            Assert.That(parser.CompletedFrames, Is.EqualTo(1));
        }

        [TestCase("-1")]
        [TestCase("2147483647")]
        [TestCase("invalid")]
        [TestCase("1")]
        public void UntrustedContentLengthCannotAllocateOrTruncateAFrame(string contentLength)
        {
            var parser = new MjpegFrameParser(1024);
            var frame = Jpeg(0x31);
            Append(parser, Multipart(frame, contentLength));
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(frame));
            Assert.That(parser.BufferedByteCount, Is.Zero);
        }

        [Test]
        public void MultipartPreambleIsNotBuffered()
        {
            var parser = new MjpegFrameParser(128);
            Append(parser, Encoding.ASCII.GetBytes("--camera\r\nX-Untrusted: " + new string('x', 256 * 1024)));
            Assert.That(parser.BufferedByteCount, Is.Zero);
            var frame = Jpeg(0x41);
            Append(parser, Encoding.ASCII.GetBytes("\r\n\r\n"));
            Append(parser, frame);
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(frame));
        }

        [Test]
        public void ThumbnailMarkersInsideMetadataDoNotFinishTheOuterFrame()
        {
            var parser = new MjpegFrameParser(1024);
            var frame = Jpeg(0x51, metadata: new byte[] { 1, 0xff, 0xd8, 2, 0xff, 0xd9, 3 });
            Append(parser, frame);
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(frame));
            Assert.That(parser.CompletedFrames, Is.EqualTo(1));
        }

        [Test]
        public void StuffedScanBytesAndRestartMarkersRemainInTheFrame()
        {
            var parser = new MjpegFrameParser(1024);
            var frame = Jpeg(0x61);
            var extraScan = new byte[] { 0xff, 0x00, 0xd9, 0xff, 0xd0, 0x11 };
            frame = frame.Take(frame.Length - 2).Concat(extraScan).Concat(new byte[] { 0xff, 0xd9 }).ToArray();
            Append(parser, frame);
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(frame));
        }

        [Test]
        public void TruncatedAndInvalidFramesResynchronizeAtTheNextImage()
        {
            var parser = new MjpegFrameParser(1024);
            var truncated = Jpeg(0x71);
            parser.Append(truncated, 0, truncated.Length - 2);
            var good = Jpeg(0x72);
            Append(parser, Multipart(good, good.Length.ToString()));
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(good));
            Assert.That(parser.DiscardedFrames, Is.GreaterThanOrEqualTo(1));

            Append(parser, new byte[] { 0xff, 0xd8, 0xff, 0xe1, 0, 1 });
            Append(parser, good);
            Assert.That(parser.TryTakeLatest(out result), Is.True);
            Assert.That(result, Is.EqualTo(good));
        }

        [Test]
        public void OversizedFrameIsDroppedWithoutBlockingTheNextFrame()
        {
            var parser = new MjpegFrameParser(64);
            var oversized = Jpeg(0x11);
            oversized = oversized.Take(oversized.Length - 2).Concat(new byte[512])
                .Concat(new byte[] { 0xff, 0xd9 }).ToArray();
            Append(parser, oversized);
            Assert.That(parser.BufferedByteCount, Is.LessThanOrEqualTo(64));
            var good = Jpeg(0x12);
            Append(parser, good);
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(good));
            Assert.That(parser.CompletedFrames, Is.EqualTo(1));
            Assert.That(parser.DiscardedFrames, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void PendingQueueKeepsOnlyTheNewestCompleteFrame()
        {
            var parser = new MjpegFrameParser(128);
            Append(parser, Jpeg(0x21));
            Append(parser, Jpeg(0x22));
            var newest = Jpeg(0x23);
            Append(parser, newest);
            Assert.That(parser.CompletedFrames, Is.EqualTo(3));
            Assert.That(parser.BufferedByteCount, Is.EqualTo(newest.Length));
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(newest));
            Assert.That(parser.TryTakeLatest(out _), Is.False);
        }

        [Test]
        public void ResetAndClosedHandlersCannotLeakOldHostFrames()
        {
            var parser = new MjpegFrameParser(128);
            Append(parser, Jpeg(0x31));
            var partial = Jpeg(0x32);
            parser.Append(partial, 0, partial.Length / 2);
            parser.Reset();
            Assert.That(parser.TryTakeLatest(out _), Is.False);
            parser.Append(partial, partial.Length / 2, partial.Length - partial.Length / 2);
            Assert.That(parser.TryTakeLatest(out _), Is.False);
            var fresh = Jpeg(0x33);
            Append(parser, fresh);
            Assert.That(parser.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(fresh));
            parser.Close();
            Append(parser, fresh);
            Assert.That(parser.TryTakeLatest(out _), Is.False);
            Assert.That(parser.BufferedByteCount, Is.Zero);
        }

        [Test]
        public void JpegDimensionsAreCheckedBeforeNativeTextureDecode()
        {
            Assert.That(JpegFrameInfo.TryGetDimensions(Jpeg(0x41, 640, 480), 2048, 2097152,
                out int width, out int height), Is.True);
            Assert.That(width, Is.EqualTo(640));
            Assert.That(height, Is.EqualTo(480));
            Assert.That(JpegFrameInfo.TryGetDimensions(Jpeg(0x42, 65535, 480), 2048, 2097152,
                out _, out _), Is.False);
            Assert.That(JpegFrameInfo.TryGetDimensions(Jpeg(0x43, 2048, 2048), 2048, 2097152,
                out _, out _), Is.False);
            Assert.That(JpegFrameInfo.TryGetDimensions(Jpeg(0x44, 0, 480), 2048, 2097152,
                out _, out _), Is.False);
            Assert.That(JpegFrameInfo.TryGetDimensions(new byte[] { 0xff, 0xd8, 0xff, 0xd9 }, 2048, 2097152,
                out _, out _), Is.False);
        }

        [Test]
        public void ExcessiveJpegMetadataIsRejectedBeforeNativeTextureDecode()
        {
            var bytes = new List<byte> { 0xff, 0xd8 };
            for (int index = 0; index < 3; index++) Segment(bytes, 0xe1, new byte[60000]);
            bytes.AddRange(Jpeg(0x51).Skip(2));
            Assert.That(JpegFrameInfo.TryGetDimensions(bytes.ToArray(), 2048, 2097152,
                out _, out _), Is.False);
        }

        [Test]
        public void StreamAddressUsesTheReadOnlyRosCameraEndpoint()
        {
            var uri = new Uri(PuppyPiCameraStream.BuildStreamUrl("192.168.100.153"));
            Assert.That(uri.Scheme, Is.EqualTo("http"));
            Assert.That(uri.Port, Is.EqualTo(8080));
            Assert.That(uri.AbsolutePath, Is.EqualTo("/stream"));
            Assert.That(uri.Query, Is.EqualTo("?topic=/usb_cam/image_raw&type=ros_compressed"),
                "The robot's web_video_server returns an empty stream for percent-encoded topic slashes.");
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl(""));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("http://robot/other"));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("robot@other"));
        }

        [Test]
        public void MapStreamUsesTheSameBoundedTransportWithItsOwnTopic()
        {
            var uri = new Uri(PuppyPiCameraStream.BuildStreamUrl("192.168.100.153", "/lidar_map/image"));
            Assert.That(uri.Port, Is.EqualTo(8080));
            Assert.That(uri.Query, Is.EqualTo("?topic=/lidar_map/image&type=ros_compressed"));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("robot", ""));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("robot", "image"));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("robot", "/image&type=h264"));
            Assert.Throws<ArgumentException>(() => PuppyPiCameraStream.BuildStreamUrl("robot", "/image\r\nHeader: injected"));
        }

        [Test]
        public void IndependentCameraAndMapParsersDoNotShareQueuedFrames()
        {
            var camera = new MjpegFrameParser(128);
            var map = new MjpegFrameParser(128);
            var cameraFrame = Jpeg(0x61);
            var mapFrame = Jpeg(0x62);
            Append(camera, cameraFrame);
            Append(map, mapFrame);
            camera.Close();
            Assert.That(camera.TryTakeLatest(out _), Is.False);
            Assert.That(map.TryTakeLatest(out byte[] result), Is.True);
            Assert.That(result, Is.EqualTo(mapFrame));
        }

        private static void Append(MjpegFrameParser parser, byte[] bytes) => parser.Append(bytes, 0, bytes.Length);

        private static byte[] Multipart(byte[] frame, string length) =>
            Encoding.ASCII.GetBytes("--camera\r\nContent-Type: image/jpeg\r\nContent-Length: " + length + "\r\n\r\n")
                .Concat(frame).Concat(Encoding.ASCII.GetBytes("\r\n--camera\r\n")).ToArray();

        private static byte[] Jpeg(byte tag, int width = 640, int height = 480, byte[] metadata = null)
        {
            var bytes = new List<byte> { 0xff, 0xd8 };
            if (metadata != null) Segment(bytes, 0xe1, metadata);
            Segment(bytes, 0xc0, new byte[] { 8, (byte)(height >> 8), (byte)height,
                (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0 });
            Segment(bytes, 0xda, new byte[] { 1, 1, 0, 0, 63, 0 });
            bytes.Add(tag);
            bytes.Add(0xff);
            bytes.Add(0xd9);
            return bytes.ToArray();
        }

        private static void Segment(List<byte> bytes, byte marker, byte[] payload)
        {
            int length = payload.Length + 2;
            bytes.Add(0xff);
            bytes.Add(marker);
            bytes.Add((byte)(length >> 8));
            bytes.Add((byte)length);
            bytes.AddRange(payload);
        }
    }
}
