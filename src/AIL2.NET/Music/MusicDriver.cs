namespace AIL2.Music;

/// <summary>Interprets Miles music events and sends them through the session's output interfaces.</summary>
/// <param name="session">The session owning time, controls and playback state.</param>
internal abstract class MusicDriver(AILSession session)
{
    /// <summary>Gets the owning session and its logical clock.</summary>
    protected AILSession Session { get; } = session;

    /// <summary>Gets the byte-limited cache capacity, or zero when entries are limited by slots instead.</summary>
    public abstract int CacheCapacity { get; }

    /// <summary>Emits the original initialization messages.</summary>
    public abstract void Initialize();

    /// <summary>Emits the original shutdown/reset messages.</summary>
    public abstract void Shutdown();

    /// <summary>Tests whether a timbre is resident or built in.</summary>
    /// <param name="key">The bank/patch key, with bank in the high byte.</param>
    /// <returns>True when the driver can resolve the key.</returns>
    public abstract bool HasTimbre(ushort key);

    /// <summary>Installs a missing timbre using the original driver's cache and patch behavior.</summary>
    /// <param name="key">The bank/patch key, with bank in the high byte.</param>
    /// <param name="bytes">The original length-prefixed definition.</param>
    /// <exception cref="InvalidDataException">An MT-32 definition is too short.</exception>
    /// <exception cref="NotSupportedException">An FM definition uses an unsupported format.</exception>
    /// <exception cref="InvalidOperationException">All eviction candidates are protected.</exception>
    public abstract void Install(ushort key, ReadOnlyMemory<byte> bytes);

    /// <summary>Changes eviction protection for one or all resident timbres.</summary>
    /// <param name="key">The bank/patch key, or null to select all entries.</param>
    /// <param name="protect">Whether selected entries should resist eviction.</param>
    public abstract void Protect(ushort? key, bool protect);

    /// <summary>Interprets a MIDI channel message according to the original driver.</summary>
    /// <param name="status">The channel status byte, including the zero-based channel.</param>
    /// <param name="d1">The first MIDI data byte.</param>
    /// <param name="d2">The second MIDI data byte, ignored by one-data-byte messages.</param>
    public abstract void Send(byte status, byte d1, byte d2);

    /// <summary>Accepts an XMIDI SysEx fragment; FM drivers ignore it.</summary>
    /// <param name="message">The fragment payload, excluding its XMIDI status and length.</param>
    /// <param name="status">0xF0 starts a message; 0xF7 continues it.</param>
    public virtual void SendSysEx(ReadOnlySpan<byte> message, byte status = 0xf0)
    {
    }

    /// <summary>Runs driver work due after sequence service; the base implementation has none.</summary>
    public virtual void Service()
    {
    }

    /// <summary>Updates a supported device display; the base implementation ignores it.</summary>
    /// <param name="text">The display text.</param>
    public virtual void WriteDisplay(string text)
    {
    }
}
