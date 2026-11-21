using System.IO;
using System.ServiceProcess;
using System.Text.Json;

namespace ServiceManager;

public sealed class ServiceRow
{
    public string Server { get; init; } = "";
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Status { get; init; } = "";
    public string StartType { get; init; } = "";
    public string Description { get; init; } = "";
    public string LogOnAs { get; init; } = "";
    public bool CanStop { get; init; }
    public bool CanPause { get; init; }
}

public static class ServerStore
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ServiceManager", "servers.json");

    public static List<string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath));
                if (list is { Count: > 0 })
                {
                    return list;
                }
            }
        }
        catch
        {
        }
        return new List<string> { "." };
    }

    public static void Save(IEnumerable<string> servers)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(servers));
        }
        catch
        {
        }
    }
}

public static class ServiceOps
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    public static string Display(string server) => server == "." ? "Local" : server;

    /// <summary>Lists every service on one server. Throws if the server is unreachable or access is denied.</summary>
    public static List<ServiceRow> List(string server)
    {
        var rows = new List<ServiceRow>();
        using var scm = new ServiceNative.Session(server);
        foreach (var sc in ServiceController.GetServices(server))
        {
            using (sc)
            {
                var cfg = new ServiceConfig();
                try
                {
                    cfg = scm.Read(sc.ServiceName, false);
                }
                catch
                {
                    cfg.StartMode = "?";
                }
                rows.Add(new ServiceRow
                {
                    Server = server,
                    Name = sc.ServiceName,
                    DisplayName = sc.DisplayName,
                    Status = sc.Status.ToString(),
                    StartType = StartModeText(cfg.StartMode),
                    Description = cfg.Description,
                    LogOnAs = cfg.Account,
                    CanStop = sc.CanStop,
                    CanPause = sc.CanPauseAndContinue
                });
            }
        }
        return rows;
    }

    public static string StartModeText(string mode) => mode switch
    {
        "auto" => "Automatic",
        "delayed" => "Automatic (Delayed Start)",
        "demand" => "Manual",
        "disabled" => "Disabled",
        "boot" => "Boot",
        "system" => "System",
        _ => "?"
    };

    public static ServiceConfig ReadConfig(string server, string name)
    {
        using var scm = new ServiceNative.Session(server);
        return scm.Read(name, true);
    }

    public static void SaveConfig(string server, ServiceConfig cfg, string? password, bool changeAccount, bool changeStartMode, bool changeRecovery, string? originalDescription)
    {
        using var scm = new ServiceNative.Session(server);
        scm.Write(cfg, password, changeAccount, changeStartMode, changeRecovery, originalDescription);
    }

    public static (List<string> DependsOn, List<string> Dependents) Dependencies(string server, string name)
    {
        using var sc = new ServiceController(name, server);
        var on = sc.ServicesDependedOn.Select(s => s.DisplayName + " (" + s.ServiceName + ")").OrderBy(s => s).ToList();
        var by = sc.DependentServices.Select(s => s.DisplayName + " (" + s.ServiceName + ")").OrderBy(s => s).ToList();
        return (on, by);
    }

    public static string Status(string server, string name)
    {
        using var sc = new ServiceController(name, server);
        return sc.Status.ToString();
    }

    public static void Start(ServiceRow r, string[]? args)
    {
        using var sc = new ServiceController(r.Name, r.Server);
        if (sc.Status == ServiceControllerStatus.Running)
        {
            return;
        }
        if (args is { Length: > 0 })
        {
            sc.Start(args);
        }
        else
        {
            sc.Start();
        }
        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
    }

    public static void Start(ServiceRow r) => Start(r, null);

    public static void Stop(ServiceRow r)
    {
        using var sc = new ServiceController(r.Name, r.Server);
        if (sc.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
    }

    public static void Restart(ServiceRow r)
    {
        Stop(r);
        Start(r);
    }

    public static void Pause(ServiceRow r)
    {
        using var sc = new ServiceController(r.Name, r.Server);
        sc.Pause();
        sc.WaitForStatus(ServiceControllerStatus.Paused, Wait);
    }

    public static void Resume(ServiceRow r)
    {
        using var sc = new ServiceController(r.Name, r.Server);
        sc.Continue();
        sc.WaitForStatus(ServiceControllerStatus.Running, Wait);
    }

    /// <summary>mode: auto, delayed, demand or disabled.</summary>
    public static void SetStartType(ServiceRow r, string mode)
    {
        var cfg = ReadConfig(r.Server, r.Name);
        cfg.StartMode = mode;
        SaveConfig(r.Server, cfg, null, false, true, false, cfg.Description);
    }
}
