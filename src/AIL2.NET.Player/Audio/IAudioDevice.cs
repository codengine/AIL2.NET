namespace AIL2.Player.Audio;

/// <summary>Provides buffered audio output for a single audio worker.</summary>
public interface IAudioDevice : IDisposable
{
    /// <summary>Gets whether a complete buffer can be queued without blocking; may reclaim consumed buffers.</summary>
    bool CanWrite { get; }

    /// <summary>Copies and queues complete signed 16-bit interleaved stereo frames.</summary>
    /// <param name="stereoFrames">The borrowed left/right samples at the device's configured frame rate.</param>
    /// <remarks>Call only when CanWrite is true. The caller may reuse the span after return.</remarks>
    void Write(ReadOnlySpan<short> stereoFrames);

    /// <summary>Suspends consumption while retaining queued audio.</summary>
    void Pause();

    /// <summary>Starts or resumes consumption of queued audio.</summary>
    void Resume();

    /// <summary>Stops consumption and discards all queued audio.</summary>
    void Clear();
}
