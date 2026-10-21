using System.IO;
using System.Text.Json;

namespace Optimizer.App.Services;

/// <summary>UI preferences in %ProgramData%\Tunero\settings.json (app-owned data; elevated profiles differ per account).</summary>
public sealed class AppSettings
{
    public string? Language { get; set; }
    public string Theme { get; set; } = "System";

    /// <summary>Shows Expert tweaks (security trade-offs, boot configuration). Off by default.</summary>
    public bool ExpertMode { get; set; }

    /// <summary>Opt-in: ask GitHub for the latest release at start. Off by default (no network request unless turned on).</summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>
    /// Version ("0.8.4") whose update bar a copy installed by a package manager has shown at start: that copy gets it once,
    /// because winget and Chocolatey offer a version only after their review, which can take days.
    /// </summary>
    public string? UpdateNoticeShownFor { get; set; }

    /// <summary>The first start asked whether to add the app to the Start menu (asked once, whatever the answer).</summary>
    public bool StartMenuAsked { get; set; }

    /// <summary>The first start's setup was answered with Run from here: it is not shown again (Settings still offers Install).</summary>
    public bool SetupDeclined { get; set; }

    /// <summary>VirusTotal API key, DPAPI-protected for the current user (never stored in clear text).</summary>
    public string? VirusTotalKey { get; set; }

    /// <summary>Windows version seen at the last scan ("26300.9550"), to notice updates.</summary>
    public string? LastSeenWindowsVersion { get; set; }

    /// <summary>Paths, registry locations and package names in the lists (Startup, Cleanup, Services, Remove apps). On by default.</summary>
    public bool TechnicalDetails { get; set; } = true;

    /// <summary>The overview's note on how the app works (reads only, asks first, can undo) was dismissed.</summary>
    public bool IntroSeen { get; set; }

    /// <summary>Scan results the user ignores (<see cref="Optimizer.Core.Findings.IgnoredResults.Key"/>): left out of the lists, the score and the facts.</summary>
    public List<string> IgnoredFindings { get; set; } = [];

    /// <summary>Tweak ids whose recommendation the user ignores (<see cref="Optimizer.Core.Profiles.ProfiledTweak.Ignored"/>): never recommended or flagged.</summary>
    public List<string> IgnoredTweaks { get; set; } = [];

    /// <summary>The opt-in sections of an export on the Changes page (removed apps, service start types, startup entries turned off).</summary>
    public bool ExportApps { get; set; }
    public bool ExportServices { get; set; }
    public bool ExportStartup { get; set; }

    /// <summary>Usage profile id ("gaming", "battery", ...); null until the first scan suggests one.</summary>
    public string? Profile { get; set; }

    private static string FilePath =>
        Optimizer.Core.Platform.DataPaths.Settings;

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex)
        {
            Optimizer.Core.Logging.Log.Warn("settings", $"settings not readable, defaults used (the file is kept as settings.json.bad): {ex.Message}");
            try
            {
                if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bad", overwrite: true);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                Optimizer.Core.Logging.Log.Warn("settings", $"settings.json.bad not written: {copy.Message}");
            }
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // A crash or power loss while saving never leaves half a file.
            Optimizer.Core.Platform.DurableFile.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Preferences are a convenience; failing to save them must not break the app, but the log says why.
            Optimizer.Core.Logging.Log.Warn("settings", $"settings not saved: {ex.Message}");
        }
    }
}
