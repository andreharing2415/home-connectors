using System.Collections.Frozen;
using System.Collections.Immutable;

namespace HomeConnectors;

/// <summary>
/// The Tholz heating modes, as the socket doc numbers them, and which of them each
/// heating <c>type</c> actually accepts. A Smart Connect module answers a
/// <c>setDevice</c> with an <c>opMode</c> its type does not support by ignoring it,
/// so the mapping from "on / off" to a mode is made here, against the type read
/// back from the device, and never guessed.
/// </summary>
public static class TholzHeating
{
    /// <summary>Desligado. Every type accepts it, which is what makes "off" unambiguous.</summary>
    public const int Off = 0;

    public const int On = 1;        // Ligado — output forced on
    public const int Automatic = 2; // Automático — the controller holds the setpoint
    public const int Economic = 3;  // Econômico
    public const int Heat = 4;      // Aquecer
    public const int Cool = 5;      // Resfriar

    // The order "on" is chosen in: prefer the thermostat (Automático) so a backup
    // is not left forced on, then plain Ligado, then Aquecer for the heat-pump
    // types that offer neither. The owner's choice, 2026-09-06: automatic where it
    // exists, forced-on where it does not.
    private static readonly ImmutableArray<int> OnPreference = [Automatic, On, Heat];

    // type -> the opMode indices that type accepts, from the socket doc's type table.
    private static readonly FrozenDictionary<int, ImmutableArray<int>> ModesByType = new Dictionary<int, ImmutableArray<int>>
    {
        [0] = [Off, On, Automatic],            // Solar Piscina
        [1] = [Off, On, Automatic],            // Trocador de Calor Piscina
        [2] = [Off, On, Automatic],            // Elétrico Piscina
        [3] = [Off, On, Automatic, Economic],  // Solar Residencial
        [4] = [Off, On],                       // Apoio a Gás
        [5] = [Off, On],                       // Apoio Elétrico
        [6] = [Off, On],                       // Recirculação de Barrilete
        [7] = [Off, Heat, Cool, Automatic],    // Trocador Tholz
        [8] = [Off, Heat],                     // Trocador LuxPool
        [9] = [Off, Heat, Cool, Automatic],    // Trocador Tholz (Turbo)
        [10] = [Off, On, Automatic],           // Termostato
        [11] = [Off, Automatic],               // Termostato Sem Agenda
        [12] = [Off, Automatic],               // Refrigeração
        [13] = [Off, Automatic],               // Refrigeração Sem Agenda
    }.ToFrozenDictionary();

    /// <summary>Whether a heating of this type accepts this opMode.</summary>
    public static bool Supports(int type, int opMode) =>
        ModesByType.TryGetValue(type, out var modes) && modes.Contains(opMode);

    /// <summary>
    /// The opMode a power request becomes for a heating of this type. Off is always
    /// <see cref="Off"/>; on is the most thermostatic mode the type offers, falling
    /// back to forced-on and then to heat. A type the doc does not list is left to
    /// forced-on, the one mode every listed heating but the two heat-pump-only ones
    /// share.
    /// </summary>
    public static int ModeFor(HomePower power, int type)
    {
        if (power == HomePower.Off)
        {
            return Off;
        }

        if (!ModesByType.TryGetValue(type, out var modes))
        {
            return On;
        }

        foreach (var candidate in OnPreference)
        {
            if (modes.Contains(candidate))
            {
                return candidate;
            }
        }

        // Every listed type has at least one non-off mode; this is only reached if
        // the table ever holds a type whose sole mode is Off, which it does not.
        return modes.FirstOrDefault(m => m != Off, On);
    }

    /// <summary>Whether an opMode read back from a heating means it is enabled — anything but Desligado.</summary>
    public static bool IsOn(int opMode) => opMode != Off;

    /// <summary>The reading a sensor with value <c>0xAAAA</c> carries: the doc's flag for a broken sensor.</summary>
    public const int SensorFault = -21846;

    // type -> which of t1..t6 the heating uses, and what each measures, from the
    // socket doc's "Sensores tX" column. Only the sensors a type actually reads
    // are listed, so a null slot the module sends is never labelled a reading.
    private static readonly FrozenDictionary<int, ImmutableArray<(string Key, string Label)>> SensorsByType =
        new Dictionary<int, ImmutableArray<(string, string)>>
        {
            [0] = [("t1", "coletor"), ("t2", "água")],
            [1] = [("t2", "água")],
            [2] = [("t2", "água")],
            [3] = [("t1", "coletor"), ("t2", "reservatório"), ("t3", "consumo")],
            [4] = [("t2", "reservatório"), ("t3", "consumo")],
            [5] = [("t2", "reservatório"), ("t3", "consumo")],
            [6] = [("t4", "recirculação")],
            [7] = [("t1", "ambiente"), ("t2", "saída"), ("t3", "entrada")],
            [8] = [("t1", "ambiente"), ("t2", "saída"), ("t3", "entrada")],
            [9] = [("t1", "ambiente"), ("t2", "saída"), ("t3", "entrada")],
            [10] = [("t1", "temperatura"), ("t4", "água")],
            [11] = [("t1", "temperatura")],
            [12] = [("t1", "temperatura")],
            [13] = [("t1", "temperatura")],
        }.ToFrozenDictionary();

    /// <summary>The sensor keys a heating of this type reads, each with the label the owner hears.</summary>
    public static ImmutableArray<(string Key, string Label)> SensorLabels(int type) =>
        SensorsByType.TryGetValue(type, out var sensors) ? sensors : [];

    /// <summary>
    /// A setpoint in whole °C, as the socket carries it: tenths of a degree,
    /// clamped to the range the device itself reported. A voice command never
    /// reaches here out of range — the executor holds it to the device — but the
    /// module's own <c>minSp</c>/<c>maxSp</c> are the final word.
    /// </summary>
    public static int ToDeciCelsius(int celsius, int minDeci, int maxDeci)
    {
        var deci = celsius * 10;

        return deci < minDeci ? minDeci : deci > maxDeci ? maxDeci : deci;
    }
}
