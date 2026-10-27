using AIL2.Samples;

namespace AIL2;

/// <summary>Selects Miles music-driver behavior independently of the host output device.</summary>
public enum MusicProfile
{
    /// <summary>AdLib behavior using one OPL2 chip.</summary>
    AdLib = 0,

    /// <summary>Sound Blaster behavior using one OPL2 chip.</summary>
    SoundBlaster = 1,

    /// <summary>Sound Blaster Pro behavior using two independent OPL2 chips.</summary>
    SoundBlasterPro1 = 2,

    /// <summary>Sound Blaster Pro 2 behavior using OPL3.</summary>
    SoundBlasterPro2 = 3,

    /// <summary>Pro Audio Spectrum behavior using dual OPL2.</summary>
    ProAudioSpectrum = 4,

    /// <summary>Pro Audio Spectrum behavior using OPL3.</summary>
    ProAudioSpectrumOpl3 = 5,

    /// <summary>AdLib Gold behavior using OPL3 and its original stereo mapping.</summary>
    AdLibGold = 6,

    /// <summary>MT-32 MIDI/SysEx output; the host supplies transport and synthesis.</summary>
    Mt32 = 7,

    /// <summary>
    ///     IBM PC internal speaker: one square-wave voice playing MIDI channel 2 (zero-based 1); the host renders the timer
    ///     output.
    /// </summary>
    PcSpeaker = 8
}

/// <summary>Selects Miles sample-playback behavior without emulating a physical device.</summary>
public enum DigitalProfile
{
    /// <summary>Mono Sound Blaster sample behavior with fixed output gain.</summary>
    SoundBlaster = 0,

    /// <summary>Sound Blaster Pro sample behavior with its quantized stereo mixer.</summary>
    SoundBlasterPro = 1,

    /// <summary>Pro Audio Spectrum unsigned PCM behavior and rate quantization.</summary>
    ProAudioSpectrum = 2,

    /// <summary>AdLib Gold encoding restrictions, discrete rates, and mixer behavior.</summary>
    AdLibGold = 3
}

/// <summary>Reports sequence progression. Pausing preserves a resumable stopped sequence.</summary>
public enum SequenceStatus
{
    /// <summary>Not advancing; a previously started sequence can be resumed.</summary>
    Stopped = 0,

    /// <summary>Events advance when the owning session advances.</summary>
    Playing = 1,

    /// <summary>An end-of-track event has been reached.</summary>
    Done = 2
}

/// <summary>Reports sampled-playback progression.</summary>
public enum SampleStatus
{
    /// <summary>Inactive; playback must be restarted with Play.</summary>
    Stopped = 0,

    /// <summary>Suspended with the remaining block duration retained.</summary>
    Paused = 1,

    /// <summary>The current block is being consumed.</summary>
    Playing = 2,

    /// <summary>The resource or selected VOC section has ended.</summary>
    Done = 3
}

/// <summary>Identifies the encoding of sample payload bytes. Compressed encodings require a host decoder.</summary>
public enum SampleEncoding
{
    /// <summary>Unsigned 8-bit PCM, with silence at 128.</summary>
    Unsigned8 = 0,

    /// <summary>Signed 8-bit two's-complement PCM.</summary>
    Signed8 = 1,

    /// <summary>Signed 16-bit PCM stored least-significant byte first.</summary>
    Signed16LittleEndian = 2,

    /// <summary>Creative 4-bit ADPCM requiring an external decoder.</summary>
    CreativeAdpcm4 = 3,

    /// <summary>Creative 2.6-bit ADPCM requiring an external decoder.</summary>
    CreativeAdpcm26 = 4,

    /// <summary>Creative 2-bit ADPCM requiring an external decoder.</summary>
    CreativeAdpcm2 = 5
}

/// <summary>Identifies a timestamped sample operation.</summary>
public enum SampleOperation
{
    /// <summary>Discard the active sample block.</summary>
    Stop = 0,

    /// <summary>Suspend consumption while retaining the active block.</summary>
    Pause = 1,

    /// <summary>Continue the suspended block at the new logical timestamp.</summary>
    Resume = 2
}

/// <summary>Describes sample encoding, channel layout, and an exact frame rate.</summary>
/// <param name="Encoding">The payload representation; compressed formats require a host decoder.</param>
/// <param name="Channels">The interleaved channel count, normally one or two.</param>
/// <param name="RateNumerator">The positive frame-rate numerator in frames per second.</param>
/// <param name="RateDenominator">The positive frame-rate denominator, defaulting to one.</param>
/// <remarks>
///     This value stores a description without validating it. The frame rate is numerator divided by denominator,
///     independent of channel count.
/// </remarks>
public readonly record struct SampleFormat(
    SampleEncoding Encoding,
    int Channels,
    long RateNumerator,
    long RateDenominator = 1);

/// <summary>
///     Contains mixer levels calculated according to the selected profile. The host applies any final output
///     processing.
/// </summary>
/// <param name="Left">The left-channel gain numerator.</param>
/// <param name="Right">The right-channel gain numerator.</param>
/// <param name="Maximum">The positive common gain denominator. Apply each gain once.</param>
public readonly record struct MixerLevels(int Left, int Right, int Maximum);

/// <summary>Receives ordered, timestamped writes in independent OPL chip address spaces.</summary>
public interface IOplOutput
{
    /// <summary>Accepts one ordered synthesizer register write on the session thread.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="chip">The zero-based chip index: zero normally, zero or one for dual OPL2.</param>
    /// <param name="register">The chip-local address; bit 8 selects the second OPL3 register bank.</param>
    /// <param name="value">The complete byte to write.</param>
    /// <remarks>
    ///     Preserve repeated writes and their order, including writes sharing a timestamp. Initialization and shutdown
    ///     also invoke this sink synchronously.
    /// </remarks>
    void WriteRegister(TimeSpan time, int chip, ushort register, byte value);

