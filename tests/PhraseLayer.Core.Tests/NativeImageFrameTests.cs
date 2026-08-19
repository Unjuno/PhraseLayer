using PhraseLayer.Core.Inputs;
using Xunit;

namespace PhraseLayer.Core.Tests
{
    public sealed class NativeImageFrameTests
    {
        [Fact]
        public void CpuFrameReportsCpuPixelsOnly()
        {
            var frame = new ImageFrame(new byte[] { 1, 2, 3, 4 }, 1, 1, 10, ImagePixelFormat.Rgba32);

            Assert.True(frame.HasCpuPixels);
            Assert.False(frame.HasNativePayload);
            Assert.Null(frame.NativePayload);
            Assert.Equal(4, frame.Pixels.Length);
        }

        [Fact]
        public void NativeFrameAvoidsSyntheticCpuCopy()
        {
            var payload = new FakePayload();
            var frame = new ImageFrame(payload, 1280, 960, 20);

            Assert.False(frame.HasCpuPixels);
            Assert.True(frame.HasNativePayload);
            Assert.Same(payload, frame.NativePayload);
            Assert.Empty(frame.Pixels);
            Assert.Equal(1280, frame.Width);
            Assert.Equal(960, frame.Height);
        }

        private sealed class FakePayload : IImageFramePayload { }
    }
}
