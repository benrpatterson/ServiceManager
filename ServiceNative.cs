using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ServiceManager;

public enum RecoveryType
{
    None = 0,
    Restart = 1,
    Reboot = 2,
    RunCommand = 3
}

public sealed class RecoveryAction
{
    public RecoveryType Type { get; set; }
    public uint DelayMs { get; set; }
}

public sealed class ServiceConfig
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string BinaryPath { get; set; } = "";

    /// <summary>auto, delayed, demand or disabled (boot/system are shown as "other" and left alone).</summary>
    public string StartMode { get; set; } = "demand";

    public uint ServiceType { get; set; }
    public string Account { get; set; } = "LocalSystem";
    public bool Interactive { get; set; }
    public uint ResetPeriodSeconds { get; set; }
    public string RebootMessage { get; set; } = "";
    public string RecoveryCommand { get; set; } = "";
    public List<RecoveryAction> Recovery { get; set; } = new();
}

/// <summary>Thin wrapper over the Service Control Manager. Works against the local machine or a remote one.</summary>
public static class ServiceNative
{
    private const uint SC_MANAGER_CONNECT = 0x1;
    private const uint SERVICE_QUERY_CONFIG = 0x1;
    private const uint SERVICE_CHANGE_CONFIG = 0x2;
    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_INTERACTIVE_PROCESS = 0x100;
    private const uint INFO_DESCRIPTION = 1;
    private const uint INFO_FAILURE_ACTIONS = 2;
    private const uint INFO_DELAYED_AUTO = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct QUERY_SERVICE_CONFIG
    {
        public uint dwServiceType;
        public uint dwStartType;
        public uint dwErrorControl;
        public IntPtr lpBinaryPathName;
        public IntPtr lpLoadOrderGroup;
        public uint dwTagId;
        public IntPtr lpDependencies;
        public IntPtr lpServiceStartName;
        public IntPtr lpDisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_FAILURE_ACTIONS
    {
        public uint dwResetPeriod;
        public IntPtr lpRebootMsg;
        public IntPtr lpCommand;
        public uint cActions;
        public IntPtr lpsaActions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SC_ACTION
    {
        public int Type;
        public uint Delay;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint level, IntPtr buffer, uint size, out uint needed);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint type, uint start, uint errorControl, string? binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint level, IntPtr info);

    private static string? Machine(string server) => server == "." || server.Length == 0 ? null : server;

    private static void Fail(string what)
    {
        throw new Win32Exception(Marshal.GetLastWin32Error(), what);
    }

    /// <summary>One connection to a server's Service Control Manager, reusable for many services.</summary>
    public sealed class Session : IDisposable
    {
        private readonly IntPtr _scm;

        public Session(string server)
        {
            _scm = OpenSCManager(Machine(server), null, SC_MANAGER_CONNECT);
            if (_scm == IntPtr.Zero)
            {
                Fail("Cannot connect to " + server);
            }
        }

        public IntPtr Open(string name, uint access)
        {
            var h = OpenService(_scm, name, access);
            if (h == IntPtr.Zero)
            {
                Fail("Cannot open service " + name);
            }
            return h;
        }

        public void Dispose() => CloseServiceHandle(_scm);

        /// <summary>Reads everything Properties shows. Pieces that can't be read are left at their defaults.</summary>
        public ServiceConfig Read(string name, bool recovery)
        {
            var h = Open(name, SERVICE_QUERY_CONFIG);
            try
            {
                var cfg = new ServiceConfig { Name = name };
                ReadBasic(h, cfg);
                ReadDescription(h, cfg);
                if (recovery)
                {
                    ReadFailure(h, cfg);
                }
                return cfg;
            }
            finally
            {
                CloseServiceHandle(h);
            }
        }

        public void Write(ServiceConfig cfg, string? password, bool changeAccount, bool changeStartMode, bool changeRecovery, string? originalDescription)
        {
            var h = Open(cfg.Name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            try
            {
                uint start = SERVICE_NO_CHANGE;
                if (changeStartMode)
                {
                    start = cfg.StartMode switch
                    {
                        "auto" or "delayed" => 2,
                        "disabled" => 4,
                        _ => 3
                    };
                }
                uint type = SERVICE_NO_CHANGE;
                if (changeAccount)
                {
                    type = cfg.ServiceType & ~SERVICE_INTERACTIVE_PROCESS;
                    if (cfg.Interactive)
                    {
                        type |= SERVICE_INTERACTIVE_PROCESS;
                    }
                }
                if (!ChangeServiceConfig(h, type, start, SERVICE_NO_CHANGE, null, null, IntPtr.Zero, null,
                        changeAccount ? cfg.Account : null, changeAccount ? password ?? "" : null, cfg.DisplayName))
                {
                    Fail("Cannot change the service configuration");
                }
                if (changeStartMode && start == 2)
                {
                    SetDelayed(h, cfg.StartMode == "delayed");
                }
                if (cfg.Description != originalDescription)
                {
                    var p = Marshal.StringToHGlobalUni(cfg.Description);
                    var info = Marshal.AllocHGlobal(IntPtr.Size);
                    try
                    {
                        Marshal.WriteIntPtr(info, p);
                        if (!ChangeServiceConfig2(h, INFO_DESCRIPTION, info))
                        {
                            Fail("Cannot change the description");
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(info);
                        Marshal.FreeHGlobal(p);
                    }
                }
                if (changeRecovery)
                {
                    WriteFailure(h, cfg);
                }
            }
            finally
            {
                CloseServiceHandle(h);
            }
        }

        private static void SetDelayed(IntPtr h, bool delayed)
        {
            var info = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(info, delayed ? 1 : 0);
                if (!ChangeServiceConfig2(h, INFO_DELAYED_AUTO, info))
                {
                    Fail("Cannot change delayed start");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(info);
            }
        }

        private static void WriteFailure(IntPtr h, ServiceConfig cfg)
        {
            var actions = cfg.Recovery;
            var strings = new List<IntPtr>();
            var arr = Marshal.AllocHGlobal(Math.Max(1, actions.Count) * Marshal.SizeOf<SC_ACTION>());
            var info = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_FAILURE_ACTIONS>());
            try
            {
                for (var i = 0; i < actions.Count; i++)
                {
                    Marshal.StructureToPtr(new SC_ACTION { Type = (int)actions[i].Type, Delay = actions[i].DelayMs }, arr + i * Marshal.SizeOf<SC_ACTION>(), false);
                }
                var cmd = Marshal.StringToHGlobalUni(cfg.RecoveryCommand);
                strings.Add(cmd);
                var msg = IntPtr.Zero;
                if (cfg.RebootMessage.Length > 0)
                {
                    msg = Marshal.StringToHGlobalUni(cfg.RebootMessage);
                    strings.Add(msg);
                }
                Marshal.StructureToPtr(new SERVICE_FAILURE_ACTIONS
                {
                    dwResetPeriod = cfg.ResetPeriodSeconds,
                    lpRebootMsg = msg,
                    lpCommand = cmd,
                    cActions = (uint)actions.Count,
                    lpsaActions = arr
                }, info, false);
                if (!ChangeServiceConfig2(h, INFO_FAILURE_ACTIONS, info))
                {
                    Fail("Cannot change the recovery actions");
                }
            }
            finally
            {
                foreach (var s in strings)
                {
                    Marshal.FreeHGlobal(s);
                }
                Marshal.FreeHGlobal(arr);
                Marshal.FreeHGlobal(info);
            }
        }

        private static void ReadBasic(IntPtr h, ServiceConfig cfg)
        {
            QueryServiceConfig(h, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
            {
                Fail("Cannot read the service configuration");
            }
            var buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfig(h, buf, needed, out _))
                {
                    Fail("Cannot read the service configuration");
                }
                var c = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buf);
                cfg.ServiceType = c.dwServiceType;
                cfg.Interactive = (c.dwServiceType & SERVICE_INTERACTIVE_PROCESS) != 0;
                cfg.BinaryPath = Marshal.PtrToStringUni(c.lpBinaryPathName) ?? "";
                cfg.DisplayName = Marshal.PtrToStringUni(c.lpDisplayName) ?? "";
                cfg.Account = Marshal.PtrToStringUni(c.lpServiceStartName) ?? "";
                cfg.StartMode = c.dwStartType switch
                {
                    2 => "auto",
                    3 => "demand",
                    4 => "disabled",
                    0 => "boot",
                    _ => "system"
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            if (cfg.StartMode == "auto" && Query2(h, INFO_DELAYED_AUTO, out var d))
            {
                try
                {
                    if (Marshal.ReadInt32(d) != 0)
                    {
                        cfg.StartMode = "delayed";
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(d);
                }
            }
        }

        private static void ReadDescription(IntPtr h, ServiceConfig cfg)
        {
            if (!Query2(h, INFO_DESCRIPTION, out var buf))
            {
                return;
            }
            try
            {
                cfg.Description = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf)) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private static void ReadFailure(IntPtr h, ServiceConfig cfg)
        {
            if (!Query2(h, INFO_FAILURE_ACTIONS, out var buf))
            {
                return;
            }
            try
            {
                var f = Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS>(buf);
                cfg.ResetPeriodSeconds = f.dwResetPeriod;
                cfg.RebootMessage = Marshal.PtrToStringUni(f.lpRebootMsg) ?? "";
                cfg.RecoveryCommand = Marshal.PtrToStringUni(f.lpCommand) ?? "";
                for (var i = 0; i < f.cActions; i++)
                {
                    var a = Marshal.PtrToStructure<SC_ACTION>(f.lpsaActions + i * Marshal.SizeOf<SC_ACTION>());
                    cfg.Recovery.Add(new RecoveryAction { Type = (RecoveryType)a.Type, DelayMs = a.Delay });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Caller frees the returned buffer.</summary>
        private static bool Query2(IntPtr h, uint level, out IntPtr buffer)
        {
            QueryServiceConfig2(h, level, IntPtr.Zero, 0, out var needed);
            buffer = IntPtr.Zero;
            if (needed == 0)
            {
                return false;
            }
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfig2(h, level, buffer, needed, out _))
            {
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                return false;
            }
            return true;
        }
    }
}
