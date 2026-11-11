namespace RemoteVault.Core.Services;

public sealed class DatabaseBackupProfilePayload
{
    public string ProfileId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DbType { get; set; } = "mssql";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; }

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string Instance { get; set; } = string.Empty;

    public string MysqldumpPath { get; set; } = string.Empty;

    public string PgDumpPath { get; set; } = string.Empty;

    public string SqlcmdPath { get; set; } = string.Empty;

    public bool IncludeAllDatabases { get; set; }

    public List<string> Databases { get; set; } = new();

    public int KeepFullChains { get; set; } = 4;

    public bool DiffEnabled { get; set; }

    public int FullIntervalDays { get; set; } = 1;

    public int DiffIntervalMinutes { get; set; } = 60;

    public string FullScheduleKind { get; set; } = "daily";

    public string FullScheduleTime { get; set; } = "02:00";

    public string DiffScheduleKind { get; set; } = "interval";
}

public sealed class DatabaseBackupLoadResult
{
    public bool IsSuccess { get; set; }

    public int ExitCode { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool IsLocked { get; set; }

    public List<DatabaseBackupProfilePayload> Profiles { get; set; } = new();
}

public sealed class DatabaseBackupSaveResult
{
    public bool IsSuccess { get; set; }

    public int ExitCode { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool IsLocked { get; set; }
}

public sealed class DatabaseBackupDiscoverResult
{
    public bool IsSuccess { get; set; }

    public int ExitCode { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<string> Databases { get; set; } = new();
}

public sealed class DatabaseBackupRunRequest
{
    public string ConfigPath { get; set; } = string.Empty;

    public string BackupType { get; set; } = "full";

    public List<string> SelectedProfileIds { get; set; } = new();

    public List<DatabaseBackupProfilePayload> Profiles { get; set; } = new();
}

public sealed class DatabaseBackupRunResult
{
    public bool IsSuccess { get; set; }

    public int ExitCode { get; set; }

    public string Message { get; set; } = string.Empty;

    public List<DatabaseBackupRunProfileResult> Results { get; set; } = new();
}

public sealed class DatabaseBackupRunProfileResult
{
    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string Message { get; set; } = string.Empty;
}

public sealed class DatabaseBackupWorkerEvent
{
    public string EventName { get; set; } = string.Empty;

    public string? Message { get; set; }

    public DatabaseBackupWorkerPayload Payload { get; set; } = new();
}

public sealed class DatabaseBackupWorkerPayload
{
    public string? ProfileId { get; set; }

    public string? ProfileName { get; set; }

    public string? BackupType { get; set; }

    public string? Line { get; set; }
}

internal sealed class DatabaseBackupWorkerEnvelope
{
    public string? EventName { get; set; }

    public string? Message { get; set; }

    public DatabaseBackupWorkerPayload? Payload { get; set; }

    public bool? Ok { get; set; }

    public int? ExitCode { get; set; }

    public bool? IsLocked { get; set; }

    public List<DatabaseBackupProfilePayload>? Profiles { get; set; }

    public List<DatabaseBackupRunProfileResult>? Results { get; set; }

    public List<string>? Databases { get; set; }
}
