using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Warehouse.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Action = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GateId = table.Column<int>(type: "int", nullable: true),
                    DocumentId = table.Column<int>(type: "int", nullable: true),
                    DocumentNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    GateCycleId = table.Column<long>(type: "bigint", nullable: true),
                    CycleId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    ReaderId = table.Column<int>(type: "int", nullable: true),
                    Epc = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PreviousState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NewState = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Result = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Details = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NumberSequences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    LastValue = table.Column<long>(type: "bigint", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NumberSequences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Uom = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    UnitsPerCarton = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    Category = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserName = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    PasswordHash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "bit", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                    LockedOutUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EpcTags",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Epc = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ItemCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ItemName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CartonNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    UnitQuantity = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastGateCycleId = table.Column<long>(type: "bigint", nullable: true),
                    LastMovementAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EpcTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EpcTags_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    OnHandArticles = table.Column<int>(type: "int", nullable: false),
                    OnHandQuantity = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Inventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Inventory_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "int", nullable: false),
                    RoleId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_UserRoles_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoles_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EpcTagId = table.Column<long>(type: "bigint", nullable: false),
                    GateCycleId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    PreviousStatus = table.Column<int>(type: "int", nullable: false),
                    NewStatus = table.Column<int>(type: "int", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_EpcTags_EpcTagId",
                        column: x => x.EpcTagId,
                        principalTable: "EpcTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Alarms",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AlarmId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    GateId = table.Column<int>(type: "int", nullable: true),
                    DocumentId = table.Column<int>(type: "int", nullable: true),
                    GateCycleId = table.Column<long>(type: "bigint", nullable: true),
                    ReaderId = table.Column<int>(type: "int", nullable: true),
                    AlarmType = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    Epc = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    EpcList = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RaisedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcknowledgedByUserId = table.Column<int>(type: "int", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "int", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolutionNotes = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alarms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alarms_Users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "DocumentItems",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    EpcTagId = table.Column<long>(type: "bigint", nullable: false),
                    Epc = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    IsDetected = table.Column<bool>(type: "bit", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DetectedByCycleId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentItems_EpcTags_EpcTagId",
                        column: x => x.EpcTagId,
                        principalTable: "EpcTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentNumber = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<int>(type: "int", nullable: true),
                    UserDisplayName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    GateId = table.Column<int>(type: "int", nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    ExpectedArticles = table.Column<int>(type: "int", nullable: false),
                    ExpectedQuantity = table.Column<int>(type: "int", nullable: false),
                    DetectedArticles = table.Column<int>(type: "int", nullable: false),
                    DetectedQuantity = table.Column<int>(type: "int", nullable: false),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledReason = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Documents_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Gates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Code = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Location = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CurrentState = table.Column<int>(type: "int", nullable: false),
                    ActiveDocumentId = table.Column<int>(type: "int", nullable: true),
                    StateChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Gates_Documents_ActiveDocumentId",
                        column: x => x.ActiveDocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Readers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReaderId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IpAddress = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Port = table.Column<int>(type: "int", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    GateId = table.Column<int>(type: "int", nullable: false),
                    IsOnline = table.Column<bool>(type: "bit", nullable: false),
                    IsInventorying = table.Column<bool>(type: "bit", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    HardwareVersion = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TemperatureCelsius = table.Column<double>(type: "float", nullable: true),
                    EnabledAntennas = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    GpioState = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Readers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Readers_Gates_GateId",
                        column: x => x.GateId,
                        principalTable: "Gates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GateCycles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CycleId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TriggerKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    GateId = table.Column<int>(type: "int", nullable: false),
                    ReaderId = table.Column<int>(type: "int", nullable: false),
                    DocumentId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DetectedEpcCount = table.Column<int>(type: "int", nullable: false),
                    RawReadCount = table.Column<int>(type: "int", nullable: false),
                    ExpectedEpcCount = table.Column<int>(type: "int", nullable: false),
                    UnknownEpcCount = table.Column<int>(type: "int", nullable: false),
                    UnexpectedEpcCount = table.Column<int>(type: "int", nullable: false),
                    MissingEpcCount = table.Column<int>(type: "int", nullable: false),
                    ValidationResult = table.Column<int>(type: "int", nullable: true),
                    ValidationSummary = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ReaderHealthy = table.Column<bool>(type: "bit", nullable: false),
                    InventoryCommitted = table.Column<bool>(type: "bit", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateCycles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateCycles_Documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "Documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GateCycles_Gates_GateId",
                        column: x => x.GateId,
                        principalTable: "Gates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GateCycles_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GpioEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReaderId = table.Column<int>(type: "int", nullable: false),
                    GateId = table.Column<int>(type: "int", nullable: false),
                    Pin = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsInput = table.Column<bool>(type: "bit", nullable: false),
                    High = table.Column<bool>(type: "bit", nullable: false),
                    GateCycleId = table.Column<long>(type: "bigint", nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GpioEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GpioEvents_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReaderEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReaderId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    SdkOperation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    ErrorCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReaderEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReaderEvents_Readers_ReaderId",
                        column: x => x.ReaderId,
                        principalTable: "Readers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GateCycleEpcs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GateCycleId = table.Column<long>(type: "bigint", nullable: false),
                    Epc = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    EpcTagId = table.Column<long>(type: "bigint", nullable: true),
                    Classification = table.Column<int>(type: "int", nullable: false),
                    ReadCount = table.Column<int>(type: "int", nullable: false),
                    PeakRssi = table.Column<double>(type: "float", nullable: true),
                    Antenna = table.Column<int>(type: "int", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GateCycleEpcs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GateCycleEpcs_GateCycles_GateCycleId",
                        column: x => x.GateCycleId,
                        principalTable: "GateCycles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_AlarmId",
                table: "Alarms",
                column: "AlarmId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_AlarmType",
                table: "Alarms",
                column: "AlarmType");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_DocumentId",
                table: "Alarms",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_GateCycleId",
                table: "Alarms",
                column: "GateCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_GateId",
                table: "Alarms",
                column: "GateId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_ResolvedByUserId",
                table: "Alarms",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_Status",
                table: "Alarms",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Alarms_Status_RaisedAt",
                table: "Alarms",
                columns: new[] { "Status", "RaisedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Action_OccurredAt",
                table: "AuditLogs",
                columns: new[] { "Action", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_DocumentId",
                table: "AuditLogs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Epc",
                table: "AuditLogs",
                column: "Epc");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_GateCycleId",
                table: "AuditLogs",
                column: "GateCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_OccurredAt",
                table: "AuditLogs",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentItems_DocumentId_Epc",
                table: "DocumentItems",
                columns: new[] { "DocumentId", "Epc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentItems_Epc",
                table: "DocumentItems",
                column: "Epc");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentItems_EpcTagId",
                table: "DocumentItems",
                column: "EpcTagId");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_DocumentNumber",
                table: "Documents",
                column: "DocumentNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_GateId_Status",
                table: "Documents",
                columns: new[] { "GateId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Status",
                table: "Documents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Type_CreatedAt",
                table: "Documents",
                columns: new[] { "Type", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UserId",
                table: "Documents",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_EpcTags_Epc",
                table: "EpcTags",
                column: "Epc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EpcTags_ProductId_Status",
                table: "EpcTags",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_EpcTags_Status",
                table: "EpcTags",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycleEpcs_Classification",
                table: "GateCycleEpcs",
                column: "Classification");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycleEpcs_Epc",
                table: "GateCycleEpcs",
                column: "Epc");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycleEpcs_GateCycleId",
                table: "GateCycleEpcs",
                column: "GateCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycleEpcs_GateCycleId_Epc",
                table: "GateCycleEpcs",
                columns: new[] { "GateCycleId", "Epc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_CycleId",
                table: "GateCycles",
                column: "CycleId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_DocumentId",
                table: "GateCycles",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_GateId_StartedAt",
                table: "GateCycles",
                columns: new[] { "GateId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_ReaderId",
                table: "GateCycles",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_Status",
                table: "GateCycles",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_GateCycles_TriggerKey",
                table: "GateCycles",
                column: "TriggerKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Gates_ActiveDocumentId",
                table: "Gates",
                column: "ActiveDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_Gates_Code",
                table: "Gates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GpioEvents_GateCycleId",
                table: "GpioEvents",
                column: "GateCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_GpioEvents_GateId_OccurredAt",
                table: "GpioEvents",
                columns: new[] { "GateId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GpioEvents_ReaderId",
                table: "GpioEvents",
                column: "ReaderId");

            migrationBuilder.CreateIndex(
                name: "IX_Inventory_ProductId",
                table: "Inventory",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_DocumentId",
                table: "InventoryMovements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_EpcTagId_OccurredAt",
                table: "InventoryMovements",
                columns: new[] { "EpcTagId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_GateCycleId",
                table: "InventoryMovements",
                column: "GateCycleId");

            migrationBuilder.CreateIndex(
                name: "IX_NumberSequences_Prefix_Year",
                table: "NumberSequences",
                columns: new[] { "Prefix", "Year" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Code",
                table: "Products",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReaderEvents_ReaderId_OccurredAt",
                table: "ReaderEvents",
                columns: new[] { "ReaderId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Readers_GateId",
                table: "Readers",
                column: "GateId");

            migrationBuilder.CreateIndex(
                name: "IX_Readers_ReaderId",
                table: "Readers",
                column: "ReaderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Name",
                table: "Roles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SystemSettings_Key",
                table: "SystemSettings",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserRoles_RoleId",
                table: "UserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Alarms_Documents_DocumentId",
                table: "Alarms",
                column: "DocumentId",
                principalTable: "Documents",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Alarms_GateCycles_GateCycleId",
                table: "Alarms",
                column: "GateCycleId",
                principalTable: "GateCycles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Alarms_Gates_GateId",
                table: "Alarms",
                column: "GateId",
                principalTable: "Gates",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_DocumentItems_Documents_DocumentId",
                table: "DocumentItems",
                column: "DocumentId",
                principalTable: "Documents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Documents_Gates_GateId",
                table: "Documents",
                column: "GateId",
                principalTable: "Gates",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Gates_Documents_ActiveDocumentId",
                table: "Gates");

            migrationBuilder.DropTable(
                name: "Alarms");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "DocumentItems");

            migrationBuilder.DropTable(
                name: "GateCycleEpcs");

            migrationBuilder.DropTable(
                name: "GpioEvents");

            migrationBuilder.DropTable(
                name: "Inventory");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "NumberSequences");

            migrationBuilder.DropTable(
                name: "ReaderEvents");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "UserRoles");

            migrationBuilder.DropTable(
                name: "GateCycles");

            migrationBuilder.DropTable(
                name: "EpcTags");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Readers");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "Documents");

            migrationBuilder.DropTable(
                name: "Gates");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
