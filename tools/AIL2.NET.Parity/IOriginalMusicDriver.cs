using AIL2.Formats;

namespace AIL2.Parity;

/// <summary>An original music driver that a comparison plays one sequence through, service by service.</summary>
internal interface IOriginalMusicDriver : IDisposable
{
    /// <summary>Gets or sets the logical timestamp assigned to output of the following calls.</summary>
    TimeSpan Time { get; set; }

    /// <summary>Gets the time between the service calls the original host timer makes for this driver.</summary>
    TimeSpan ServicePeriod { get; }

    /// <summary>Gets the status of the most recently registered sequence.</summary>
    SequenceStatus Status { get; }

    /// <summary>Gets the zero-based EVNT offset of the most recently registered sequence, excluding the chunk header.</summary>
    int EventOffset { get; }

    /// <summary>Installs the bank and registers the selected sequence.</summary>
    /// <param name="xmidi">The complete XMIDI resource.</param>
    /// <param name="bank">The application-selected instrument bank.</param>
    /// <param name="index">The zero-based resource sequence index.</param>
    void Load(byte[] xmidi, TimbreBank bank, int index);

    /// <summary>Starts the most recently registered sequence.</summary>
    void Start();

    /// <summary>Stops the most recently registered sequence.</summary>
    void Stop();

    /// <summary>Executes one music-driver service call at the current Time.</summary>
    void Service();

    /// <summary>Executes driver shutdown, keeping its output in the trace.</summary>
    void Shutdown();
}
