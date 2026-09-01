namespace Warehouse.Domain;

/// <summary>Direction of stock movement a document represents.</summary>
public enum DocumentType
{
    Inward = 1,
    Outward = 2
}

public enum DocumentStatus
{
    /// <summary>Created but not yet released to a gate.</summary>
    Draft = 0,

    /// <summary>Released and assigned to a gate; waiting for the gate signal.</summary>
    Released = 1,

    /// <summary>At least one gate cycle has run but the document is not satisfied.</summary>
    InProgress = 2,

    /// <summary>Every expected EPC was detected and inventory was committed.</summary>
    Completed = 3,

    Cancelled = 4,

    /// <summary>Validation failed terminally and a supervisor must intervene.</summary>
    Failed = 5
}

public enum GateCycleStatus
{
    /// <summary>Gate signal went active; inventory is running.</summary>
    Running = 0,

    /// <summary>Signal cleared; EPC set frozen, validation not yet finished.</summary>
    Validating = 1,

    /// <summary>Validation passed and the inventory transaction committed.</summary>
    Passed = 2,

    /// <summary>Validation failed. See the linked alarms for why.</summary>
    Failed = 3,

    /// <summary>Ended without a verdict (reader lost, host shutdown, timeout).</summary>
    Aborted = 4
}

/// <summary>
/// Explicit gate lifecycle (§35). Kept as one enum rather than scattered
/// booleans so every transition is auditable and illegal moves are rejected
/// centrally by <see cref="Gates.GateStateMachine"/>.
/// </summary>
public enum GateState
{
    /// <summary>No document assigned.</summary>
    Idle = 0,

    /// <summary>Document assigned, reader healthy, ready to arm.</summary>
    Ready = 1,

    /// <summary>Armed and waiting for the gate input to go active.</summary>
    WaitingForGate = 2,

    /// <summary>Input active; collecting EPCs.</summary>
    Reading = 3,

    /// <summary>Input cleared; draining in-flight reads before validation.</summary>
    Processing = 4,

    /// <summary>Running the validation engine and, on success, the inventory transaction.</summary>
    Validating = 5,

    /// <summary>Cycle passed. Terminal for the cycle, not the gate.</summary>
    Passed = 6,

    /// <summary>Cycle failed validation; an alarm is active.</summary>
    Alarm = 7,

    /// <summary>Non-RFID fault (database, GPIO write, internal error).</summary>
    Error = 8,

    /// <summary>Reader is offline. No cycle may start or complete.</summary>
    ReaderDisconnected = 9
}

/// <summary>Outcome of the validation engine for one gate cycle.</summary>
public enum ValidationOutcome
{
    Pass = 0,
    Fail = 1
}

/// <summary>Why a validation failed. Ordered by severity, highest first.</summary>
public enum AlarmType
{
    /// <summary>An EPC was read that exists in no warehouse record.</summary>
    UnknownEpc = 1,

    /// <summary>A known EPC was read that the active document does not expect.</summary>
    UnexpectedEpc = 2,

    /// <summary>The document expected EPCs that were never detected.</summary>
    MissingEpc = 3,

    /// <summary>The cycle ended having read nothing: an untagged item may have passed.</summary>
    NoEpc = 4,

    /// <summary>Detected set contradicts the document's type or state.</summary>
    DocumentMismatch = 5,

    /// <summary>Reader reported an SDK/transport failure.</summary>
    ReaderError = 6,

    /// <summary>A GPIO read or write failed.</summary>
    GpioError = 7,

    /// <summary>Reader dropped while a cycle was live.</summary>
    ReaderDisconnected = 8,

    /// <summary>Cycle exceeded its configured maximum duration.</summary>
    Timeout = 9,

    /// <summary>The same gate event arrived twice; the replay was suppressed.</summary>
    DuplicateGateEvent = 10
}

public enum AlarmStatus
{
    Active = 0,
    Acknowledged = 1,
    Resolved = 2
}

/// <summary>Lifecycle of a physical tag as tracked by the warehouse.</summary>
public enum EpcStatus
{
    /// <summary>Registered but never received.</summary>
    Registered = 0,

    /// <summary>Inside the warehouse.</summary>
    InStock = 1,

    /// <summary>Shipped out through a gate.</summary>
    Shipped = 2,

    /// <summary>Withdrawn from use; must not pass a gate.</summary>
    Retired = 3,

    /// <summary>Flagged by an operator; treated as unexpected wherever it appears.</summary>
    Blocked = 4
}

public enum ReaderEventType
{
    Connected = 0,
    Disconnected = 1,
    ReconnectAttempt = 2,
    Error = 3,
    InventoryStarted = 4,
    InventoryStopped = 5,
    HeartbeatLost = 6,
    ConfigurationApplied = 7
}

/// <summary>Auditable operations (§32).</summary>
public enum AuditAction
{
    DocumentCreated = 0,
    DocumentReleased = 1,
    DocumentCompleted = 2,
    DocumentCancelled = 3,
    DocumentRetried = 4,
    GateCycleStarted = 10,
    GateCycleCompleted = 11,
    GateCycleAborted = 12,
    EpcDetected = 20,
    UnknownEpc = 21,
    UnexpectedEpc = 22,
    MissingEpc = 23,
    AlarmTriggered = 30,
    AlarmAcknowledged = 31,
    AlarmResolved = 32,
    ReaderConnected = 40,
    ReaderDisconnected = 41,
    GpioOn = 50,
    GpioOff = 51,
    GpioOutputSet = 52,
    InventoryCommitted = 60,
    InventoryRolledBack = 61,
    EpcImported = 70,
    UserLoggedIn = 80,
    UserLoginFailed = 81,
    SettingChanged = 90
}
