using System.ComponentModel.DataAnnotations;

namespace Warehouse.Rfid.Abstractions;

/// <summary>Which concrete driver backs a configured reader.</summary>
public enum RfidDriverKind
{
    /// <summary>Chainway U300 via the vendor Java bridge (production).</summary>
    U300 = 0,

    /// <summary>Deterministic in-process fake. Refuses to load outside Development.</summary>
    Simulation = 1
}

/// <summary>
/// Physical wiring of the gate to the U300 GPIO terminal.
/// </summary>
/// <remarks>
/// Defaults follow the brief's gate design: the 12V presence signal lands on
/// input 1 and the alarm beacon hangs off output 1. Both are configurable
/// because the terminal has four of each and installations differ.
///
/// Pin numbers on the U300's 24-way terminal (integration guide table 4.3.1.3
/// -- NOT the URA4 table): Input1=5, Input3=6, Output1=7, Output3=8,
/// IO_GND=9, VDD5V=12, Input2=17, Input4=18, Output2=19, Output4=20.
/// </remarks>
public sealed class GpioMapOptions
{
    /// <summary>Input carrying the 12V gate-presence signal.</summary>
    public GpiPin GateSignalInput { get; set; } = GpiPin.Gpi1;

    /// <summary>
    /// Level that means "gate active". Optocoupler inputs read high when the
    /// 12V signal is applied, so true is the normal wiring; set false for a
    /// normally-closed sensor.
    /// </summary>
    public bool GateSignalActiveHigh { get; set; } = true;

    /// <summary>Output driving the alarm beacon/sounder. Null disables it.</summary>
    public GpoPin? AlarmOutput { get; set; } = GpoPin.Gpo1;

    /// <summary>Output driving the "pass" indicator. Null disables it.</summary>
    public GpoPin? PassOutput { get; set; } = GpoPin.Gpo2;

    /// <summary>How long the alarm output stays asserted, milliseconds.</summary>
    [Range(0, 600_000)]
    public int AlarmPulseMs { get; set; } = 3_000;

    /// <summary>How long the pass indicator stays asserted, milliseconds.</summary>
    [Range(0, 600_000)]
    public int PassPulseMs { get; set; } = 1_500;

    /// <summary>
    /// Ignore input edges arriving within this window of the previous one.
    /// Guards against contact bounce producing phantom gate cycles.
    /// </summary>
    [Range(0, 10_000)]
    public int DebounceMs { get; set; } = 150;
}

/// <summary>Per-reader configuration. Everything here comes from config, never source.</summary>
public sealed class RfidReaderOptions
{
    /// <summary>Stable id; must match the Readers row.</summary>
    [Required]
    public string ReaderId { get; set; } = string.Empty;

    [Required]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gate this reader is mounted on.</summary>
    [Required]
    public string GateId { get; set; } = string.Empty;

    public RfidDriverKind Driver { get; set; } = RfidDriverKind.U300;

    /// <summary>Host of the Java bridge process that owns this reader.</summary>
    public string BridgeHost { get; set; } = "127.0.0.1";

    /// <summary>Port the Java bridge listens on for adapter connections.</summary>
    [Range(1, 65535)]
    public int BridgePort { get; set; } = 9310;

    /// <summary>Reader address the bridge should dial. Default per vendor manual.</summary>
    public string ReaderHost { get; set; } = "192.168.1.100";

    /// <summary>Vendor RAW-protocol service port. Default 9160 per the manual.</summary>
    [Range(1, 65535)]
    public int ReaderPort { get; set; } = 9160;

    /// <summary>Set to use RS-232 instead of Ethernet, e.g. "COM3".</summary>
    public string? SerialPort { get; set; }

    /// <summary>Antenna ports to enable, 1-based. Empty means leave as configured on the reader.</summary>
    public int[] Antennas { get; set; } = [1];

    /// <summary>Transmit power in dBm per antenna port. U300 supports 1-30 dBm.</summary>
    public Dictionary<int, int> AntennaPowerDbm { get; set; } = new();

    public InventoryTriggerMode TriggerMode { get; set; } = InventoryTriggerMode.Command;

    public GpioMapOptions Gpio { get; set; } = new();

    /// <summary>Reconnect backoff floor, seconds.</summary>
    [Range(1, 3600)]
    public int ReconnectMinSeconds { get; set; } = 5;

    /// <summary>Reconnect backoff ceiling, seconds. Prevents an aggressive retry storm.</summary>
    [Range(1, 86400)]
    public int ReconnectMaxSeconds { get; set; } = 120;

    /// <summary>Fail an SDK call that has not answered within this many milliseconds.</summary>
    [Range(100, 120_000)]
    public int CommandTimeoutMs { get; set; } = 10_000;

    /// <summary>Declare the reader offline if no bridge heartbeat arrives for this long.</summary>
    [Range(1, 600)]
    public int HeartbeatTimeoutSeconds { get; set; } = 30;

    public bool Enabled { get; set; } = true;
}

/// <summary>Root RFID configuration section.</summary>
public sealed class RfidOptions
{
    public const string SectionName = "Rfid";

    /// <summary>
    /// Master switch for simulated readers. Guarded again at startup: the host
    /// refuses to boot if this is true outside a Development environment.
    /// </summary>
    public bool AllowSimulation { get; set; }

    public List<RfidReaderOptions> Readers { get; set; } = [];
}
