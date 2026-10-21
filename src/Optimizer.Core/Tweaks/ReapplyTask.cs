using System.Security;
using Optimizer.Core.Actions;

namespace Optimizer.Core.Tweaks;

/// <summary>
/// The opt-in task that applies the user's changes again after a Windows update reset them: two
/// minutes after the user signs in, Task Scheduler starts this exe with <see cref="Argument"/> as that user with
/// administrator rights, and it applies only the reset changes the Overview's "Apply again" offers. No service and no
/// process stays running: the run ends when it is done. It is a change like any other (<see cref="OwnTaskAction"/>),
/// listed on the Changes page; undo removes the task.
/// </summary>
public static class ReapplyTask
{
    public const string TweakId = "app.reapplyTask";
    /// <summary>One task per account, named after its SID as Windows names per-user tasks: each administrator turns their own on and off.</summary>
    public static string TaskPath(string userSid) => $@"\{TaskPrefix}{userSid}";

    private const string TaskPrefix = @"Tunero\Apply changes again ";

    /// <summary>A scheduled task's path (with or without the leading backslash) is one of these tasks.</summary>
    public static bool IsOwnTask(string taskPath) => taskPath.TrimStart('\\').StartsWith(TaskPrefix, StringComparison.OrdinalIgnoreCase);

    public const string Argument = "--reapply";

    /// <summary>
    /// The task runs the exe elevated without asking, so the exe must be one only administrators can change (Program
    /// Files, a machine-wide winget or Chocolatey install): one in Downloads could be swapped by any program of the
    /// user. <paramref name="isAdminOnly"/> checks the path and the folders above it.
    /// </summary>
    public static bool CanRegister(string? exe, Func<string, bool> isAdminOnly) => exe is { Length: > 0 } && isAdminOnly(exe);

    public static string Xml(string exe, string userSid) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>Tunero</Author>
            <Description>Applies your Tunero changes again that a Windows update has reset, two minutes after you sign in. Turn it off in Tunero's Settings or undo it on the Changes page.</Description>
            <URI>{SecurityElement.Escape(TaskPath(userSid))}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <Delay>PT2M</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(userSid)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <ExecutionTimeLimit>PT30M</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(exe)}</Command>
              <Arguments>{Argument}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    public static TweakDefinition Definition(string exe, string userSid) => new()
    {
        Id = TweakId,
        Docs = TweakId,
        Category = "App",
        Impact = new ImpactInfo { Gaming = 0, Basis = "situational", Effect = ["none"] },
        Risk = Risk.Safe,
        Hidden = true,
        Actions = [new OwnTaskAction { Path = TaskPath(userSid), Xml = Xml(exe, userSid) }],
        Sources = ["https://learn.microsoft.com/en-us/windows/win32/taskschd/logon-trigger-example--xml-"],
    };
}
