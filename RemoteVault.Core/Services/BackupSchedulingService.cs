using System.Diagnostics;
using System.Security;
using System.Xml.Linq;

namespace RemoteVault.Core.Services;

public interface IBackupSchedulingService
{
    Task<BackupScheduleSyncResult> SyncAsync(StoredAppConfig config, string configPath, CancellationToken cancellationToken = default);

    Task RepairTaskActionsAsync(StoredAppConfig config, string configPath, CancellationToken cancellationToken = default);
}

public sealed class BackupScheduleSyncResult
{
    public bool IsSuccess { get; init; }

    public string Message { get; init; } = string.Empty;
}

public sealed class WindowsTaskSchedulerService : IBackupSchedulingService
{
    private const string FileTaskName = @"RemoteVault File Backup";
    private const string LegacyDatabaseTaskName = @"RemoteVault DB Backup";
    private const string DatabaseFullTaskName = @"RemoteVault DB Full Backup";
    private const string DatabaseDiffTaskName = @"RemoteVault DB Differential Backup";

    public async Task<BackupScheduleSyncResult> SyncAsync(StoredAppConfig config, string configPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var messages = new List<string>();

        try
        {
            if (ShouldScheduleFileBackup(config))
            {
                await CreateOrUpdateTaskAsync(FileTaskName, "--run-scheduled-backup", config.FileBackupSchedule, cancellationToken);
                messages.Add("File backup task updated.");
            }
            else
            {
                await DeleteTaskIfExistsAsync(FileTaskName, cancellationToken);
                messages.Add("File backup task removed.");
            }

            await DeleteTaskIfExistsAsync(LegacyDatabaseTaskName, cancellationToken);

            if (ShouldScheduleDatabaseBackup(config.DatabaseFullBackupSchedule, config))
            {
                await CreateOrUpdateTaskAsync(DatabaseFullTaskName, "--run-scheduled-db-full-backup", config.DatabaseFullBackupSchedule, cancellationToken);
                messages.Add("DB full backup task updated.");
            }
            else
            {
                await DeleteTaskIfExistsAsync(DatabaseFullTaskName, cancellationToken);
                messages.Add("DB full backup task removed.");
            }

            if (ShouldScheduleDatabaseBackup(config.DatabaseDiffBackupSchedule, config))
            {
                await CreateOrUpdateTaskAsync(DatabaseDiffTaskName, "--run-scheduled-db-diff-backup", config.DatabaseDiffBackupSchedule, cancellationToken);
                messages.Add("DB differential backup task updated.");
            }
            else
            {
                await DeleteTaskIfExistsAsync(DatabaseDiffTaskName, cancellationToken);
                messages.Add("DB differential backup task removed.");
            }

            return new BackupScheduleSyncResult
            {
                IsSuccess = true,
                Message = string.Join(" ", messages)
            };
        }
        catch (Exception ex)
        {
            return new BackupScheduleSyncResult
            {
                IsSuccess = false,
                Message = $"Scheduler update failed: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Re-registers the scheduled tasks when any existing one still points at a
    /// previous executable name/location (e.g. RemoteVault.Desktop.WPF.exe from
    /// an install upgraded in place). No-op when everything already matches.
    /// </summary>
    public async Task RepairTaskActionsAsync(StoredAppConfig config, string configPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var currentPath = ResolveApplicationPath();
        if (currentPath is null)
        {
            return;
        }

        foreach (var taskName in new[] { FileTaskName, DatabaseFullTaskName, DatabaseDiffTaskName })
        {
            var query = await RunSchtasksAsync(new[]
            {
                "/Query",
                "/TN",
                taskName,
                "/XML"
            }, cancellationToken, throwOnFailure: false);

            if (query.ExitCode != 0)
            {
                continue;
            }

            var registeredCommand = TryReadTaskCommand(query.StandardOutput);
            if (registeredCommand is null ||
                string.Equals(registeredCommand, currentPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await SyncAsync(config, configPath, cancellationToken);
            return;
        }
    }

    private static string? TryReadTaskCommand(string taskXml)
    {
        try
        {
            var document = XDocument.Parse(taskXml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return document.Descendants(ns + "Command").FirstOrDefault()?.Value.Trim().Trim('"');
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveApplicationPath()
    {
        var appPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
        {
            var entryAssemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "RemoteVault";
            appPath = Path.Combine(AppContext.BaseDirectory, entryAssemblyName + ".exe");
        }

        return string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath) ? null : appPath;
    }

    private static bool ShouldScheduleFileBackup(StoredAppConfig config)
    {
        return config.FileBackupSchedule.Enabled &&
               config.BackupFolders.Count > 0 &&
               !string.IsNullOrWhiteSpace(config.BucketName) &&
               !string.IsNullOrWhiteSpace(config.Endpoint) &&
               !string.IsNullOrWhiteSpace(config.AccessKey) &&
               !string.IsNullOrWhiteSpace(config.SecretKey);
    }

    private static bool ShouldScheduleDatabaseBackup(StoredBackupSchedule schedule, StoredAppConfig config)
    {
        return config.EnableDatabaseBackups &&
               schedule.Enabled &&
               !string.IsNullOrWhiteSpace(config.DatabaseBackupRootPath);
    }

    private static async Task CreateOrUpdateTaskAsync(
        string taskName,
        string appArgument,
        StoredBackupSchedule schedule,
        CancellationToken cancellationToken)
    {
        var appPath = ResolveApplicationPath()
            ?? throw new FileNotFoundException("Could not resolve the application executable for scheduling.");

        var taskXml = BuildTaskXml(appPath, appArgument, schedule);
        var tempPath = Path.Combine(Path.GetTempPath(), $"remotevault-task-{Guid.NewGuid():N}.xml");

        await File.WriteAllTextAsync(tempPath, taskXml, System.Text.Encoding.Unicode, cancellationToken);

        try
        {
            await RunSchtasksAsync(new[]
            {
                "/Create",
                "/TN",
                taskName,
                "/XML",
                tempPath,
                "/F"
            }, cancellationToken);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private static async Task DeleteTaskIfExistsAsync(string taskName, CancellationToken cancellationToken)
    {
        var queryExitCode = await RunSchtasksAsync(new[]
        {
            "/Query",
            "/TN",
            taskName
        }, cancellationToken, throwOnFailure: false);

        if (queryExitCode.ExitCode != 0)
        {
            return;
        }

        await RunSchtasksAsync(new[]
        {
            "/Delete",
            "/TN",
            taskName,
            "/F"
        }, cancellationToken);
    }

    private static async Task<SchtasksResult> RunSchtasksAsync(IEnumerable<string> arguments, CancellationToken cancellationToken, bool throwOnFailure = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var result = new SchtasksResult(process.ExitCode, stdout, stderr);

        if (throwOnFailure && result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            throw new InvalidOperationException(detail.Trim());
        }

        return result;
    }

    private static string NormalizeFrequency(string? frequency)
    {
        return string.Equals((frequency ?? string.Empty).Trim(), "hourly", StringComparison.OrdinalIgnoreCase)
            ? "hourly"
            : string.Equals((frequency ?? string.Empty).Trim(), "weekly", StringComparison.OrdinalIgnoreCase)
                ? "weekly"
                : string.Equals((frequency ?? string.Empty).Trim(), "monthly", StringComparison.OrdinalIgnoreCase)
                    ? "monthly"
                    : "daily";
    }

    private static string NormalizeDailyTime(string? time)
    {
        if (TimeSpan.TryParse(time, out var parsed))
        {
            return $"{parsed.Hours:00}:{parsed.Minutes:00}";
        }

        return "02:00";
    }

    private static string NormalizeWeekday(string? weekday)
    {
        return (weekday ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "tuesday" => "Tuesday",
            "wednesday" => "Wednesday",
            "thursday" => "Thursday",
            "friday" => "Friday",
            "saturday" => "Saturday",
            "sunday" => "Sunday",
            _ => "Monday"
        };
    }

    private static int NormalizeDayOfMonth(int dayOfMonth)
    {
        return Math.Clamp(dayOfMonth, 1, 31);
    }

    private static string BuildTaskXml(string appPath, string appArgument, StoredBackupSchedule schedule)
    {
        var now = DateTime.Now;
        var startBoundary = BuildStartBoundary(now, schedule);
        var escapedCommand = SecurityElement.Escape(appPath) ?? string.Empty;
        var escapedArguments = SecurityElement.Escape(appArgument) ?? string.Empty;
        var trigger = BuildTriggerXml(schedule, startBoundary);

        var document = $"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Author>RemoteVault</Author>
    <Description>RemoteVault scheduled backup task.</Description>
  </RegistrationInfo>
  <Triggers>
    {trigger}
  </Triggers>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT72H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Actions Context="Author">
    <Exec>
      <Command>{escapedCommand}</Command>
      <Arguments>{escapedArguments}</Arguments>
    </Exec>
  </Actions>
</Task>
""";

        _ = XDocument.Parse(document);
        return document;
    }

    private static string BuildTriggerXml(StoredBackupSchedule schedule, DateTime startBoundary)
    {
        var frequency = NormalizeFrequency(schedule.Frequency);
        var start = startBoundary.ToString("yyyy-MM-dd'T'HH:mm:ss");

        if (string.Equals(frequency, "hourly", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
<CalendarTrigger>
  <StartBoundary>{start}</StartBoundary>
  <Enabled>true</Enabled>
  <Repetition>
    <Interval>PT{Math.Clamp(schedule.IntervalHours, 1, 24)}H</Interval>
    <Duration>P1D</Duration>
    <StopAtDurationEnd>false</StopAtDurationEnd>
  </Repetition>
  <ScheduleByDay>
    <DaysInterval>1</DaysInterval>
  </ScheduleByDay>
</CalendarTrigger>
""";
        }

        if (string.Equals(frequency, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
<CalendarTrigger>
  <StartBoundary>{start}</StartBoundary>
  <Enabled>true</Enabled>
  <ScheduleByWeek>
    <WeeksInterval>1</WeeksInterval>
    <DaysOfWeek>
      <{NormalizeWeekday(schedule.Weekday)} />
    </DaysOfWeek>
  </ScheduleByWeek>
</CalendarTrigger>
""";
        }

        if (string.Equals(frequency, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            return $"""
<CalendarTrigger>
  <StartBoundary>{start}</StartBoundary>
  <Enabled>true</Enabled>
  <ScheduleByMonth>
    <DaysOfMonth>
      <Day>{NormalizeDayOfMonth(schedule.DayOfMonth)}</Day>
    </DaysOfMonth>
    <Months>
      <January />
      <February />
      <March />
      <April />
      <May />
      <June />
      <July />
      <August />
      <September />
      <October />
      <November />
      <December />
    </Months>
  </ScheduleByMonth>
</CalendarTrigger>
""";
        }

        return $"""
<CalendarTrigger>
  <StartBoundary>{start}</StartBoundary>
  <Enabled>true</Enabled>
  <ScheduleByDay>
    <DaysInterval>1</DaysInterval>
  </ScheduleByDay>
</CalendarTrigger>
""";
    }

    private static DateTime BuildStartBoundary(DateTime now, StoredBackupSchedule schedule)
    {
        var time = ParseDailyTime(schedule.DailyTime);
        var todayAtTime = new DateTime(now.Year, now.Month, now.Day, time.Hours, time.Minutes, 0, DateTimeKind.Local);
        var frequency = NormalizeFrequency(schedule.Frequency);

        if (string.Equals(frequency, "hourly", StringComparison.OrdinalIgnoreCase))
        {
            return todayAtTime <= now ? todayAtTime.AddDays(1) : todayAtTime;
        }

        if (string.Equals(frequency, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            var targetDay = ParseWeekday(schedule.Weekday);
            var daysUntil = ((int)targetDay - (int)now.DayOfWeek + 7) % 7;
            var candidate = todayAtTime.AddDays(daysUntil);
            return candidate <= now ? candidate.AddDays(7) : candidate;
        }

        if (string.Equals(frequency, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            var day = NormalizeDayOfMonth(schedule.DayOfMonth);
            var currentMonthDay = Math.Min(day, DateTime.DaysInMonth(now.Year, now.Month));
            var candidate = new DateTime(now.Year, now.Month, currentMonthDay, time.Hours, time.Minutes, 0, DateTimeKind.Local);
            if (candidate > now)
            {
                return candidate;
            }

            var nextMonth = now.AddMonths(1);
            var nextMonthDay = Math.Min(day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
            return new DateTime(nextMonth.Year, nextMonth.Month, nextMonthDay, time.Hours, time.Minutes, 0, DateTimeKind.Local);
        }

        return todayAtTime <= now ? todayAtTime.AddDays(1) : todayAtTime;
    }

    private static DayOfWeek ParseWeekday(string? weekday)
    {
        return (weekday ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "tuesday" => DayOfWeek.Tuesday,
            "wednesday" => DayOfWeek.Wednesday,
            "thursday" => DayOfWeek.Thursday,
            "friday" => DayOfWeek.Friday,
            "saturday" => DayOfWeek.Saturday,
            "sunday" => DayOfWeek.Sunday,
            _ => DayOfWeek.Monday
        };
    }

    private static TimeSpan ParseDailyTime(string? time)
    {
        return TimeSpan.TryParse(time, out var parsed)
            ? parsed
            : new TimeSpan(2, 0, 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private readonly record struct SchtasksResult(int ExitCode, string StandardOutput, string StandardError);
}
