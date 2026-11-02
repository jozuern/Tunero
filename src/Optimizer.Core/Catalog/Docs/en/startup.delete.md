# Delete startup entry

## Summary
Deletes this autostart entry (a registry value, a file in a Startup folder or a task of another program), as Autoruns does. Tunero backs it up first, so the Changes page can put it back.

## How it works
Windows runs the command lines in the Run keys every time someone signs in, and the ones in the RunOnce keys once [1]. A Group Policy Run key [5] and the AutoRun value of the Command Processor, which cmd.exe runs each time it starts [4], work the same way, and so do the files in the Startup folders and the scheduled tasks other programs register [2]. Turning an entry off keeps it and marks it as disabled; Delete removes it, the way Autoruns does [2]. Before that, Tunero saves what it deletes: a registry value with its type, a file in a Startup folder with its content, a task as the XML Task Scheduler exports. Undo writes the value or the file back, or registers the task again from its XML under the same name and account [3]. A task that keeps a stored password is not offered, because it could only be registered again with that password [3].

## Why it can help
An entry whose program was uninstalled stays in the list and points to a file that is gone; deleting it tidies the list. RunOnce, policy Run and AutoRun entries cannot be turned off on this page, so Delete is the only way to stop them here.

## Evidence
Nothing measurable: an entry that points to a missing file costs almost nothing at sign-in. For entries that still run a program, the effect is the same as turning them off.

## Trade-offs & risks
The program no longer starts by itself, and it may register the entry or the task again the next time it runs or updates. A RunOnce entry an installer left for the next sign-in (finishing an update) does not run; restore it on the Changes page if the program then misbehaves. An updater task that is gone stops the program's automatic updates.

## When not to use it
For entries you only want to pause: turn them off instead, which keeps them in the list. For RunOnce entries of an installer you just ran: let it finish with a restart first. For the update task of a program you keep: turn it off rather than deleting it, or keep it.

## Sources
1. https://learn.microsoft.com/en-us/windows/win32/setupapi/run-and-runonce-registry-keys
2. https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns
3. https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertask
4. https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cmd
5. https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-logon
