// Source tables: REL105 YAMAHA32.INC.

namespace AIL2.Music;

/// <summary>Original YAMAHA32.INC frequency, routing, initialization and response tables.</summary>
internal static class FmTables
{
    /// <summary>Sixteen pitch steps per semitone; signed high values signal an octave adjustment.</summary>
    internal static readonly ushort[] FreqTable =
    [
        690, 692, 695, 697, 700, 702, 705, 707, 710, 713, 715, 718, 720, 723, 726, 728, 731, 733, 736, 739, 741, 744,
        747, 749, 752, 755, 758, 760, 763, 766, 769, 771, 774, 777, 780, 783, 785, 788, 791, 794, 797, 800, 803, 806,
        809, 811, 814, 817, 820, 823, 826, 829, 832, 835, 838, 841, 844, 847, 850, 854, 857, 860, 863, 866, 869, 872,
        875, 879, 882, 885, 888, 891, 895, 898, 901, 904, 908, 911, 914, 917, 921, 924, 927, 931, 934, 937, 941, 944,
        948, 951, 955, 958, 961, 965, 968, 972, 975, 979, 983, 986, 990, 993, 997, 1000, 1004, 1008, 1011, 1015, 1019,
        1022, 65025, 65027, 65029, 65031, 65032, 65034, 65036, 65038, 65040, 65042, 65044, 65046, 65048, 65050, 65052,
        65054, 65056, 65057, 65059, 65061, 65063, 65065, 65067, 65069, 65071, 65073, 65076, 65078, 65080, 65082, 65084,
        65086, 65088, 65090, 65092, 65094, 65096, 65098, 65100, 65103, 65105, 65107, 65109, 65111, 65113, 65116, 65118,
        65120, 65122, 65124, 65127, 65129, 65131, 65133, 65135, 65138, 65140, 65142, 65145, 65147, 65149, 65151, 65154,
        65156, 65158, 65161, 65163, 65165, 65168, 65170, 65173, 65175, 65177, 65180, 65182, 65185, 65187, 65189, 65192,
        65194, 65197, 65199
    ];

    /// <summary>Octave index for each note in the original folded pitch range.</summary>
    internal static readonly byte[] NoteOctave =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3,
        3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 5, 6, 6,
        6, 6, 6, 6, 6, 6, 6, 6, 6, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7, 7
    ];

    /// <summary>Semitone index within the octave for each folded note.</summary>
    internal static readonly byte[] NoteHalftone =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
        11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,
        10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11
    ];

    /// <summary>Initial values for register bank zero, starting at address 1.</summary>
    internal static readonly byte[] Array0Init =
    [
        32, 0, 0, 96, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 63, 63, 63, 63, 63, 63, 63, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 192, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    /// <summary>Initial OPL3 bank-one values, starting at address 0x101.</summary>
    internal static readonly byte[] Array1Init =
    [
        0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 63, 63, 63, 63, 63, 63, 63, 63,
        63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 63, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    /// <summary>Original amplitude curve indexed by the MIDI velocity's upper four bits.</summary>
    internal static readonly byte[] VelGraph =
        [82, 85, 88, 91, 94, 97, 100, 103, 106, 109, 112, 115, 118, 121, 124, 127];

    /// <summary>Original dual-OPL2 side-gain curve indexed by MIDI pan.</summary>
    internal static readonly byte[] PanGraph =
    [
        0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30, 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 52, 54, 56,
        58, 60, 62, 64, 66, 68, 70, 72, 74, 76, 78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100, 102, 104, 106, 108,
        110, 112, 114, 116, 118, 120, 122, 124, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127,
        127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127,
        127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127, 127,
        127, 127, 127, 127, 127, 127, 127
    ];

    /// <summary>First operator index for each synthesizer voice.</summary>
    internal static readonly byte[] Op0 = [0, 1, 2, 6, 7, 8, 12, 13, 14, 18, 19, 20, 24, 25, 26, 30, 31, 32];

    /// <summary>Second operator index for each synthesizer voice.</summary>
    internal static readonly byte[] Op1 = [3, 4, 5, 9, 10, 11, 15, 16, 17, 21, 22, 23, 27, 28, 29, 33, 34, 35];

    /// <summary>Chip-local register offset for each operator in the original table.</summary>
    internal static readonly byte[] OpIndex =
    [
        0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 16, 17, 18, 19, 20, 21, 0, 1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 13, 16, 17,
        18, 19, 20, 21
    ];

    /// <summary>OPL register bank for each operator in the original table.</summary>
    internal static readonly byte[] OpArray =
        [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    /// <summary>Channel-register offset for each synthesizer voice.</summary>
    internal static readonly byte[] VoiceNum = [0, 1, 2, 3, 4, 5, 6, 7, 8, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Register bank for each synthesizer voice.</summary>
    internal static readonly byte[] VoiceArray = [0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1];

    /// <summary>Whether a voice can serve as the base of a four-operator pair.</summary>
    internal static readonly byte[] Op4Base = [1, 1, 1, 0, 0, 0, 0, 0, 0, 1, 1, 1, 0, 0, 0, 0, 0, 0];

    /// <summary>Paired voice index, or 255 when no four-operator partner exists.</summary>
    internal static readonly byte[] AltVoice = [3, 4, 5, 0, 1, 2, 255, 255, 255, 12, 13, 14, 9, 10, 11, 255, 255, 255];

    /// <summary>First operator of the paired voice, or 255 without a partner.</summary>
    internal static readonly byte[] AltOp0 = [6, 7, 8, 0, 1, 2, 255, 255, 255, 24, 25, 26, 18, 19, 20, 255, 255, 255];

    /// <summary>Second operator of the paired voice, or 255 without a partner.</summary>
    internal static readonly byte[] AltOp1 =
        [9, 10, 11, 3, 4, 5, 255, 255, 255, 27, 28, 29, 21, 22, 23, 255, 255, 255];

    /// <summary>OPL3 connection-mask bit associated with each voice.</summary>
    internal static readonly byte[] ConnSel = [1, 2, 4, 1, 2, 4, 0, 0, 0, 8, 16, 32, 8, 16, 32, 0, 0, 0];

    /// <summary>Four-operator base voices in the original allocation order.</summary>
    internal static readonly byte[] Op4Voice = [0, 1, 2, 9, 10, 11];

    /// <summary>Volume-scaled carriers in the first operator pair for each four-operator algorithm.</summary>
    internal static readonly byte[] Carrier01 = [0, 1, 2, 1];

    /// <summary>Volume-scaled carriers in the second operator pair for each four-operator algorithm.</summary>
    internal static readonly byte[] Carrier23 = [2, 2, 2, 3];
}
