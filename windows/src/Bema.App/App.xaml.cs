using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Interop;
using Bema.App.Model;
using Bema.App.Platform;
using Bema.App.Speech;
using Bema.App.Views;
using Bema.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Bema.App;

/// <summary>
/// Bema for Windows. Runs in the notification area (the Mac app's menu bar icon) and keeps running
/// when its window closes, so Ctrl+Alt+P and links always find it. One Bema at a time: a second one
/// hands its deck or link to the first and quits.
/// </summary>
public partial class BemaApp : Application
{
    const string InstanceName = "Bema.SingleInstance";
    Mutex? instanceMutex;
    Forms.NotifyIcon? tray;
    MainWindow? main;
    TrayPanel? trayPanel;
    PrompterWindow? prompter;
    HwndSource? hotkeySource;
    const int HotkeyId = 0xB3A;

    public static BemaApp Instance => (BemaApp)Current;
    public PollModel Poll { get; } = new();
    public RemoteRouter Router { get; private set; } = null!;
    public RemoteServer Server { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Started by Claude Desktop to reach Bema: relay and exit, no window.
        if (e.Args.Length > 0 && e.Args[0] == "--mcp")
        {
            var code = McpBridge.Run();
            Shutdown(code);
            return;
        }
        instanceMutex = new Mutex(true, Pipes.ForUser(InstanceName), out var first);
        if (!first)
        {
            ForwardToRunningBema(e.Args);
            Shutdown();
            return;
        }
        base.OnStartup(e);
        Theme.Apply();
        DispatcherUnhandledException += (_, ex) =>
        {
            Presenter.Shared.Notice = $"Something went wrong: {ex.Exception.Message}";
            ex.Handled = true;
        };

        Router = new RemoteRouter(Presenter.Shared, Poll);
        Server = new RemoteServer(Router, work => Dispatcher.InvokeAsync(work).Task);

        Presenter.Shared.ShowPresenterWindow = ShowMainWindow;
        CoachRuntime.Shared.ReportReady += report => new ReportWindow(report) { Owner = main }.Show();
        RegisterLinkScheme();
        StartTray();
        ListenForOtherInstances();
        ShowMainWindow();
        RegisterHotkey();
        McpServer.Shared.StartIfEnabled();
        foreach (var arg in e.Args) _ = Open(arg);
    }

    // ---------------- Windows ----------------

    public void ShowMainWindow()
    {
        if (main is null)
        {
            main = new MainWindow();
            main.Closed += (_, _) => { main = null; Presenter.Shared.PresenterWindowClosed(); };
        }
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
    }

    public void ShowPrompter(bool editOwnText = false)
    {
        if (editOwnText) PrompterRuntime.Shared.EditingOwnScript = true;
        prompter ??= new PrompterWindow();
        prompter.Closed += (_, _) => prompter = null;
        prompter.Show();
        prompter.Activate();
    }

    // ---------------- Opening decks and links ----------------

    public async Task Open(string arg)
    {
        if (arg.StartsWith("bema:", StringComparison.OrdinalIgnoreCase)) { HandleLink(arg); return; }
        if (Presenter.IsDeck(arg) && (CloudDeck.IsCloud(arg) || File.Exists(arg)))
        {
            ShowMainWindow();
            await Presenter.Shared.Load(arg);
        }
    }

    /// <summary>
    /// bema://present (the deck open in PowerPoint), bema://prompter (with text=… to prompt it),
    /// bema://next, bema://previous, bema://start, bema://end — the Mac app's links.
    /// </summary>
    void HandleLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return;
        var p = Presenter.Shared;
        var query = uri.Query.TrimStart('?');
        switch (uri.Host.ToLowerInvariant())
        {
            case "present": _ = p.PresentOpenDeck(); break;
            case "next": p.Next(); break;
            case "previous" or "back": p.Previous(); break;
            case "start": p.RequestStart(); break;
            case "end": p.EndShowButton(); break;
            case "prompter":
                var text = query.Split('&').Where(s => s.StartsWith("text=", StringComparison.Ordinal))
                    .Select(s => Uri.UnescapeDataString(s[5..].Replace('+', ' '))).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(text)) PrompterRuntime.Shared.UseOwnScript(text);
                ShowPrompter(editOwnText: query.Split('&').Contains("text"));
                break;
        }
    }

    /// <summary>bema:// links open Bema (per user, no administrator needed).</summary>
    static void RegisterLinkScheme()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\bema");
            key.SetValue("", "URL:Bema");
            key.SetValue("URL Protocol", "");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{exe}\" \"%1\"");
            // Right-click a deck in File Explorer: "Present with Bema" (the Mac's Finder service).
            foreach (var ext in new[] { ".pptx", ".ppsx", ".pptm", ".ppt" })
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\SystemFileAssociations\{ext}\shell\PresentWithBema");
                verb.SetValue("", "Present with Bema");
                verb.SetValue("Icon", $"\"{exe}\"");
                using var run = verb.CreateSubKey("command");
                run.SetValue("", $"\"{exe}\" \"%1\"");
            }
        }
        catch (Exception) { }
    }

    // ---------------- One Bema at a time ----------------

    static void ForwardToRunningBema(string[] args)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Pipes.ForUser(InstanceName), PipeDirection.Out);
            pipe.Connect(2000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(args.Length == 0 ? "--show" : string.Join('\n', args));
        }
        catch (Exception) { }
    }

    void ListenForOtherInstances()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    // Only this Windows user can connect.
                    using var pipe = Pipes.OwnerOnly(Pipes.ForUser(InstanceName), PipeDirection.In);
                    await pipe.WaitForConnectionAsync();
                    using var r = new StreamReader(pipe);
                    var text = await r.ReadToEndAsync();
                    await Dispatcher.InvokeAsync(async () =>
                    {
                        var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (args.Length == 0 || args[0] == "--show") { ShowMainWindow(); return; }
                        foreach (var a in args) await Open(a);
                    });
                }
                catch (Exception) { await Task.Delay(500); }
            }
        });
    }

    // ---------------- Tray (the Mac's menu bar icon) ----------------

    void StartTray()
    {
        var icon = GetResourceStream(new Uri("pack://application:,,,/Assets/Bema.ico"))?.Stream;
        tray = new Forms.NotifyIcon
        {
            Icon = icon is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(icon),
            Text = "Bema",
            Visible = true,
        };
        // Left or right click opens the same panel (as on the Mac).
        tray.MouseUp += (_, e) =>
        {
            if (e.Button is Forms.MouseButtons.Left or Forms.MouseButtons.Right) ToggleTrayPanel();
        };
    }

    void ToggleTrayPanel()
    {
        trayPanel ??= new TrayPanel();
        if (trayPanel.IsVisible) { trayPanel.Hide(); return; }
        trayPanel.ShowNearTray();
    }

    // ---------------- Ctrl+Alt+P: present the deck open in PowerPoint ----------------

    void RegisterHotkey()
    {
        var p = new HwndSourceParameters("Bema hotkey") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
        hotkeySource = new HwndSource(p);
        hotkeySource.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotkeyId) { _ = Presenter.Shared.PresentOpenDeck(); handled = true; }
            return IntPtr.Zero;
        });
        Native.RegisterHotKey(hotkeySource.Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 0x50 /* P */);
    }

    public void Quit()
    {
        _ = Task.Run(async () =>
        {
            if (Presenter.Shared.IsPresenting) await Presenter.Shared.EndShow();
        });
        if (hotkeySource is not null) Native.UnregisterHotKey(hotkeySource.Handle, HotkeyId);
        Server.Stop();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); }
        Presenter.Shared.Shutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
