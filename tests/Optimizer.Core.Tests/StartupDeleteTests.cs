using Optimizer.Core.Actions;
using Optimizer.Core.Startup;
using Optimizer.Core.Tweaks;

namespace Optimizer.Core.Tests;

/// <summary>Autoruns-style Delete for Startup folder files and tasks of other programs: backed up first, put back on undo.</summary>
public class StartupDeleteTests
{
    private static string TaskXml(string principal, string command = @"C:\Vendor\updater.exe", string? args = "/silent") => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="Author">{principal}</Principal></Principals>
          <Actions Context="Author"><Exec><Command>{command}</Command>{(args is null ? "" : $"<Arguments>{args}</Arguments>")}</Exec></Actions>
        </Task>
        """;

    private static readonly Facts Facts = new Facts().Set("os.build", 26300).Set("elevated", true);
    private static readonly ApplyOptions Options = new() { ContinueWithoutRestorePoint = true };

    [Fact]
    public void TaskPrincipalSaysHowItCanBeRegisteredAgain()
    {
        Assert.Equal((3, "S-1-5-21-1-2-3-1001"), ScheduledTaskXml.Principal(TaskXml("<UserId>S-1-5-21-1-2-3-1001</UserId><LogonType>InteractiveToken</LogonType>")));
        Assert.Equal((2, "S-1-5-21-1-2-3-1001"), ScheduledTaskXml.Principal(TaskXml("<UserId>S-1-5-21-1-2-3-1001</UserId><LogonType>S4U</LogonType>")));
        Assert.Equal((5, "S-1-5-18"), ScheduledTaskXml.Principal(TaskXml("<UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel>")));
        Assert.Equal((4, "S-1-5-32-545"), ScheduledTaskXml.Principal(TaskXml("<GroupId>S-1-5-32-545</GroupId>")));
        // A stored password cannot be exported, so such a task could not come back: never deleted.
        Assert.Null(ScheduledTaskXml.Principal(TaskXml("<UserId>PC\\admin</UserId><LogonType>Password</LogonType>")));
        Assert.Null(ScheduledTaskXml.Principal(TaskXml("<UserId>PC\\admin</UserId><LogonType>InteractiveTokenOrPassword</LogonType>")));
        Assert.Null(ScheduledTaskXml.Principal("not xml"));
        Assert.Equal("\"C:\\Vendor\\updater.exe\" /silent", ScheduledTaskXml.Command(TaskXml("<UserId>S-1-5-18</UserId>")));
    }

    [Fact]
    public async Task DeletedTaskIsRegisteredAgainOnUndo()
    {
        using var fx = new EngineFixture();
        const string path = @"\Vendor\Updater";
        var xml = TaskXml("<UserId>S-1-5-18</UserId>");
        fx.Tasks.Xml[path] = xml;
        var entry = new StartupEntry(StartupKind.LogonTask, @"Vendor\Updater", @"""C:\Vendor\updater.exe"" /silent", @"C:\Vendor\updater.exe", @"Task Scheduler\Vendor", Hive.Machine, true, "task:vendor")
        {
            Target = path,
        };
        var delete = StartupTweaks.Delete(entry)!;
        Assert.IsType<ScheduledTaskDeleteAction>(Assert.Single(delete.Actions));
        // The confirmation shows what the task runs, not its XML.
        var line = Assert.Single(fx.Engine.Preview(delete));
        Assert.Equal("\"C:\\Vendor\\updater.exe\" /silent", line.Before);

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(delete, Facts, Options)).Outcome);
        Assert.False(fx.Tasks.Xml.ContainsKey(path));

        // After a restart of the app the stored definition still undoes it, with the exported XML.
        var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
        Assert.True(engine.Revert(engine.Resolve(delete.Id)!).Success);
        Assert.Equal(xml, fx.Tasks.Xml[path]);

        // A task with a stored password is not offered for a delete that could not be undone.
        fx.Tasks.Xml[path] = TaskXml("<UserId>PC\\admin</UserId><LogonType>Password</LogonType>");
        Assert.NotEqual(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(StartupTweaks.Delete(entry)!, Facts, Options)).Outcome);
        Assert.True(fx.Tasks.Xml.ContainsKey(path));
    }

    /// <summary>
    /// A deleted task its program registered again (a new version, a new path) is not "reset by Windows": nothing deletes
    /// it on its own, and deleting it again keeps the new XML, so undo brings back the program's current task.
    /// </summary>
    [Fact]
    public async Task ATaskPutBackByItsProgramIsNotDriftAndItsNewXmlIsKept()
    {
        using var fx = new EngineFixture();
        const string path = @"\Vendor\Updater";
        fx.Tasks.Xml[path] = TaskXml("<UserId>S-1-5-18</UserId>");
        var entry = new StartupEntry(StartupKind.LogonTask, @"Vendor\Updater", null, null, @"Task Scheduler\Vendor", Hive.Machine, true, "task:vendor") { Target = path };
        var delete = StartupTweaks.Delete(entry)!;
        await fx.Engine.ApplyAsync(delete, Facts, Options);

        var v2 = TaskXml("<UserId>S-1-5-18</UserId>", @"C:\Vendor\v2\updater.exe");
        fx.Tasks.Xml[path] = v2;
        Assert.Empty(fx.Engine.CheckDrift(Facts));

        Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(delete, Facts, Options)).Outcome);
        Assert.True(fx.Engine.Revert(delete).Success);
        Assert.Equal(v2, fx.Tasks.Xml[path]);
    }

    /// <summary>The undo confirmation and the change log name the file's size and the task's program, never the stored bytes or XML.</summary>
    [Fact]
    public void StoredFilesAndTasksDisplayShort()
    {
        Assert.Equal("@file:8", new StoredValue(true, "file", Convert.ToBase64String(new byte[8])).Display);
        Assert.Equal("@file:7", new StoredValue(true, "file", Convert.ToBase64String(new byte[7])).Display);
        Assert.Equal("file of 1,400 bytes", Docs.Labels.Current.Display("en", "@file:1400"));
        Assert.Equal("Datei mit 1.400 Byte", Docs.Labels.Current.Display("de", "@file:1400"));
        Assert.Equal("\"C:\\Vendor\\updater.exe\" /silent", new StoredValue(true, "taskXml", TaskXml("<UserId>S-1-5-18</UserId>")).Display);
        Assert.Equal("@task", new StoredValue(true, "taskXml", "not xml").Display);
    }

    [Fact]
    public async Task DeletedStartupFileIsWrittenBackOnUndo()
    {
        using var fx = new EngineFixture();
        var profile = TestFolders.Create("profile");
        try
        {
            var folder = Path.Combine(profile, @"AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "Helper.lnk");
            byte[] bytes = [0x4C, 0, 0, 0, 1, 2, 3, 250];
            await File.WriteAllBytesAsync(file, bytes);

            var entry = Assert.Single(new StartupScanner(fx.Registry, fx.Tasks, profile) { CommonStartupFolder = Path.Combine(profile, "no-startup") }.StartupFolders());
            var delete = StartupTweaks.Delete(entry)!;
            Assert.Equal(file, Assert.IsType<StartupFileDeleteAction>(Assert.Single(delete.Actions)).Path, ignoreCase: true);
            Assert.Equal(ApplyOutcome.Applied, (await fx.Engine.ApplyAsync(delete, Facts, Options)).Outcome);
            Assert.False(File.Exists(file));

            var engine = new TweakEngine(fx.Context, new Backup.BackupStore(fx.BackupRoot, secure: false), fx.RestorePoints, "test", 26300);
            Assert.True(engine.Revert(engine.Resolve(delete.Id)!).Success);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
        }
        finally
        {
            TestFolders.Delete(profile);
        }
    }

    [Fact]
    public void OnlyFilesDirectlyInAStartupFolder()
    {
        Assert.True(StartupFileDeleteAction.IsStartupFile(@"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.lnk"));
        Assert.True(StartupFileDeleteAction.IsStartupFile(@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp\x.lnk"));
        Assert.False(StartupFileDeleteAction.IsStartupFile(@"C:\Windows\System32\drivers\etc\hosts"));
        Assert.False(StartupFileDeleteAction.IsStartupFile(@"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\..\..\x.lnk"));
        Assert.False(StartupFileDeleteAction.IsStartupFile(@"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\desktop.ini"));
        Assert.False(StartupFileDeleteAction.IsStartupFile(@"Startup\x.lnk"));
        // The action refuses such a path also when a backup was edited to name it.
        var action = new StartupFileDeleteAction { Path = @"C:\Windows\System32\drivers\etc\hosts" };
        Assert.Throws<InvalidOperationException>(() => action.Restore(null!, new StoredValue(true, "file", "AA==")));
    }
}
