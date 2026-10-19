using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using Optimizer.Core.Actions;
using Optimizer.Core.Startup;

namespace Optimizer.Core.Platform;

/// <summary>
/// Installs the downloaded exe into Program Files on the first start (the owner's decision, 2026-10-15): there only
/// administrators can change it, so the reapply task may start it, and Windows lists it under Installed apps. Only a copy
/// signed by Tunero's publisher is installed: the downloaded file sits in a folder the signed-in user can change.
/// </summary>
public static class AppInstall
{
    /// <summary>The Installed apps entry, under HKLM (64-bit view).</summary>
    public const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Tunero";

    public const string ExeName = "Tunero.exe";

    /// <summary>
    /// Certificate subjects accepted as Tunero's publisher, exactly as Windows shows them. A renewed certificate with
    /// another subject is added here in the release before it signs one (CONTRIBUTING, Code signing).
    /// </summary>
    public static IReadOnlyList<string> TrustedPublishers { get; } =
    [
        "CN=Open Source Developer Joshua Zürn, O=Open Source Developer, L=Neudenau, S=Baden-Württemberg, C=DE",
    ];

    /// <summary>
    /// C:\Program Files\Tunero. From the system's known folder, not from %ProgramW6432%, which the signed-in user can set
    /// (the app is x64, so this is the 64-bit Program Files).
    /// </summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tunero");

    /// <summary>The installed exe is newer than the running one: installing would replace it with an older version.</summary>
    public static bool IsDowngrade(string? installedVersion, string runningVersion) =>
        Version.TryParse(installedVersion, out var installed) && Version.TryParse(runningVersion, out var running) && installed > running;

    /// <summary>
    /// The setup screen shows for the published exe, run elevated from a plain start, signed by Tunero's publisher, not a
    /// package manager's copy, not installed already and not declined before.
    /// </summary>
    public static bool ShouldOffer(bool publishedExe, bool elevated, bool plainStart, bool packageCopy, bool declined, bool installedHere,
        bool trustedSignature) =>
        publishedExe && elevated && plainStart && trustedSignature && !packageCopy && !declined && !installedHere;

    /// <summary>A valid embedded signature (not a catalog's) whose certificate subject is one of <see cref="TrustedPublishers"/>.</summary>
    public static bool IsTrusted(SignatureInfo signature, string? subject) =>
        signature is { Status: SignatureStatus.Signed, Catalog: false } && subject is not null && TrustedPublishers.Contains(subject, StringComparer.Ordinal);

    /// <summary>
    /// <see cref="IsTrusted(SignatureInfo, string?)"/> for a file, whose chain must also end in a root of the machine's own
    /// stores (<see cref="IsMachineRoot"/>): WinVerifyTrust also trusts a root in the user's store, which any program of
    /// the user can add (after one prompt), with a made-up certificate of the same name under it. Check a file only where
    /// it cannot change between the reads (in the admin-only install folder); for a file in Downloads the answer only
    /// decides whether to offer.
    /// </summary>
    public static bool IsTrustedFile(string path)
    {
        var signature = SignatureVerifier.Verify(path);
        if (signature.Status != SignatureStatus.Signed) return false;
        return IsTrusted(signature, SignerSubject(path)) && signature.RootThumbprint is { } root && IsMachineRoot(root);
    }

