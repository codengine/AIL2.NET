namespace AIL2.Music;

/// <summary>Implements the SOUNDFX.C effect behavior, including its 10 ms service interval and reserved MIDI channel.</summary>
public sealed class MidiEffect : IDisposable
{
    /// <summary>The session owning the reserved channel and effect service.</summary>
    private readonly AILSession _session;

    /// <summary>The original duration counter, decremented by ten milliseconds per service.</summary>
    private long _remainingMilliseconds;

    /// <summary>Whether the reserved channel has been released.</summary>
    private bool _stopped;

    /// <summary>Gets the zero-based channel reserved for this effect.</summary>
    public int Channel { get; }

    /// <summary>Gets whether the effect still owns its channel.</summary>
    public bool IsPlaying => !_stopped;

    /// <summary>Retains a newly reserved channel and the original duration counter.</summary>
    /// <param name="session">The owning session.</param>
    /// <param name="channel">The zero-based reserved channel.</param>
    /// <param name="duration">The duration truncated to whole milliseconds.</param>
    internal MidiEffect(AILSession session, int channel, TimeSpan duration)
    {
        _session = session;
        Channel = channel;
        _remainingMilliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>Emits the original helper's program, pitch, pan, volume and note-on sequence.</summary>
    /// <param name="program">The MIDI program.</param>
    /// <param name="note">The MIDI note.</param>
    /// <param name="volume">The channel volume.</param>
    /// <param name="pan">The pan value passed to the original helper.</param>
    internal void Start(byte program, byte note, byte volume, byte pan)
    {
        // SOUNDFX.C passes the one-based lock result directly into the MIDI status nibble.
        var messageChannel = Channel + 1;
        _session.SendChannelMessage((byte)(0xc0 | messageChannel), program);
        _session.SendChannelMessage((byte)(0xe0 | messageChannel), 0, 64);
        _session.SendChannelMessage((byte)(0xb0 | messageChannel), 10, pan);
        _session.SendChannelMessage((byte)(0xb0 | messageChannel), 7, volume);
        _session.SendChannelMessage((byte)(0x90 | messageChannel), note, 127);
    }

    /// <summary>Decrements duration by 10 ms and releases expired effects.</summary>
    internal void Service()
    {
        _remainingMilliseconds -= 10;
        if (_remainingMilliseconds <= 0)
        {
            Dispose();
        }
    }

    /// <summary>Releases the reserved channel and removes this effect from the session.</summary>
    /// <remarks>Repeated disposal does nothing. The session also disposes expired effects. Call on its owning thread.</remarks>
    public void Dispose()
    {
        if (_stopped)
        {
            return;
        }

        _session.CheckAccess();
        _session.ReleaseChannel(Channel);
        _session.RemoveEffect(this);
        _stopped = true;
    }
}
