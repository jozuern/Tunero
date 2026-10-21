using Optimizer.Core.Actions;
using Optimizer.Core.Apps;
using Optimizer.Core.Platform;

namespace Optimizer.Core.Tests;

/// <summary>
/// The one expander for paths read from the system (<see cref="PathExpander"/>): per-user strings expand for the signed-in
/// user whose profile is affected, never with this (elevated, maybe other) account's environment.
/// </summary>
public class PathExpanderTests
{
    private const string Profile = @"D:\Users\anna";

    private static readonly Dictionary<string, string> Machine = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["ProgramFiles"] = @"C:\Program Files",
        ["TEMP"] = @"C:\Windows\TEMP",
        ["TMP"] = @"C:\Windows\TEMP",
    };

    private static Dictionary<string, string> Vars(params (string Name, string Value)[] values) =>
        values.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheTargetUsersFoldersAndVariablesComeOnTopOfTheMachines()
    {
        var anna = PathExpander.ForUser(Profile, Vars(("OneDrive", @"%USERPROFILE%\OneDrive"), ("Path", @"%USERPROFILE%\bin")), Machine);
        Assert.Equal(@"D:\Users\anna\AppData\Local\Programs\App\app.exe", anna.Expand(@"%LOCALAPPDATA%\Programs\App\app.exe"));
        Assert.Equal(@"D:\Users\anna\AppData\Roaming\x", anna.Expand(@"%appdata%\x"));
        Assert.Equal(@"D:\Users\anna\AppData\Local\Temp\setup.exe", anna.Expand(@"%TEMP%\setup.exe"));
        Assert.Equal(@"D:\Users\anna\OneDrive\Documents", anna.Expand(@"%OneDrive%\Documents"));
        Assert.Equal(@"D:\Users\anna", anna.Expand("%HOMEDRIVE%%HOMEPATH%"));
        Assert.Equal(@"C:\Windows\System32\cmd.exe", anna.Expand(@"%SystemRoot%\System32\cmd.exe"));
        // Path is joined with the machine's by Windows, never a folder of its own.
        Assert.Equal("%Path%", anna.Expand("%Path%"));
        Assert.Equal(Profile, anna.ProfilePath);
    }

    [Fact]
    public void TheUsersOwnTempWinsOverTheProfileDefault()
    {
        var anna = PathExpander.ForUser(Profile, Vars(("TEMP", @"E:\Temp")), Machine);
        Assert.Equal(@"E:\Temp\x", anna.Expand(@"%TEMP%\x"));
        Assert.Equal(@"D:\Users\anna\AppData\Local\Temp\x", anna.Expand(@"%TMP%\x"));
    }

    /// <summary>
    /// A name the target user does not have stays as it is, also one this process has: the elevated process's environment
    /// is another account's when an administrator elevated Tunero with a separate account.
    /// </summary>
    [Fact]
    public void ThisProcesssEnvironmentIsNeverUsed()
    {
        const string name = "TUNERO_TEST_ONLY_IN_THIS_PROCESS";
        Environment.SetEnvironmentVariable(name, @"C:\Users\admin");
        try
        {
            var anna = PathExpander.ForUser(Profile, Vars(), Machine);
            Assert.Equal($@"%{name}%\x", anna.Expand($@"%{name}%\x"));
            Assert.Equal($@"%{name}%\x", PathExpander.Machine.Expand($@"%{name}%\x"));
            Assert.Equal($@"%{name}%\x", PathExpander.UnknownUser.Expand($@"%{name}%\x"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Without the user's profile their folders stay unexpanded, and TEMP is not the system's.</summary>
    [Fact]
    public void AnUnknownProfileLeavesTheUserFoldersAlone()
    {
        var unknown = PathExpander.ForUser(null, Vars(("TEMP", @"%USERPROFILE%\AppData\Local\Temp")), Machine);
        Assert.Equal(@"%LOCALAPPDATA%\App\app.exe", unknown.Expand(@"%LOCALAPPDATA%\App\app.exe"));
        Assert.Equal(@"%USERPROFILE%\AppData\Local\Temp\x", unknown.Expand(@"%TEMP%\x"));
        Assert.Equal(@"%TMP%\x", unknown.Expand(@"%TMP%\x"));
        Assert.Equal(@"C:\Program Files\App", unknown.Expand(@"%ProgramFiles%\App"));
        Assert.Null(unknown.ProfilePath);
    }

    [Fact]
    public void TheUsersVariablesAreReadFromTheirHive()
    {
        using var r = new SandboxRegistry();
        RegistryValue.Write(r, Hive.User, "Environment", "OneDrive", "expandString", @"%USERPROFILE%\OneDrive - Contoso");
        var anna = PathExpander.ForUser(Profile, r);
        Assert.Equal(@"D:\Users\anna\OneDrive - Contoso\x", anna.Expand(@"%OneDrive%\x"));
        Assert.Same(anna, anna.For(Hive.User));
        Assert.Same(PathExpander.Machine, anna.For(Hive.Machine));
    }

    /// <summary>
    /// A per-user uninstall entry runs as the signed-in user: its command is that user's file, not the one under this
    /// process's %LOCALAPPDATA%. An entry of the PC expands with the machine's variables only.
    /// </summary>
    [Fact]
    public void APerUserUninstallerIsTheSignedInUsersFile()
    {
        var anna = PathExpander.ForUser(Profile, Vars(), Machine);
        var discord = new DesktopProgram("Discord", null, null, null, null, @"""%LOCALAPPDATA%\Discord\Update.exe"" --uninstall", null, true, "k");
        var command = Programs.Command(discord, _ => true, _ => false, anna)!;
        Assert.Equal(UninstallMode.AsUser, command.Mode);
        Assert.Equal(@"D:\Users\anna\AppData\Local\Discord\Update.exe", command.File, ignoreCase: true);
        Assert.Equal("--uninstall", command.Arguments);

        // Without the user's expander the folder stays a variable: never this process's own %LOCALAPPDATA%.
        var own = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.DoesNotContain(own, Programs.Command(discord, _ => true, _ => false)?.File ?? "",StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeftoverFoldersOfAPerUserProgramAreInTheSignedInUsersProfile()
    {
        var anna = PathExpander.ForUser(Profile, Vars(), Machine);
        var app = new DesktopProgram("App", null, null, null, null, null, null, true, "k", InstallLocation: @"%LOCALAPPDATA%\Programs\App",
            DisplayIcon: @"""%LOCALAPPDATA%\Programs\App\app.exe"",0");
        Assert.Equal([@"D:\Users\anna\AppData\Local\Programs\App"], ProgramLeftovers.Folders(app, _ => false, anna));
        Assert.Equal(@"D:\Users\anna\AppData\Local\Programs\App", ProgramLeftovers.CleanPath(app.InstallLocation, anna, _ => false));
        // Unknown user: no full path, so nothing is offered from it.
        Assert.Empty(ProgramLeftovers.Folders(app, _ => false));
    }
}
