using System.Text.Json.Serialization;

namespace Warehouse.Rfid.U300.Bridge;

/// <summary>
/// Wire contract between this adapter and the U300 Java bridge.
/// </summary>
/// <remarks>
/// This is our own protocol, not Chainway's. The bridge speaks the vendor SDK
/// (<c>com.rscja.deviceapi.RFIDWithUHFNetworkA4</c> and friends) on one side
/// and this newline-delimited JSON on the other, which is what keeps the
/// vendor's Java-only API usable from .NET without reimplementing or guessing
/// their RAW protocol on port 9160.
///
/// Framing is one JSON object per line, UTF-8. Commands carry a correlation id
/// and are answered by exactly one <c>ack</c>; everything else is an unsolicited
/// event.
/// </remarks>
public static class BridgeCommands
{
    public const string Connect = "connect";
    public const string Disconnect = "disconnect";
    public const string Status = "status";
    public const string StartInventory = "startInventory";
    public const string StopInventory = "stopInventory";
    public const string ReadGpi = "readGpi";
    public const string SetGpo = "setGpo";
    public const string SetAntennaPower = "setAntennaPower";
    public const string Ping = "ping";
}

public static class BridgeEvents
{
    public const string Ack = "ack";
    public const string Tag = "tag";
    public const string Gpi = "gpi";
    public const string State = "state";
    public const string Error = "error";
    public const string Heartbeat = "heartbeat";
}

public sealed class BridgeRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("cmd")]
    public string Command { get; set; } = string.Empty;

    /// <summary>Outputs to drive, for <see cref="BridgeCommands.SetGpo"/>.</summary>
    [JsonPropertyName("outputs")]
    public List<BridgeGpoCommand>? Outputs { get; set; }

    /// <summary>Antenna port to dBm map, for <see cref="BridgeCommands.SetAntennaPower"/>.</summary>
    [JsonPropertyName("power")]
    public Dictionary<string, int>? Power { get; set; }
}

public sealed class BridgeGpoCommand
{
    /// <summary>Vendor pin name: GPO1..GPO4, WiegandData0, WiegandData1.</summary>
    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;

    [JsonPropertyName("high")]
    public bool High { get; set; }
}

/// <summary>Any inbound line. <see cref="Type"/> discriminates.</summary>
public sealed class BridgeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("ok")]
    public bool? Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    /// <summary>SDK method that failed, for <see cref="BridgeEvents.Error"/>.</summary>
    [JsonPropertyName("op")]
    public string? Operation { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    // Tag payload, mirroring com.rscja.deviceapi.entity.UHFTAGInfo.
    [JsonPropertyName("epc")]
    public string? Epc { get; set; }

    [JsonPropertyName("tid")]
    public string? Tid { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("pc")]
    public string? Pc { get; set; }

    /// <summary>Vendor reports RSSI as a string; kept as text and parsed by the adapter.</summary>
    [JsonPropertyName("rssi")]
    public string? Rssi { get; set; }

    [JsonPropertyName("ant")]
    public string? Antenna { get; set; }

    [JsonPropertyName("count")]
    public int? Count { get; set; }

    // GPI payload.
    [JsonPropertyName("pin")]
    public string? Pin { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    /// <summary>Reader connection state, for <see cref="BridgeEvents.State"/>.</summary>
    [JsonPropertyName("connection")]
    public string? Connection { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("inventorying")]
    public bool? Inventorying { get; set; }

    [JsonPropertyName("firmware")]
    public string? Firmware { get; set; }

    [JsonPropertyName("hardware")]
    public string? Hardware { get; set; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("antennas")]
    public List<int>? Antennas { get; set; }

    [JsonPropertyName("inputs")]
    public List<BridgeGpiState>? Inputs { get; set; }

    [JsonPropertyName("ts")]
    public long? Timestamp { get; set; }
}

public sealed class BridgeGpiState
{
    /// <summary>Vendor pin name: GPI1..GPI4.</summary>
    [JsonPropertyName("pin")]
    public string Pin { get; set; } = string.Empty;

    /// <summary>Vendor reports 0 or 1.</summary>
    [JsonPropertyName("state")]
    public int State { get; set; }
}
