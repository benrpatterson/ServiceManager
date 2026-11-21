# Service Manager

A services.msc-style Windows app that manages services on many servers at once.

- Add any number of servers (computer name or IP); `.` is the local machine. The list is saved in `%AppData%\ServiceManager\servers.json`.
- Start, stop, restart, pause and resume, across servers, on a multi-selection.
- Properties dialog (double-click): General (display name, description, startup type incl. delayed start, start parameters), Log On, Recovery, Dependencies.
- Filter by text or state; export the list to CSV or TSV.

Remote servers use your current Windows login and need admin rights there, plus RPC access to the Service Control Manager through the firewall.

```powershell
dotnet run -c Release
```
