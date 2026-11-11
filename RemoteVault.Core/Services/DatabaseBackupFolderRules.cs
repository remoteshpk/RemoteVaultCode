namespace RemoteVault.Core.Services;

/// <summary>
/// Where database backup files may be written locally.
/// SQL Server writes its .bak files itself, under its own Windows service account, and that
/// account has no access to user profile folders (Documents, Downloads, Desktop...). A folder
/// directly on a drive works, so profile folders are refused up front instead of failing later
/// with "Operating system error 5 (Access is denied)".
/// </summary>
public static class DatabaseBackupFolderRules
{
    public const string SuggestedFolder = @"C:\RemoteVaultDB";

    /// <summary>Returns null when the folder is acceptable, otherwise a message for the user.</summary>
    public static string? GetProblem(string? folder)
    {
        var value = (folder ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return $"Choose a local folder for database backup files, for example {SuggestedFolder}.";
        }

        string fullPath;
        try
        {
            if (!Path.IsPathFullyQualified(value))
            {
                return $"Enter the full path of the folder, starting with the drive, for example {SuggestedFolder}.";
            }

            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"This is not a valid folder path. Choose a folder on C: or another drive, for example {SuggestedFolder}.";
        }

        if (IsInsideUserProfile(fullPath))
        {
            return "Database backup files cannot be saved inside a user profile folder such as Documents, Downloads or Desktop, " +
                   "because SQL Server is not allowed to write there. " +
                   $"Choose a folder directly on C: or on another drive, for example {SuggestedFolder}.";
        }

        return null;
    }

    /// <summary>True for any path at or below the Windows profiles directory (normally C:\Users).</summary>
    public static bool IsInsideUserProfile(string? folder)
    {
        var value = (folder ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (var root in GetProfileRoots())
        {
            if (IsSameOrBelow(fullPath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> GetProfileRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The current user's profile, and its parent: the folder holding every profile.
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            roots.Add(userProfile);
            var parent = Path.GetDirectoryName(userProfile.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parent) && Path.GetPathRoot(parent) != parent)
            {
                roots.Add(parent);
            }
        }

        // Fallback for services or unusual accounts whose own profile lives elsewhere.
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (!string.IsNullOrWhiteSpace(systemDrive))
        {
            roots.Add(Path.Combine(systemDrive + Path.DirectorySeparatorChar, "Users"));
        }

        return roots;
    }

    private static bool IsSameOrBelow(string path, string root)
    {
        var normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
