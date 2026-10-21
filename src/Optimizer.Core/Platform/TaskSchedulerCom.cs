using System.Runtime.InteropServices;

namespace Optimizer.Core.Platform;

/// <summary>
/// Task Scheduler 2.0 early-bound (taskschd.dll). Declared from the type library: every interface is dual, so the
/// runtime puts IDispatch's four methods first and the members follow in vtable order (slot 7 on). Only the members the
/// app calls and the ones before them are declared; placeholders keep the slots of those it never calls. The late-bound
/// calls these replace compiled a binder on first use, about 400 ms of the startup scan.
/// </summary>
internal static class TaskSchedulerCom
{
    // taskschd.h
    public const int EnumHidden = 1, CreateOrUpdate = 6, LogonInteractiveToken = 3, RunLevelLua = 0;
    public const int TriggerBoot = 8, TriggerLogon = 9, ActionExec = 0;

    // TASK_STATE
    public const int StateQueued = 2, StateReady = 3, StateRunning = 4;

    /// <summary>A connection to the local Task Scheduler.</summary>
    public static ITaskService Connect()
    {
        var service = (ITaskService)new TaskScheduler();
        service.Connect(null, null, null, null);
        return service;
    }

    [ComImport, Guid("0F87369F-A4E5-4CFC-BD3E-73E6154572DD")]
    private class TaskScheduler;

    [ComImport, Guid("2FABA4C7-4DA9-4013-9697-20CC3FD40F85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITaskService
    {
        ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetRunningTasks(int flags);
        ITaskDefinition NewTask(uint flags);
        void Connect(object? serverName, object? user, object? domain, object? password);
    }

    [ComImport, Guid("8CFAC062-A080-4C15-9A88-AA7C2AF80DFC"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITaskFolder
    {
        string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
        ITaskFolder GetFolder([MarshalAs(UnmanagedType.BStr)] string path);
        ITaskFolderCollection GetFolders(int flags);
        ITaskFolder CreateFolder([MarshalAs(UnmanagedType.BStr)] string name, object? sddl);
        void DeleteFolder([MarshalAs(UnmanagedType.BStr)] string name, int flags);
        IRegisteredTask GetTask([MarshalAs(UnmanagedType.BStr)] string path);
        IRegisteredTaskCollection GetTasks(int flags);
        void DeleteTask([MarshalAs(UnmanagedType.BStr)] string name, int flags);
        IRegisteredTask RegisterTask([MarshalAs(UnmanagedType.BStr)] string path, [MarshalAs(UnmanagedType.BStr)] string xml, int flags,
            object? userId, object? password, int logonType, object? sddl);
        IRegisteredTask RegisterTaskDefinition([MarshalAs(UnmanagedType.BStr)] string path, ITaskDefinition definition, int flags,
            object? userId, object? password, int logonType, object? sddl);
    }

    /// <summary>1-based.</summary>
    [ComImport, Guid("79184A66-8664-423F-97F1-637356A5D812"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITaskFolderCollection
    {
        int Count { get; }
        ITaskFolder Item(object index);
    }

    /// <summary>1-based.</summary>
    [ComImport, Guid("86627EB4-42A7-41E4-A4D9-AC33A72F2D52"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRegisteredTaskCollection
    {
        int Count { get; }
        IRegisteredTask Item(object index);
    }

    [ComImport, Guid("9C86F320-DEE3-4DD1-B972-A303F26B061E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRegisteredTask
    {
        string Name { [return: MarshalAs(UnmanagedType.BStr)] get; }
        string Path { [return: MarshalAs(UnmanagedType.BStr)] get; }
        int State { get; }
        bool Enabled { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        [return: MarshalAs(UnmanagedType.IUnknown)] object Run(object? parameters);
        [return: MarshalAs(UnmanagedType.IUnknown)] object RunEx(object? parameters, int flags, int sessionId, [MarshalAs(UnmanagedType.BStr)] string user);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetInstances(int flags);

        /// <summary>An OLE automation date; 1899-12-30 (0) when the task never ran.</summary>
        double LastRunTime { get; }
        int LastTaskResult { get; }
        int NumberOfMissedRuns { get; }
        double NextRunTime { get; }
        ITaskDefinition Definition { get; }
        string Xml { [return: MarshalAs(UnmanagedType.BStr)] get; }
        void GetSecurityDescriptor();
        void SetSecurityDescriptor();
        void Stop(int flags);
    }

    [ComImport, Guid("F5BC8FC5-536D-4F77-B852-FBC1356FDEB6"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITaskDefinition
    {
        IRegistrationInfo RegistrationInfo { get; set; }
        ITriggerCollection Triggers { get; set; }
        ITaskSettings Settings { get; set; }
        string Data { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        IPrincipal Principal { get; set; }
        IActionCollection Actions { get; set; }
    }

    [ComImport, Guid("416D8B73-CB41-4EA1-805C-9BE9A5AC4A74"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IRegistrationInfo
    {
        string? Description { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        string? Author { [return: MarshalAs(UnmanagedType.BStr)] get; }
    }

    /// <summary>1-based.</summary>
    [ComImport, Guid("85DF5081-1B24-4F32-878A-D9D14DF4CB77"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITriggerCollection
    {
        int Count { get; }
        ITrigger Item(int index);
    }

    [ComImport, Guid("09941815-EA89-4B5B-89E0-2A773801FAC3"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITrigger
    {
        int Type { get; }
    }

    /// <summary>1-based.</summary>
    [ComImport, Guid("02820E19-7B98-4ED2-B2E8-FDCCCEFF619B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IActionCollection
    {
        int Count { get; }
        IAction Item(int index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        string XmlText { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        IAction Create(int type);
    }

    [ComImport, Guid("BAE54997-48B1-4CBE-9965-D6BE263EBEA4"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IAction
    {
        string? Id { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        int Type { get; }
    }

    /// <summary>An action of type <see cref="ActionExec"/>; IAction's members come first.</summary>
    [ComImport, Guid("4C3D624D-FD6B-49A3-B9B7-09CB3CD3F047"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IExecAction
    {
        string? Id { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        int Type { get; }
        string? Path { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        string? Arguments { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    }

    [ComImport, Guid("D98D51E5-C9B4-496A-A9C1-18980261CF0F"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface IPrincipal
    {
        string? Id { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        string? DisplayName { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        string? UserId { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        int LogonType { get; set; }
        string? GroupId { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        int RunLevel { get; set; }
    }

    [ComImport, Guid("8FD4711D-2D02-4C8C-87E3-EFF699DE127E"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    public interface ITaskSettings
    {
        bool AllowDemandStart { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        string? RestartInterval { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        int RestartCount { get; set; }
        int MultipleInstances { get; set; }
        bool StopIfGoingOnBatteries { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        bool DisallowStartIfOnBatteries { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        bool AllowHardTerminate { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        bool StartWhenAvailable { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        string? XmlText { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
        bool RunOnlyIfNetworkAvailable { [return: MarshalAs(UnmanagedType.VariantBool)] get; [param: MarshalAs(UnmanagedType.VariantBool)] set; }
        string? ExecutionTimeLimit { [return: MarshalAs(UnmanagedType.BStr)] get; [param: MarshalAs(UnmanagedType.BStr)] set; }
    }
}