    /// <summary>Updates the profile-derived FM mixer gains.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="levels">The source gains, each divided by Maximum and applied once.</param>
    void SetOplLevels(TimeSpan time, MixerLevels levels);
}

/// <summary>Receives complete messages; borrowed message spans must be copied before retention.</summary>
public interface IMidiOutput
{
    /// <summary>Accepts a complete MIDI channel or SysEx message on the session thread.</summary>
    /// <param name="time">The logical output timestamp, which can lead session time because of MT-32 pacing.</param>
    /// <param name="message">The borrowed message, including status and SysEx framing; copy it before retaining it.</param>
    void SendMessage(TimeSpan time, ReadOnlySpan<byte> message);

    /// <summary>Records a logical transport delay requested by the MT-32 driver.</summary>
    /// <param name="time">The logical start of the delay.</param>
    /// <param name="duration">The requested delay duration.</param>
    /// <remarks>The host schedules physical transport. This callback must not advance the session recursively.</remarks>
    void Delay(TimeSpan time, TimeSpan duration);
}

/// <summary>Receives the PC speaker driver's timer and speaker-gate writes in order.</summary>
public interface ISpeakerOutput
{
    /// <summary>Accepts a reload of PIT channel 2 in square-wave mode: control word B6h, then the 16-bit count.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="divisor">The count loaded into PIT channel 2; the tone is the PIT clock divided by it.</param>
    /// <remarks>Preserve repeated calls. The AIL 1991 driver reprograms the timer on every pitch update, even with an unchanged count.</remarks>
    void SetDivisor(TimeSpan time, ushort divisor);

    /// <summary>Connects or disconnects the speaker from PIT channel 2, as bits 0 and 1 of port 61h.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="on">True when the driver sets both bits; false when it clears both.</param>
    /// <remarks>The driver writes the gate on every pitch update and note release, so repeated states occur.</remarks>
    void SetGate(TimeSpan time, bool on);
}

/// <summary>Consumes sample blocks on the session thread. The session owns the block memory and keeps it immutable.</summary>
/// <remarks>For external decoding, report logical completion through SamplePlayback.CompleteBlock on the session thread.</remarks>
public interface ISampleOutput
{
    /// <summary>Reports whether this consumer accepts a resolved sample format.</summary>
    /// <param name="format">The encoding, channel layout and rational frame rate.</param>
    /// <returns>True if this consumer can render or externally decode the format.</returns>
    bool Supports(SampleFormat format);

    /// <summary>Begins an audio block or a period of silence.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="playback">The handle to which control and completion apply.</param>
    /// <param name="bytes">Immutable owned payload, or empty memory for silence; the memory may be retained.</param>
    /// <param name="format">The resolved encoding, channel layout and rational frame rate.</param>
    /// <param name="continuation">Whether encoded data continues the previous stream without resetting decoder state.</param>
    /// <remarks>
    ///     The session completes PCM and silence automatically. For compressed data, advance session time to the
    ///     completion timestamp and call playback.CompleteBlock on the session thread. That call can synchronously deliver the
    ///     next block.
    /// </remarks>
    void BeginBlock(TimeSpan time,
        SamplePlayback playback,
        ReadOnlyMemory<byte> bytes,
        SampleFormat format,
        bool continuation);

    /// <summary>Applies a state change to a sample's active block.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="playback">The affected playback handle.</param>
    /// <param name="operation">The stop, pause or resume request.</param>
    void Control(TimeSpan time, SamplePlayback playback, SampleOperation operation);

    /// <summary>Updates the profile-derived sample mixer gains.</summary>
    /// <param name="time">The logical session timestamp.</param>
    /// <param name="levels">The source gains, each divided by Maximum and applied once.</param>
    void SetSampleLevels(TimeSpan time, MixerLevels levels);
}

/// <summary>Queries profile topology and the corresponding global timbre-bank format.</summary>
public static class MusicProfiles
{
    /// <param name="profile">The Miles music profile.</param>
    extension(MusicProfile profile)
    {
        /// <summary>Checks whether a profile uses OPL3.</summary>
        /// <returns>True for the OPL3 Sound Blaster Pro, Pro Audio Spectrum and AdLib Gold profiles.</returns>
        public bool IsOpl3()
        {
            return profile is MusicProfile.SoundBlasterPro2 or MusicProfile.ProAudioSpectrumOpl3 or MusicProfile.AdLibGold;
        }

        /// <summary>Checks whether a profile uses two independent OPL2 chips.</summary>
        /// <returns>True for SoundBlasterPro1 and ProAudioSpectrum.</returns>
        public bool IsDualOpl2()
        {
            return profile is MusicProfile.SoundBlasterPro1 or MusicProfile.ProAudioSpectrum;
        }

        /// <summary>Returns the conventional bank suffix associated with a profile.</summary>
        /// <returns>
        ///     The suffix .ad, .opl or .mt, including the dot, or an empty string for the PC speaker, whose driver uses
        ///     no timbres and names no global timbre library.
        /// </returns>
        /// <remarks>The application chooses the bank. This convention does not identify, select or convert instrument content.</remarks>
        public string BankExtension()
        {
            return profile switch
            {
                MusicProfile.Mt32 => ".mt",
                MusicProfile.PcSpeaker => "",
                _ => profile.IsOpl3() ? ".opl" : ".ad"
            };
        }
    }
}
