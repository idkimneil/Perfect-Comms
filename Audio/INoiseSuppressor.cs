using System;

namespace VoiceChatPlugin.Audio;

// Common surface for the native noise suppressors so MicPreprocessor can hold either one. Windows uses
// DeepFilterDenoiser (DeepFilterNet 3); other platforms use RnNoiseSuppressor.
internal interface INoiseSuppressor : IDisposable
{
    int FrameSize { get; }
    string NativePath { get; }
    bool TryProcessInPlace(float[] pcm, int sampleCount, out int processedFrames, out float speechProbabilityMax);
    void Reset();
}
