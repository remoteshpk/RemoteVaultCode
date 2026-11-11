using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RemoteVault.Core.Services;

public sealed class NativeDatabaseBackupService : IDatabaseBackupService
{
    private readonly IOperationHistoryStore _operationHistoryStore;
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public NativeDatabaseBackupService(IOperationHistoryStore operationHistoryStore)
    {
        _operationHistoryStore = operationHistoryStore;
    }

    public async Task<DatabaseBackupLoadResult> LoadProfilesAsync(
        string configPath,
        CancellationToken cancellationToken = default)
    {
        var (_, profilesPath, lockPath, _) = GetPaths(configPath);
        if (!File.Exists(profilesPath))
        {
            return new DatabaseBackupLoadResult
            {
                IsSuccess = true,
                ExitCode = 0,
                Message = "No DB backup profiles found yet.",
                IsLocked = File.Exists(lockPath)
            };
        }

        try
        {
            var json = await File.ReadAllTextAsync(profilesPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new DatabaseBackupLoadResult
                {
                    IsSuccess = true,
                    ExitCode = 0,
                    Message = "No DB backup profiles found yet.",
                    IsLocked = File.Exists(lockPath)
                };
            }

            var document = JsonSerializer.Deserialize<NativeDbProfilesDocument>(json);
            var profiles = (document?.Profiles ?? [])
                .Select(MapStoredToPayload)
                .ToList();

            return new DatabaseBackupLoadResult
            {
                IsSuccess = true,
                ExitCode = 0,
                Message = profiles.Count == 0 ? "No DB backup profiles found yet." : "Profiles loaded.",
                IsLocked = File.Exists(lockPath),
                Profiles = profiles
            };
        }
        catch (Exception ex)
        {
            return new DatabaseBackupLoadResult
            {
                IsSuccess = false,
                ExitCode = 1,
                Message = $"Failed to read DB profiles: {ex.Message}"
            };
        }
    }

    public async Task<DatabaseBackupSaveResult> SaveProfilesAsync(
        string configPath,
        IReadOnlyList<DatabaseBackupProfilePayload> profiles,
        CancellationToken cancellationToken = default)
    {
        var (_, profilesPath, lockPath, _) = GetPaths(configPath);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(profilesPath)!);

            var document = new NativeDbProfilesDocument
            {
                Profiles = profiles.Select(MapPayloadToStored).ToList()
            };

