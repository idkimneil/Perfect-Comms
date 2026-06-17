using System;

namespace VoiceChatPlugin.Audio;

// Surface for the native noise suppressor so MicPreprocessor can hold it behind a platform-neutral type
// (the concrete DeepFilterDenoiser is Windows-only). Non-Windows builds currently have no native suppressor.
internal interface INoiseSuppressor : IDisposable
{
    int FrameSize { get; }
    string NativePath { get; }
    bool TryProcessInPlace(float[] pcm, int sampleCount, out int processedFrames, out float speechProbabilityMax);
    void Reset();
}