    /// <summary>
    /// The root is in the local machine's Trusted Root (Root) or Third-Party Root (AuthRoot) store, which only
    /// administrators and Windows' root updates change; a root only in the user's own store does not count.
    /// </summary>
    public static bool IsMachineRoot(string thumbprint)
    {
        foreach (var name in new[] { "Root", "AuthRoot" })
        {
            try
            {
                using var store = new X509Store(name, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                var any = found.Count > 0;
                foreach (var c in found) c.Dispose();
                if (any) return true;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A store that cannot be opened holds nothing to trust.
            }
        }
        return false;
    }

    private static string? SignerSubject(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // reads the signer's certificate from the file's signature, not a certificate file
            using var certificate = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The values of the Installed apps entry for <paramref name="exe"/>.</summary>
    public static IReadOnlyDictionary<string, object> EntryValues(string exe, string version, long sizeKb) => new Dictionary<string, object>
    {
        ["DisplayName"] = "Tunero",
        ["DisplayVersion"] = version,
        ["Publisher"] = "Joshua Zürn",
        ["DisplayIcon"] = exe,
        ["InstallLocation"] = Path.GetDirectoryName(exe)!,
        ["UninstallString"] = $"\"{exe}\" --uninstall",
        ["URLInfoAbout"] = "https://tunero.dev",
        ["EstimatedSize"] = (int)Math.Clamp(sizeKb, 0, int.MaxValue),
        ["NoModify"] = 1,
        ["NoRepair"] = 1,
    };

    /// <summary>Writes (or refreshes, after an update) the Installed apps entry.</summary>
    public static void WriteEntry(IRegistryRoots registry, string exe, string version, long sizeKb)
    {
        using var key = registry.Open(Hive.Machine, KeyPath, writable: true, create: true)
            ?? throw new IOException($"HKLM\\{KeyPath} could not be created.");
        foreach (var (name, value) in EntryValues(exe, version, sizeKb))
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public static void RemoveEntry(IRegistryRoots registry)
    {
        using var parent = registry.Open(Hive.Machine, Path.GetDirectoryName(KeyPath)!, writable: true);
        parent?.DeleteSubKeyTree(Path.GetFileName(KeyPath), throwOnMissingSubKey: false);
    }

    /// <summary>The exe the entry names, when it exists; null without an entry.</summary>
    public static string? InstalledExe(IRegistryRoots registry)
    {
        using var key = registry.Open(Hive.Machine, KeyPath, writable: false);
        return key?.GetValue("DisplayIcon") is string exe && File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Copies the running exe into <see cref="InstallRequest.Folder"/>, checks the copy there (where it cannot change any
    /// more) and writes the Installed apps entry. A failed step undoes the earlier ones: an older installed exe stays.
    /// </summary>
    public static InstallResult Install(InstallRequest request, IRegistryRoots registry)
    {
        var folder = Path.GetFullPath(request.Folder);
        var exe = Path.Combine(folder, ExeName);
        var copy = exe + ".new";
        var previous = exe + ".old";
        var createdFolder = false;
        var step = InstallStep.Folder;
        var replaced = false;
        var placed = false;
        try
        {
            // An existing folder may hold only an earlier install (and what an interrupted install or update left): a DLL put
            // next to the exe would be loaded by it.
            if (Directory.Exists(folder))
            {
                string[] own = [ExeName, ExeName + ".old", ExeName + ".new"];
                var others = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)
                    .Where(name => !own.Contains(name, StringComparer.OrdinalIgnoreCase));
                if (others.Any()) return Fail(InstallStep.Folder, $"{folder} holds other files");
            }
            else
            {
                Directory.CreateDirectory(folder);
                createdFolder = true;
            }
            if (!request.FolderIsAdminOnly(folder)) return Fail(InstallStep.Folder, $"{folder} can be changed by users");

            step = InstallStep.Copy;
            if (File.Exists(copy)) SafeDelete.DeleteFile(copy);
            // A .old without an installed exe beside it is left from an interrupted run: Undo would put it back as if it
            // were the earlier install.
            if (!File.Exists(exe) && File.Exists(previous)) SafeDelete.DeleteFile(previous);
            using (var source = new FileStream(request.RunningExe, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = SafeDelete.CreateNew(copy))
            {
                source.CopyTo(target);
            }

            step = InstallStep.Check;
            // The file's own ACL too: an inherit-only entry on the folder (which the folder check skips) can give users
            // write access to new files in it.
            if (!request.CopyIsAdminOnly(copy)) return Fail(InstallStep.Check, "users can change the copy");
            if (!request.Verify(copy)) return Fail(InstallStep.Check, "the copy is not signed by Tunero's publisher");
            if (request.FileVersion(copy) != request.Version) return Fail(InstallStep.Check, $"the copy is not version {request.Version}");

            if (File.Exists(exe))
            {
                if (File.Exists(previous)) SafeDelete.DeleteFile(previous);
                File.Move(exe, previous);
                replaced = true;
            }
            File.Move(copy, exe);
            placed = true;

            step = InstallStep.Entry;
            WriteEntry(registry, exe, request.Version, new FileInfo(exe).Length / 1024);
            // The earlier exe stays as .old until the installed copy has started (Undo puts it back; the installed copy's
            // start removes it, as after a self-update).
            return new InstallResult(exe, null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return Fail(step, ex.Message);
        }

        InstallResult Fail(InstallStep failed, string error)
        {
            TryDelete(copy);
            if (placed && !replaced) TryDelete(exe);
            if (replaced)
            {
                TryDelete(exe);
                try
                {
                    File.Move(previous, exe);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logging.Log.Error("install", $"the earlier installed exe could not be put back, it is {previous}", ex);
                }
            }
            // An earlier install keeps its entry; a fresh one leaves none behind.
            if (failed == InstallStep.Entry && !replaced) TryRemoveEntry(registry);
            if (createdFolder) TryDeleteFolder(folder);
            return new InstallResult(null, failed, error);
        }
    }

    /// <summary>
    /// After the installed copy has started: deletes the downloaded exe it came from, but only when that is still the same
    /// file (same SHA-256 as the installed exe, no link in its path). True when it is gone; false when it stays.
    /// </summary>
    /// <remarks>The downloaded copy's own process may hold its exe for a moment after it ended: tried for up to five seconds.</remarks>
    public static bool DeleteOldCopy(string oldExe, string installedExe)
    {
        if (string.Equals(Path.GetFullPath(oldExe), Path.GetFullPath(installedExe), StringComparison.OrdinalIgnoreCase)) return false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(oldExe)) return true;
                if (!SafeDelete.HasNoLinks(oldExe) || !SameContent(oldExe, installedExe)) return false;
                SafeDelete.DeleteFile(oldExe);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 20)
                {
                    Thread.Sleep(250);
                    continue;
                }
                Logging.Log.Warn("install", $"the downloaded copy {oldExe} was not removed: {ex.Message}");
                return false;
            }
        }
    }

    private static bool SameContent(string a, string b)
    {
        using var first = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var second = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read);
        return first.Length == second.Length &&
            System.Security.Cryptography.SHA256.HashData(first).AsSpan().SequenceEqual(System.Security.Cryptography.SHA256.HashData(second));
    }

    /// <summary>
    /// Undoes an install whose later steps failed in the app (the Start menu entry, starting the installed exe). An earlier
    /// install (its exe kept as .old) is put back with its entry; otherwise the entry, the exe and the then empty folder go.
    /// </summary>
    public static void Undo(string installedExe, IRegistryRoots registry)
    {
        var previous = installedExe + ".old";
        if (File.Exists(previous))
        {
            TryDelete(installedExe);
            try
            {
                File.Move(previous, installedExe);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logging.Log.Error("install", $"the earlier installed exe could not be put back, it is {previous}", ex);
            }
        }
        TryRemoveEntry(registry);
        TryDelete(installedExe);
        TryDeleteFolder(Path.GetDirectoryName(installedExe)!);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) SafeDelete.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("install", $"{path} not removed: {ex.Message}");
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) SafeDelete.DeleteEmptyDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logging.Log.Warn("install", $"{folder} not removed: {ex.Message}");
        }
    }

    private static void TryRemoveEntry(IRegistryRoots registry)
    {
        try
        {
            RemoveEntry(registry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Logging.Log.Warn("install", $"Installed apps entry not removed: {ex.Message}");
        }
    }
}

/// <summary>What <see cref="AppInstall.Install"/> copies where, and the checks it runs on the copy (faked in tests).</summary>
/// <param name="Verify">The copy is signed by Tunero's publisher (<see cref="AppInstall.IsTrustedFile"/>).</param>
/// <param name="FileVersion">The copy's product version.</param>
/// <param name="FolderIsAdminOnly">Only administrators can change the folder (<see cref="TrustedPath.IsAdminOnlyWritableFolder"/>).</param>
/// <param name="CopyIsAdminOnly">Only administrators can change the copied file and its folder (<see cref="TrustedPath.IsAdminOnlyBelowSharedFolders"/>).</param>
public sealed record InstallRequest(string RunningExe, string Folder, string Version, Func<string, bool> Verify, Func<string, string?> FileVersion,
    Func<string, bool> FolderIsAdminOnly, Func<string, bool> CopyIsAdminOnly);

public enum InstallStep { Folder, Copy, Check, Entry }

/// <summary>The installed exe, or the step that failed and why.</summary>
public sealed record InstallResult(string? InstalledExe, InstallStep? FailedAt, string? Error);