            var tempPath = profilesPath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
            }

            File.Move(tempPath, profilesPath, overwrite: true);
            await File.WriteAllTextAsync(lockPath, "locked" + Environment.NewLine, Encoding.UTF8, cancellationToken);

            return new DatabaseBackupSaveResult
            {
                IsSuccess = true,
                ExitCode = 0,
                Message = "Profiles saved.",
                IsLocked = true
            };
        }
        catch (Exception ex)
        {
            return new DatabaseBackupSaveResult
            {
                IsSuccess = false,
                ExitCode = 2,
                Message = $"Failed to save profiles: {ex.Message}",
                IsLocked = File.Exists(lockPath)
            };
        }
    }

    public async Task<DatabaseBackupDiscoverResult> DiscoverDatabasesAsync(
        string configPath,
        DatabaseBackupProfilePayload profile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var storedConfig = await LocalConfigService.LoadFromPathAsync(configPath, cancellationToken);
            var effectiveProfile = ApplyGlobalToolPaths(profile, storedConfig);
            var validation = ValidateDiscoveryProfile(effectiveProfile);
            if (validation.Count > 0)
            {
                return new DatabaseBackupDiscoverResult
                {
                    IsSuccess = false,
                    ExitCode = 3,
                    Message = string.Join(Environment.NewLine, validation)
                };
            }

            var databases = (effectiveProfile.DbType ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "mysql" => await DiscoverMySqlAsync(effectiveProfile, cancellationToken),
                "postgres" => await DiscoverPostgresAsync(effectiveProfile, cancellationToken),
                _ => await DiscoverMsSqlAsync(effectiveProfile, cancellationToken)
            };

            return new DatabaseBackupDiscoverResult
            {
                IsSuccess = true,
                ExitCode = 0,
                Message = $"Discovered {databases.Count} database(s).",
                Databases = databases
            };
        }
        catch (Exception ex)
        {
            return new DatabaseBackupDiscoverResult
            {
                IsSuccess = false,
                ExitCode = 4,
                Message = $"Database discovery failed: {ex.Message}"
            };
        }
    }

    public Task<DatabaseBackupRunResult> RunNowAsync(
        DatabaseBackupRunRequest request,
        Action<DatabaseBackupWorkerEvent>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        return RunWithLockAsync(request, _operationHistoryStore, onProgress, cancellationToken);
    }

    private static async Task<DatabaseBackupRunResult> RunWithLockAsync(
        DatabaseBackupRunRequest request,
        IOperationHistoryStore operationHistoryStore,
        Action<DatabaseBackupWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        if (!await RunLock.WaitAsync(0, cancellationToken))
        {
            return new DatabaseBackupRunResult
            {
                IsSuccess = false,
                ExitCode = -99,
                Message = "A DB backup is already running. Please wait for it to finish."
            };
        }

        try
        {
            return await RunNowNativeAsync(request, operationHistoryStore, onProgress, cancellationToken);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private static async Task<DatabaseBackupRunResult> RunNowNativeAsync(
        DatabaseBackupRunRequest request,
        IOperationHistoryStore operationHistoryStore,
        Action<DatabaseBackupWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.ConfigPath) || !File.Exists(request.ConfigPath))
        {
            return new DatabaseBackupRunResult
            {
                IsSuccess = false,
                ExitCode = -1,
                Message = $"Config file not found: {request.ConfigPath}"
            };
        }

        var storedConfig = await LocalConfigService.LoadFromPathAsync(request.ConfigPath, cancellationToken);

        if (storedConfig is null)
        {
            return new DatabaseBackupRunResult
            {
                IsSuccess = false,
                ExitCode = -2,
                Message = "Configuration is missing or invalid."
            };
        }

        var (_, _, _, dbBackupRoot) = GetPaths(request.ConfigPath, storedConfig);
        if (DatabaseBackupFolderRules.GetProblem(dbBackupRoot) is { } folderProblem)
        {
            return new DatabaseBackupRunResult
            {
                IsSuccess = false,
                ExitCode = -6,
                Message = $"{folderProblem} Change the folder in Settings, under Database backup. Current folder: {dbBackupRoot}"
            };
        }

        Directory.CreateDirectory(dbBackupRoot);

        var selectedIds = request.SelectedProfileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var selectedProfiles = (selectedIds.Count == 0
                ? request.Profiles
                : request.Profiles.Where(profile => selectedIds.Contains(profile.ProfileId)).ToList())
            .Select(profile => ApplyGlobalToolPaths(profile, storedConfig))
            .ToList();

        if (selectedProfiles.Count == 0)
        {
            return new DatabaseBackupRunResult
            {
                IsSuccess = false,
                ExitCode = -3,
                Message = "No DB backup profile was selected."
            };
        }

        var backupType = string.Equals(request.BackupType, "diff", StringComparison.OrdinalIgnoreCase)
            ? "diff"
            : "full";

        var results = new List<DatabaseBackupRunProfileResult>();
        var createdArtifacts = new List<string>();
        var anyError = false;

        foreach (var profile in selectedProfiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EmitDbProgress(onProgress, "status",
                $"Starting {backupType.ToUpperInvariant()} backup for {profile.Name}...",
                profile.ProfileId,
                profile.Name,
                backupType);

            try
            {
                var dbNames = (profile.Databases ?? [])
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (dbNames.Count == 0)
                {
                    throw new InvalidOperationException("Please configure at least one database for this profile.");
                }

                if (backupType == "diff" &&
                    !string.Equals(profile.DbType, "mssql", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Differential backups are only available for MSSQL profiles.");
                }

                foreach (var dbName in dbNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EmitDbProgress(onProgress, "status",
                        $"Running {backupType.ToUpperInvariant()} for {profile.Name} / {dbName}...",
                        profile.ProfileId,
                        profile.Name,
                        backupType);

                    var artifact = await RunOneBackupAsync(storedConfig, profile, backupType, dbName, dbBackupRoot, onProgress, cancellationToken);
                    createdArtifacts.Add(artifact);
                }

                results.Add(new DatabaseBackupRunProfileResult
                {
                    ProfileId = profile.ProfileId,
                    ProfileName = profile.Name,
                    Ok = true,
                    Message = $"{backupType.ToUpperInvariant()} backup completed."
                });
            }
            catch (Exception ex)
            {
                anyError = true;
                results.Add(new DatabaseBackupRunProfileResult
                {
                    ProfileId = profile.ProfileId,
                    ProfileName = profile.Name,
                    Ok = false,
                    Message = ex.Message
                });
                EmitDbProgress(onProgress, "fatal", ex.Message, profile.ProfileId, profile.Name, backupType);
            }
        }

        if (createdArtifacts.Count > 0)
        {
            try
            {
                await UploadArtifactsAsync(storedConfig, dbBackupRoot, createdArtifacts, onProgress, cancellationToken);
            }
            catch (Exception ex)
            {
                anyError = true;
                results.Add(new DatabaseBackupRunProfileResult
                {
                    ProfileId = string.Empty,
                    ProfileName = "Upload",
                    Ok = false,
                    Message = ex.Message
                });
                EmitDbProgress(onProgress, "fatal", ex.Message, string.Empty, "Upload", backupType);
            }
        }

        var lines = results
            .Select(result => $"{result.ProfileName}: {result.Message}")
            .ToList();

        EmitDbProgress(onProgress, "completed", string.Join(Environment.NewLine, lines), null, null, backupType);

        var result = new DatabaseBackupRunResult
        {
            IsSuccess = !anyError,
            ExitCode = anyError ? 1 : 0,
            Message = lines.Count == 0
                ? "Done."
                : string.Join(Environment.NewLine, lines),
            Results = results
        };
        await operationHistoryStore.AppendAsync(
            BuildDbBackupEntry(storedConfig, backupType, selectedProfiles.Count, result),
            cancellationToken);
        return result;
    }

    private static OperationHistoryEntry BuildDbBackupEntry(
        StoredAppConfig config,
        string backupType,
        int profileCount,
        DatabaseBackupRunResult result)
    {
        var successCount = result.Results.Count(item => item.Ok);
        var failedCount = result.Results.Count(item => !item.Ok);
        var status = failedCount == 0 ? "success" : (successCount > 0 ? "partial" : "failed");

        return new OperationHistoryEntry
        {
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            Category = "dbbackup",
            Action = backupType,
            Status = status,
            Summary = result.IsSuccess
                ? $"{backupType.ToUpperInvariant()} DB backup completed."
                : $"{backupType.ToUpperInvariant()} DB backup finished with issues.",
            Source = $"{profileCount} profile(s)",
            Target = string.IsNullOrWhiteSpace(config.BucketName) ? "No target" : config.BucketName,
            Provider = config.Provider ?? string.Empty,
            TotalCount = result.Results.Count,
            SuccessCount = successCount,
            FailedCount = failedCount,
            Detail = result.Message
        };
    }

    private static async Task<List<string>> DiscoverMySqlAsync(DatabaseBackupProfilePayload profile, CancellationToken cancellationToken)
    {
        var mysqldumpPath = ResolveMysqldumpPath(profile.MysqldumpPath);
        if (string.IsNullOrWhiteSpace(mysqldumpPath) || !File.Exists(mysqldumpPath))
        {
            throw new FileNotFoundException($"mysqldump not found: {mysqldumpPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = mysqldumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"--host={profile.Host.Trim()}");
        startInfo.ArgumentList.Add($"--port={profile.Port}");
        startInfo.ArgumentList.Add($"--user={profile.Username.Trim()}");
        startInfo.ArgumentList.Add("--no-data");
        startInfo.ArgumentList.Add("--all-databases");
        startInfo.Environment["MYSQL_PWD"] = profile.Password ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Discovery failed (exit={process.ExitCode}): {stderr.Trim()}");
        }

        var databases = stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("-- Current Database:", StringComparison.Ordinal))
            .Select(ParseMySqlDatabaseName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (databases.Count == 0)
        {
            throw new InvalidOperationException("No databases discovered (mysqldump output did not contain any database headers).");
        }

        return databases;
    }

    private static async Task<List<string>> DiscoverMsSqlAsync(DatabaseBackupProfilePayload profile, CancellationToken cancellationToken)
    {
        var sqlcmdPath = ResolveSqlcmdPath(profile.SqlcmdPath);
        if (string.IsNullOrWhiteSpace(sqlcmdPath))
        {
            throw new FileNotFoundException("sqlcmd.exe not found. Install SQL Server tools or add sqlcmd.exe to PATH.");
        }

        var query = "SET NOCOUNT ON; SELECT name FROM sys.databases WHERE state_desc='ONLINE' ORDER BY name;";
        string lastError = "MSSQL discovery failed.";

        foreach (var serverTarget in BuildSqlServerTargets(profile))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = sqlcmdPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(serverTarget);
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("5");
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("30");

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                startInfo.ArgumentList.Add("-E");
            }
            else
            {
                startInfo.ArgumentList.Add("-U");
                startInfo.ArgumentList.Add(profile.Username.Trim());
                startInfo.ArgumentList.Add("-P");
                startInfo.ArgumentList.Add(profile.Password ?? string.Empty);
            }

            startInfo.ArgumentList.Add("-Q");
            startInfo.ArgumentList.Add(query);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                return stdout
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => line.Trim())
                    .Where(line => !string.IsNullOrWhiteSpace(line) && !string.Equals(line, "name", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            lastError = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        }

        throw new InvalidOperationException($"MSSQL discovery failed: {lastError}");
    }

    private static async Task<List<string>> DiscoverPostgresAsync(DatabaseBackupProfilePayload profile, CancellationToken cancellationToken)
    {
        var psqlPath = ResolvePostgresToolPath(profile.PgDumpPath, "psql.exe", "psql");
        if (string.IsNullOrWhiteSpace(psqlPath))
        {
            throw new FileNotFoundException("psql not found. Install PostgreSQL client tools or add psql to PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = psqlPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(profile.Host.Trim());
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(profile.Port.ToString());
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(profile.Username.Trim());
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add("postgres");
        startInfo.ArgumentList.Add("-At");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("SELECT datname FROM pg_database WHERE datistemplate = false ORDER BY datname;");
        startInfo.Environment["PGPASSWORD"] = profile.Password ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PostgreSQL discovery failed (exit={process.ExitCode}): {stderr.Trim()}");
        }

        return stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<string> RunOneBackupAsync(
        StoredAppConfig storedConfig,
        DatabaseBackupProfilePayload profile,
        string backupType,
        string dbName,
        string dbBackupRoot,
        Action<DatabaseBackupWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        var destinationDir = GetBackupDestinationDirectory(dbBackupRoot, profile, dbName, backupType);
        Directory.CreateDirectory(destinationDir);

        if (string.Equals(backupType, "diff", StringComparison.OrdinalIgnoreCase))
        {
            var fullDir = GetBackupDestinationDirectory(dbBackupRoot, profile, dbName, "full");
            var existingFulls = ListVersionedFiles(fullDir);
            if (existingFulls.Count == 0)
            {
                throw new InvalidOperationException($"No local FULL backup found for db={dbName}. Please run a FULL backup first.");
            }
        }

        var stagingDir = Path.Combine(destinationDir, "_staging");
        Directory.CreateDirectory(stagingDir);

        string rawArtifactPath = (profile.DbType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mysql" => await RunMySqlDumpAsync(profile, dbName, stagingDir, cancellationToken),
            "postgres" => await RunPostgresDumpAsync(profile, dbName, stagingDir, cancellationToken),
            _ => await RunMsSqlBackupAsync(profile, dbName, stagingDir, backupType, cancellationToken)
        };

        var timeStamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
        var rawExtension = Path.GetExtension(rawArtifactPath);
        var baseName = $"{dbName}-{backupType.ToUpperInvariant()}-v{timeStamp}";
        var finalName = $"{baseName}{rawExtension}.gz";
        var finalPath = await CompressToFinalAsync(rawArtifactPath, destinationDir, finalName, cancellationToken);

        EmitDbProgress(onProgress, "status",
            $"Created artifact: {finalPath}",
            profile.ProfileId,
            profile.Name,
            backupType);

        ApplyRetention(profile, dbName, dbBackupRoot);
        return finalPath;
    }

    private static async Task<string> RunMySqlDumpAsync(
        DatabaseBackupProfilePayload profile,
        string dbName,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var mysqldumpPath = ResolveMysqldumpPath(profile.MysqldumpPath);
        if (string.IsNullOrWhiteSpace(mysqldumpPath) || !File.Exists(mysqldumpPath))
        {
            throw new FileNotFoundException($"mysqldump not found: {mysqldumpPath}");
        }

        var tempPath = Path.Combine(stagingDir, $"{dbName}-FULL.tmp.sql");
        var startInfo = new ProcessStartInfo
        {
            FileName = mysqldumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"--host={profile.Host.Trim()}");
        startInfo.ArgumentList.Add($"--port={profile.Port}");
        startInfo.ArgumentList.Add($"--user={profile.Username.Trim()}");
        startInfo.ArgumentList.Add("--single-transaction");
        startInfo.ArgumentList.Add("--routines");
        startInfo.ArgumentList.Add("--triggers");
        startInfo.ArgumentList.Add("--events");
        startInfo.ArgumentList.Add("--databases");
        startInfo.ArgumentList.Add(dbName);
        startInfo.Environment["MYSQL_PWD"] = profile.Password ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        await using (var output = File.Create(tempPath))
        {
            await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        }

        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            TryDelete(tempPath);
            throw new InvalidOperationException($"mysqldump failed for '{dbName}' (exit={process.ExitCode}): {stderr.Trim()}");
        }

        EnsureNotEmpty(tempPath, $"mysqldump produced empty output for '{dbName}'");
        return tempPath;
    }

    private static async Task<string> RunPostgresDumpAsync(
        DatabaseBackupProfilePayload profile,
        string dbName,
        string stagingDir,
        CancellationToken cancellationToken)
    {
        var pgDumpPath = ResolvePostgresToolPath(profile.PgDumpPath, "pg_dump.exe", "pg_dump");
        if (string.IsNullOrWhiteSpace(pgDumpPath))
        {
            throw new FileNotFoundException("pg_dump not found. Install PostgreSQL client tools or add pg_dump to PATH.");
        }

        var tempPath = Path.Combine(stagingDir, $"{dbName}-FULL.dump");
        var startInfo = new ProcessStartInfo
        {
            FileName = pgDumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-h");
        startInfo.ArgumentList.Add(profile.Host.Trim());
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(profile.Port.ToString());
        startInfo.ArgumentList.Add("-U");
        startInfo.ArgumentList.Add(profile.Username.Trim());
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(dbName);
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add("c");
        startInfo.ArgumentList.Add("--no-owner");
        startInfo.ArgumentList.Add("--no-privileges");
        startInfo.ArgumentList.Add("--verbose");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(tempPath);
        startInfo.Environment["PGPASSWORD"] = profile.Password ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            TryDelete(tempPath);
            throw new InvalidOperationException($"PostgreSQL FULL backup failed for '{dbName}' (exit={process.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim())}");
        }

        EnsureNotEmpty(tempPath, $"PostgreSQL FULL backup produced empty output for '{dbName}'");
        return tempPath;
    }

    private static async Task<string> RunMsSqlBackupAsync(
        DatabaseBackupProfilePayload profile,
        string dbName,
        string stagingDir,
        string backupType,
        CancellationToken cancellationToken)
    {
        var sqlcmdPath = ResolveSqlcmdPath(profile.SqlcmdPath);
        if (string.IsNullOrWhiteSpace(sqlcmdPath))
        {
            throw new FileNotFoundException("sqlcmd.exe not found. Install SQL Server tools or add sqlcmd.exe to PATH.");
        }

        var suffix = string.Equals(backupType, "diff", StringComparison.OrdinalIgnoreCase) ? "DIFF" : "FULL";
        var tempPath = Path.Combine(stagingDir, $"{dbName}-{suffix}.bak");

        // ZSTD backup compression exists only on SQL Server 2025 (major version 17) and later.
        // The server picks the option itself, so each connection target is checked on its own;
        // the BACKUP statement is built as dynamic SQL because older servers cannot parse the ZSTD syntax.
        var differential = string.Equals(backupType, "diff", StringComparison.OrdinalIgnoreCase) ? "DIFFERENTIAL, " : string.Empty;
        var quotedDbName = dbName.Replace("]", "]]").Replace("'", "''");
        var query =
            $"DECLARE @p NVARCHAR(4000) = N'{tempPath.Replace("'", "''")}'; " +
            "DECLARE @c NVARCHAR(100) = N'COMPRESSION'; " +
            "IF CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS NVARCHAR(128)), 4) AS INT) >= 17 " +
            "SET @c = N'COMPRESSION (ALGORITHM = ZSTD, LEVEL = MEDIUM)'; " +
            $"DECLARE @s NVARCHAR(MAX) = N'BACKUP DATABASE [{quotedDbName}] TO DISK = @p WITH {differential}INIT, CHECKSUM, ' + @c + N', STATS=5;'; " +
            "EXEC sp_executesql @s, N'@p NVARCHAR(4000)', @p = @p;";

        string lastError = "MSSQL backup failed.";
        foreach (var target in BuildSqlServerTargets(profile))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = sqlcmdPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("-b");
            startInfo.ArgumentList.Add("-r");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-N");
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("5");
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("30");

            if (string.IsNullOrWhiteSpace(profile.Username))
            {
                startInfo.ArgumentList.Add("-E");
            }
            else
            {
                startInfo.ArgumentList.Add("-U");
                startInfo.ArgumentList.Add(profile.Username.Trim());
                startInfo.ArgumentList.Add("-P");
                startInfo.ArgumentList.Add(profile.Password ?? string.Empty);
            }

            startInfo.ArgumentList.Add("-Q");
            startInfo.ArgumentList.Add(query);

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (process.ExitCode == 0)
            {
                EnsureNotEmpty(tempPath, $"MSSQL {suffix} backup produced empty/invalid file for '{dbName}'");
                return tempPath;
            }

            lastError = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        }

        TryDelete(tempPath);
        var hint = (lastError ?? string.Empty).Contains("Operating system error 5", StringComparison.OrdinalIgnoreCase)
            ? " SQL Server is not allowed to write to this folder. In Settings, choose a database backup folder directly on C: or another drive, or give the SQL Server service account write access to the folder."
            : string.Empty;
        throw new InvalidOperationException($"MSSQL {suffix} backup failed for '{dbName}': {lastError}{hint}");
    }

    private static async Task<string> CompressToFinalAsync(
        string sourcePath,
        string destinationDirectory,
        string finalName,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var finalPath = Path.Combine(destinationDirectory, finalName);
        if (File.Exists(finalPath))
        {
            finalPath = EnsureUniqueVersionedName(destinationDirectory, finalName);
        }

        await using (var input = File.OpenRead(sourcePath))
        await using (var output = File.Create(finalPath))
        {
            await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                await input.CopyToAsync(gzip, cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
        }

        var finalInfo = new FileInfo(finalPath);
        finalInfo.Refresh();
        TryDelete(sourcePath);
        TryDeleteEmptyDirectory(Path.GetDirectoryName(sourcePath)!);
        EnsureFileExistsAndNotZero(finalPath, "Compressed artifact is empty.");
        return finalPath;
    }

    private static async Task UploadArtifactsAsync(
        StoredAppConfig config,
        string dbBackupRoot,
        IReadOnlyList<string> artifactPaths,
        Action<DatabaseBackupWorkerEvent>? onProgress,
        CancellationToken cancellationToken)
    {
        foreach (var artifactPath in artifactPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(dbBackupRoot, artifactPath).Replace("\\", "/");
            var remoteKey = $"dbbackup/{relativePath}";

            EmitDbProgress(onProgress, "status",
                $"Uploading DB artifact: {relativePath}",
                string.Empty,
                "Upload",
                "upload");

            if (string.Equals(config.Provider, "azureblob", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(config.Provider, "azure", StringComparison.OrdinalIgnoreCase))
            {
                var containerClient = NativeStorageSupport.CreateAzureContainerClient(config, config.BucketName.Trim());
                var blobClient = containerClient.GetBlobClient(remoteKey);
                await using var stream = File.OpenRead(artifactPath);
        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = "application/gzip"
            }
        }, cancellationToken);
            }
            else
            {
                using var client = NativeStorageSupport.CreateS3Client(config);
                await using var stream = File.OpenRead(artifactPath);
                await client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = config.BucketName.Trim(),
                    Key = remoteKey,
                    InputStream = stream,
                    AutoCloseStream = false,
                    ContentType = "application/gzip"
                }, cancellationToken);
            }
        }
    }

    private static DatabaseBackupProfilePayload MapStoredToPayload(NativeDbProfileRecord record)
    {
        var fullSchedule = record.FullSchedule;
        var diffSchedule = record.DiffSchedule;

        return new DatabaseBackupProfilePayload
        {
            ProfileId = record.ProfileId ?? string.Empty,
            Name = record.Name ?? string.Empty,
            DbType = string.IsNullOrWhiteSpace(record.DbType) ? "mssql" : record.DbType!,
            Host = record.Host ?? "localhost",
            Port = record.Port <= 0 ? GetDefaultPort(record.DbType) : record.Port,
            Username = record.Username ?? string.Empty,
            Password = Unprotect(record.Password),
            Instance = record.Instance ?? string.Empty,
            MysqldumpPath = record.MysqldumpPath ?? string.Empty,
            PgDumpPath = record.PgDumpPath ?? string.Empty,
            SqlcmdPath = record.SqlcmdPath ?? string.Empty,
            IncludeAllDatabases = record.IncludeAllDatabases,
            Databases = record.Databases ?? [],
            KeepFullChains = record.KeepFullChains <= 0 ? 4 : record.KeepFullChains,
            DiffEnabled = record.DiffEnabled,
            FullIntervalDays = fullSchedule is null || fullSchedule.IntervalMinutes <= 0 ? 1 : fullSchedule.IntervalMinutes,
            DiffIntervalMinutes = diffSchedule is null || diffSchedule.IntervalMinutes <= 0 ? 60 : diffSchedule.IntervalMinutes,
            FullScheduleKind = string.IsNullOrWhiteSpace(fullSchedule?.Kind) ? "daily" : fullSchedule.Kind,
            FullScheduleTime = string.IsNullOrWhiteSpace(fullSchedule?.TimeHhmm) ? "02:00" : fullSchedule.TimeHhmm,
            DiffScheduleKind = string.IsNullOrWhiteSpace(diffSchedule?.Kind) ? "interval" : diffSchedule.Kind
        };
    }

    private static NativeDbProfileRecord MapPayloadToStored(DatabaseBackupProfilePayload payload)
    {
        return new NativeDbProfileRecord
        {
            ProfileId = payload.ProfileId,
            Name = payload.Name,
            DbType = payload.DbType,
            Host = payload.Host,
            Port = payload.Port,
            Username = payload.Username,
            Password = Protect(payload.Password),
            Instance = payload.Instance,
            MysqldumpPath = payload.MysqldumpPath,
            PgDumpPath = payload.PgDumpPath,
            SqlcmdPath = payload.SqlcmdPath,
            IncludeAllDatabases = payload.IncludeAllDatabases,
            Databases = payload.Databases ?? [],
            KeepFullChains = payload.KeepFullChains,
            DiffEnabled = payload.DiffEnabled,
            FullSchedule = new NativeDbScheduleRecord
            {
                Enabled = true,
                Kind = string.IsNullOrWhiteSpace(payload.FullScheduleKind) ? "daily" : payload.FullScheduleKind,
                TimeHhmm = string.IsNullOrWhiteSpace(payload.FullScheduleTime) ? "02:00" : payload.FullScheduleTime,
                IntervalMinutes = payload.FullIntervalDays <= 0 ? 1 : payload.FullIntervalDays
            },
            DiffSchedule = new NativeDbScheduleRecord
            {
                Enabled = payload.DiffEnabled,
                Kind = string.IsNullOrWhiteSpace(payload.DiffScheduleKind) ? "interval" : payload.DiffScheduleKind,
                IntervalMinutes = payload.DiffIntervalMinutes <= 0 ? 60 : payload.DiffIntervalMinutes
            }
        };
    }

    private static List<string> ValidateDiscoveryProfile(DatabaseBackupProfilePayload profile)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            issues.Add("Host is required.");
        }

        if (string.IsNullOrWhiteSpace(profile.Username) &&
            !string.Equals((profile.DbType ?? string.Empty).Trim(), "mssql", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add("Username is required.");
        }

        if (profile.Port < 1 || profile.Port > 65535)
        {
            issues.Add("Port must be between 1 and 65535.");
        }

        if (string.Equals((profile.DbType ?? string.Empty).Trim(), "mysql", StringComparison.OrdinalIgnoreCase))
        {
            var mysqldumpPath = ResolveMysqldumpPath(profile.MysqldumpPath);
            if (string.IsNullOrWhiteSpace(mysqldumpPath) || !File.Exists(mysqldumpPath))
            {
                issues.Add($"mysqldump not found: {mysqldumpPath}");
            }
        }
        else if (string.Equals((profile.DbType ?? string.Empty).Trim(), "postgres", StringComparison.OrdinalIgnoreCase))
        {
            var psqlPath = ResolvePostgresToolPath(profile.PgDumpPath, "psql.exe", "psql");
            if (string.IsNullOrWhiteSpace(psqlPath) || !File.Exists(psqlPath))
            {
                issues.Add($"psql not found: {psqlPath}");
            }
        }
        else
        {
            var sqlcmdPath = ResolveSqlcmdPath(profile.SqlcmdPath);
            if (string.IsNullOrWhiteSpace(sqlcmdPath) || !File.Exists(sqlcmdPath))
            {
                issues.Add($"sqlcmd.exe not found: {sqlcmdPath}");
            }
        }

        return issues;
    }

    private static DatabaseBackupProfilePayload ApplyGlobalToolPaths(DatabaseBackupProfilePayload profile, StoredAppConfig? config)
    {
        return new DatabaseBackupProfilePayload
        {
            ProfileId = profile.ProfileId,
            Name = profile.Name,
            DbType = profile.DbType,
            Host = profile.Host,
            Port = profile.Port,
            Username = profile.Username,
            Password = profile.Password,
            Instance = profile.Instance,
            MysqldumpPath = string.IsNullOrWhiteSpace(config?.MysqldumpPath) ? profile.MysqldumpPath : config!.MysqldumpPath,
            PgDumpPath = string.IsNullOrWhiteSpace(config?.PgDumpPath) ? profile.PgDumpPath : config!.PgDumpPath,
            SqlcmdPath = string.IsNullOrWhiteSpace(config?.SqlcmdPath) ? profile.SqlcmdPath : config!.SqlcmdPath,
            IncludeAllDatabases = profile.IncludeAllDatabases,
            Databases = profile.Databases?.ToList() ?? [],
            KeepFullChains = profile.KeepFullChains,
            DiffEnabled = profile.DiffEnabled,
            FullIntervalDays = profile.FullIntervalDays,
            DiffIntervalMinutes = profile.DiffIntervalMinutes,
            FullScheduleKind = profile.FullScheduleKind,
            FullScheduleTime = profile.FullScheduleTime,
            DiffScheduleKind = profile.DiffScheduleKind
        };
    }

    public static string ResolveDatabaseBackupRoot(StoredAppConfig? config, string? configPath)
    {
        var configured = (config?.DatabaseBackupRootPath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var configFile = new FileInfo(configPath ?? string.Empty);
        var baseDirectory = configFile.Directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(baseDirectory, "data", "dbbackup");
    }

    private static (string dataDir, string profilesPath, string lockPath, string dbBackupDir) GetPaths(string configPath, StoredAppConfig? config = null)
    {
        var configFile = new FileInfo(configPath ?? string.Empty);
        var baseDirectory = configFile.Directory?.FullName ?? AppContext.BaseDirectory;
        var dataDirectory = Path.Combine(baseDirectory, "data");
        var profilesPath = Path.Combine(dataDirectory, "db_profiles.json");
        return (dataDirectory, profilesPath, profilesPath + ".lock", ResolveDatabaseBackupRoot(config, configPath));
    }

    private static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.LocalMachine);
            return Convert.ToBase64String(protectedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Unprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            var unprotectedBytes = ProtectedData.Unprotect(bytes, null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(unprotectedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int GetDefaultPort(string? dbType)
    {
        return (dbType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "mysql" => 3306,
            "postgres" => 5432,
            _ => 1433
        };
    }

    private static string ParseMySqlDatabaseName(string line)
    {
        var tickStart = line.IndexOf('`');
        var tickEnd = line.LastIndexOf('`');
        if (tickStart >= 0 && tickEnd > tickStart)
        {
            return line[(tickStart + 1)..tickEnd].Trim();
        }

        return string.Empty;
    }

    private static string ResolveMysqldumpPath(string? configuredPath)
    {
        var normalized = NormalizeWindowsPath(configuredPath);
        if (!string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized))
        {
            return normalized;
        }

        var candidates = new List<string>();
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)"
                 })
        {
            var mysqlRoot = Path.Combine(root, "MySQL");
            if (!Directory.Exists(mysqlRoot))
            {
                continue;
            }

            candidates.AddRange(Directory
                .EnumerateFiles(mysqlRoot, "mysqldump.exe", SearchOption.AllDirectories)
                .Where(path => path.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase)));
        }

        return candidates
            .OrderByDescending(GetVersionKey)
            .FirstOrDefault()
            ?? normalized;
    }

    private static string ResolveSqlcmdPath(string? configuredPath)
    {
        var normalized = NormalizeWindowsPath(configuredPath);
        if (!string.IsNullOrWhiteSpace(normalized) && File.Exists(normalized))
        {
            return normalized;
        }

        return FindSqlcmd() ?? normalized;
    }

    private static string ResolvePostgresToolPath(string? configuredPath, string windowsName, string portableName)
    {
        var normalized = NormalizeWindowsPath(configuredPath);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            if (File.Exists(normalized))
            {
                var fileName = Path.GetFileName(normalized);
                if (string.Equals(fileName, windowsName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fileName, portableName, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(fileName, windowsName, StringComparison.OrdinalIgnoreCase))
                    {
                        return normalized;
                    }

                    var siblingWindowsName = Path.Combine(Path.GetDirectoryName(normalized) ?? string.Empty, windowsName);
                    if (File.Exists(siblingWindowsName))
                    {
                        return siblingWindowsName;
                    }

                    return normalized;
                }

                var sibling = Path.Combine(Path.GetDirectoryName(normalized) ?? string.Empty, windowsName);
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
            else
            {
                var sibling = Path.Combine(normalized, windowsName);
                if (File.Exists(sibling))
                {
                    return sibling;
                }
            }
        }

        return FindPostgresTool(windowsName, portableName) ?? normalized;
    }

    private static string NormalizeWindowsPath(string? value)
    {
        var path = (value ?? string.Empty).Trim().Trim('"');
        if (path.StartsWith("\\") && !(path.Length >= 3 && path[1] == ':' && path[2] == '\\'))
        {
            path = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + path;
        }

        return path;
    }

    private static Version GetVersionKey(string path)
    {
        var match = Regex.Match(path, @"MySQL Server\s+(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version)
            ? version
            : new Version(0, 0);
    }

    private static string? FindSqlcmd()
    {
        var pathEntry = FindOnPath("sqlcmd.exe");
        if (!string.IsNullOrWhiteSpace(pathEntry))
        {
            return pathEntry;
        }

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
            Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)"
        };

        foreach (var root in roots)
        {
            var sqlRoot = Path.Combine(root, "Microsoft SQL Server");
            if (!Directory.Exists(sqlRoot))
            {
                continue;
            }

            var match = Directory.EnumerateFiles(sqlRoot, "sqlcmd.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Contains(@"Tools\Binn", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path.Length)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }
        }

        return null;
    }

    private static List<string> BuildSqlServerTargets(DatabaseBackupProfilePayload profile)
    {
        var host = string.IsNullOrWhiteSpace(profile.Host) ? "localhost" : profile.Host.Trim();
        var instance = (profile.Instance ?? string.Empty).Trim();
        var targets = new List<string>();
        var localAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".", "localhost", "(local)" };
        var machineName = Environment.GetEnvironmentVariable("COMPUTERNAME") ?? string.Empty;

        if (localAliases.Contains(host) || string.Equals(host, machineName, StringComparison.OrdinalIgnoreCase))
        {
            targets.Add("lpc:localhost");
        }

        if (!string.IsNullOrWhiteSpace(instance))
        {
            targets.Add($"{host}\\{instance}");
        }

        if (profile.Port > 0)
        {
            targets.Add($"tcp:{host},{profile.Port}");
        }

        targets.Add(host);
        return targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? FindPostgresTool(string windowsName, string portableName)
    {
        var fromPath = FindOnPath(windowsName) ?? FindOnPath(portableName);
        if (!string.IsNullOrWhiteSpace(fromPath))
        {
            return fromPath;
        }

        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files",
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)"
                 })
        {
            var basePath = Path.Combine(root, "PostgreSQL");
            if (!Directory.Exists(basePath))
            {
                continue;
            }

            var candidate = Directory.EnumerateDirectories(basePath)
                .Select(dir => Path.Combine(dir, "bin", windowsName))
                .Where(File.Exists)
                .OrderByDescending(path => path)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var entry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(entry, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static string GetBackupDestinationDirectory(string dbBackupRoot, DatabaseBackupProfilePayload profile, string dbName, string backupType)
    {
        var safeName = new string((profile.Name ?? "profile")
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_')
            .ToArray())
            .Trim('_');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "profile";
        }

        var profileFolder = safeName;
        return Path.Combine(
            dbBackupRoot,
            profileFolder,
            (profile.DbType ?? "mssql").Trim().ToLowerInvariant(),
            (profile.Host ?? "localhost").Trim(),
            dbName,
            backupType.ToUpperInvariant());
    }

    private static void ApplyRetention(DatabaseBackupProfilePayload profile, string dbName, string dbBackupRoot)
    {
        var keepFullChains = profile.KeepFullChains < 1 ? 1 : profile.KeepFullChains;
        var fullDir = GetBackupDestinationDirectory(dbBackupRoot, profile, dbName, "full");
        var diffDir = GetBackupDestinationDirectory(dbBackupRoot, profile, dbName, "diff");

        var fullFiles = ListVersionedFiles(fullDir);
        if (fullFiles.Count == 0)
        {
            return;
        }

        var retainedFulls = fullFiles.Take(keepFullChains).ToList();
        var removedFulls = fullFiles.Skip(keepFullChains).ToList();
        var cutoff = retainedFulls.Last().Timestamp;

        foreach (var file in removedFulls)
        {
            TryDelete(file.Path);
        }

        foreach (var file in ListVersionedFiles(diffDir))
        {
            if (file.Timestamp < cutoff)
            {
                TryDelete(file.Path);
            }
        }
    }

    private static List<(DateTime Timestamp, string Path)> ListVersionedFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Select(path => (Timestamp: ParseVersionTimestamp(Path.GetFileName(path)), Path: path))
            .Where(item => item.Timestamp.HasValue)
            .Select(item => (Timestamp: item.Timestamp!.Value, item.Path))
            .OrderByDescending(item => item.Timestamp)
            .ToList();
    }

    private static DateTime? ParseVersionTimestamp(string fileName)
    {
        var match = Regex.Match(fileName, @"-v(?<ts>\d{4}-\d{2}-\d{2}-\d{6})(?:-\d{3})?\.", RegexOptions.IgnoreCase);
        return match.Success && DateTime.TryParseExact(match.Groups["ts"].Value, "yyyy-MM-dd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static string EnsureUniqueVersionedName(string destinationDirectory, string finalName)
    {
        var extension = Path.GetExtension(finalName);
        var baseNameWithoutGz = finalName[..Math.Max(0, finalName.Length - extension.Length)];

        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(destinationDirectory, $"{baseNameWithoutGz}-{index:000}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(destinationDirectory, $"{Guid.NewGuid():N}{extension}");
    }

    private static void EnsureNotEmpty(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < 32)
        {
            TryDelete(path);
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureFileExistsAndNotZero(string path, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0)
        {
            TryDelete(path);
            throw new InvalidOperationException(message);
        }
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

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void EmitDbProgress(
        Action<DatabaseBackupWorkerEvent>? callback,
        string eventName,
        string? message,
        string? profileId,
        string? profileName,
        string? backupType)
    {
        callback?.Invoke(new DatabaseBackupWorkerEvent
        {
            EventName = eventName,
            Message = message,
            Payload = new DatabaseBackupWorkerPayload
            {
                ProfileId = profileId,
                ProfileName = profileName,
                BackupType = backupType,
                Line = message
            }
        });
    }

    private sealed class NativeDbProfilesDocument
    {
        [JsonPropertyName("profiles")]
        public List<NativeDbProfileRecord> Profiles { get; init; } = [];
    }

    private sealed class NativeDbProfileRecord
    {
        [JsonPropertyName("profile_id")]
        public string? ProfileId { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("db_type")]
        public string? DbType { get; init; }

        [JsonPropertyName("host")]
        public string? Host { get; init; }

        [JsonPropertyName("port")]
        public int Port { get; init; }

        [JsonPropertyName("username")]
        public string? Username { get; init; }

        [JsonPropertyName("password")]
        public string? Password { get; init; }

        [JsonPropertyName("instance")]
        public string? Instance { get; init; }

        [JsonPropertyName("mysqldump_path")]
        public string? MysqldumpPath { get; init; }

        [JsonPropertyName("pg_dump_path")]
        public string? PgDumpPath { get; init; }

        [JsonPropertyName("sqlcmd_path")]
        public string? SqlcmdPath { get; init; }

        [JsonPropertyName("include_all_databases")]
        public bool IncludeAllDatabases { get; init; }

        [JsonPropertyName("databases")]
        public List<string>? Databases { get; init; }

        [JsonPropertyName("keep_full_chains")]
        public int KeepFullChains { get; init; }

        [JsonPropertyName("diff_enabled")]
        public bool DiffEnabled { get; init; }

        [JsonPropertyName("full_schedule")]
        public NativeDbScheduleRecord? FullSchedule { get; init; }

        [JsonPropertyName("diff_schedule")]
        public NativeDbScheduleRecord? DiffSchedule { get; init; }
    }

    private sealed class NativeDbScheduleRecord
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }

        [JsonPropertyName("time_hhmm")]
        public string? TimeHhmm { get; init; }

        [JsonPropertyName("interval_minutes")]
        public int IntervalMinutes { get; init; }
    }
}
