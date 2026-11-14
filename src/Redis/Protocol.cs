namespace SE2GameAPI.Redis;
public sealed record DeviceCommand(string RequestId, string Epoch, string GridId, string DeviceId,
    string Cmd, long ExpiresAtMs, bool? Enabled = null, long? ExpectedRevision = null,
    string? TargetId = null, int? ItemIndex = null, double? Amount = null,
    NavigationRequest? Navigation = null, string? Mode = null);
public sealed record NavigationRequest(double[] Target, double[]? Forward = null, double[]? Up = null,
    string Mode = "flight", double MaxSpeed = 5, double PositionTolerance = 1,
    double HeadingToleranceDeg = 3, double TimeoutSeconds = 120, bool AlignCourse = false,
    bool KeepControlOnPlayer = true, bool EjectPilot = false);
public sealed record DeviceState(string Id, string GridId, string GridName, string Name, string Type,
    bool Enabled, bool Working, bool Functional, long Revision, long MeasuredAtMs = 0, string? Status = null, string[]? Capabilities = null, object? Properties = null,
    InventoryTelemetry[]? Inventories = null);
public sealed record InventoryItemTelemetry(int Index, string Definition, string Amount);
public sealed record InventoryTelemetry(int Slot, string MassCurrent, string MassMaximum, InventoryItemTelemetry[] Items);
public sealed record CommandResult(string RequestId, string Status, string? Error, DeviceState? ActualState);
public sealed record ProjectionRequest(string RequestId, string Epoch, string GridId,
    string Action = "create", string? TargetGridId = null, double[]? Offset = null,
    int Count = 0, long ExpiresAtMs = 0, string? Mode = null);
public sealed record ProjectionStatus(string RequestId, string Epoch, string GridId,
    string State, string? Error, string? ProjectionEntityId, int Projections, int Materialized,
    long UpdatedAtMs, double[]? WorldPosition = null);
public sealed record BlockInventory(string GridId, string GridName, string BlockId,
    string Definition, string[] Components, string? Adapter);
public sealed record BridgeSnapshot(string Epoch, string State, long SampledAtMs, DeviceState[] Devices,
    double LastTickMs, double MaxTickMs, double AverageTickMs, long TickCount, long CommandsApplied,
    int QueueDepth, double DiscoveryMs, string? Error = null, long[]? TickHistogram = null,
    [property: System.Text.Json.Serialization.JsonIgnore] BlockInventory[]? Blocks = null, long InventoryAtMs = 0)
{
    public DeviceState[] Lamps => Devices.Where(d => d.Type == "lamp").ToArray();
    public int ProtocolVersion => 2;
}
internal sealed class Work(DeviceCommand command)
{
    public DeviceCommand Command { get; } = command;
    public TaskCompletionSource<CommandResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
