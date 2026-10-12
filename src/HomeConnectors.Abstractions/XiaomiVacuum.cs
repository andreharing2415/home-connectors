using System.Collections.Frozen;
using System.Collections.Immutable;

namespace HomeConnectors;

/// <summary>
/// The Mijia robot vacuum as its MIoT spec describes it
/// (<c>urn:miot-spec-v2:device:vacuum:0000A006:xiaomi-d110ch:2</c>, the Mijia M40),
/// kept apart from the socket so the numbers can be tested without one.
/// <para>
/// Every property and action is addressed by service id (siid) and property or
/// action id (piid/aiid). The ids below were read off the published spec and
/// checked against the owner's M40 on 2026-09-27; another Dreame-built Mijia model
/// shares the "Robot Cleaner" and "Battery" services but may move the rest.
/// </para>
/// </summary>
public static class XiaomiVacuum
{
    /// <summary>A property or action address: service id, then property or action id.</summary>
    public readonly record struct Address(int Siid, int Iid);

    public static readonly Address Status = new(2, 1);
    public static readonly Address Fault = new(2, 2);
    public static readonly Address Battery = new(3, 1);
    public static readonly Address MainBrush = new(9, 2);
    public static readonly Address SideBrush = new(10, 2);
    public static readonly Address Filter = new(11, 1);
    public static readonly Address Mop = new(18, 1);

    /// <summary>Start a whole-house clean (Robot Cleaner · Start Sweep).</summary>
    public static readonly Address StartClean = new(2, 1);

    /// <summary>Go back to the base (Battery · Start Charge). What Off means for a vacuum.</summary>
    public static readonly Address GoHome = new(3, 1);

    /// <summary>
    /// Vacuum-extend · clean-setting, the mode the app's "aspirar / pano / os
    /// dois" switch writes. The low byte is the mode (<see cref="VacuumCleanMode"/>);
    /// the bytes above hold the mop's wetness and are kept as they are. The owner's
    /// M40 read 5122 (0x1402) on 2026-09-27: both, wetness 20. Every clean —
    /// whole house or by room — runs in whatever mode this last held, so each
    /// one sets it first.
    /// </summary>
    public static readonly Address CleanSetting = new(4, 23);

    /// <summary>The clean-setting value with its mode replaced and the rest kept.</summary>
    public static int WithMode(int setting, VacuumCleanMode mode) => (setting & ~0xFF) | (int)mode;

    /// <summary>What one read asks for, in order.</summary>
    public static ImmutableArray<Address> ReadSet { get; } = [Status, Fault, Battery, MainBrush, SideBrush, Filter, Mop];

    // Robot Cleaner · Status, straight from the spec's value list.
    private static readonly FrozenDictionary<int, string> StatusWords = new Dictionary<int, string>
    {
        [1] = "aspirando",
        [2] = "parado",
        [3] = "pausado",
        [4] = "com erro",
        [5] = "voltando para a base",
        [6] = "carregando na base",
        [7] = "passando pano",
        [8] = "secando o pano",
        [9] = "lavando o pano",
        [10] = "indo lavar o pano",
        [11] = "mapeando a casa",
        [12] = "aspirando e passando pano",
    }.ToFrozenDictionary();

    // The states in which it is out doing the job it was sent to do. Washing the
    // mop mid-clean is part of the clean; drying it afterwards, at the base, is not.
    private static readonly FrozenSet<int> Working = new[] { 1, 7, 9, 10, 11, 12 }.ToFrozenSet();

    /// <summary>The status code in the owner's words, or a plain "estado N" for a code the spec did not list.</summary>
    public static string Describe(int status) =>
        StatusWords.TryGetValue(status, out var word) ? word : $"estado {status}";

    /// <summary>
    /// On while it is cleaning, Off otherwise. This is what the verifier compares
    /// against: after "limpa a casa" it expects On, after "volta para a base" Off —
    /// and "voltando para a base" already counts as Off, since that is the order
    /// being carried out.
    /// </summary>
    public static HomePower PowerOf(int status) => Working.Contains(status) ? HomePower.On : HomePower.Off;

    /// <summary>The percentages worth showing, labelled the way the owner would ask for them.</summary>
    public static ImmutableArray<HomeGauge> Gauges(IReadOnlyDictionary<Address, int> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var gauges = ImmutableArray.CreateBuilder<HomeGauge>(5);

        Add(Battery, "bateria");
        Add(MainBrush, "escova principal");
        Add(SideBrush, "escova lateral");
        Add(Filter, "filtro");
        Add(Mop, "pano");

        return gauges.ToImmutable();

        void Add(Address address, string label)
        {
            if (values.TryGetValue(address, out var percent) && percent is >= 0 and <= 100)
            {
                gauges.Add(new HomeGauge(label, percent));
            }
        }
    }
}
