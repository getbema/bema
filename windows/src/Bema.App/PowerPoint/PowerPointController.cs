using System.IO;
using System.Runtime.InteropServices;
using Bema.App.Platform;
using Bema.Core;
using Microsoft.Win32;

namespace Bema.App.PowerPoint;

public enum ScreenState { Normal, Black, White }

/// <summary>Where the slideshow is: running (with its slide and window), not running, or its deck was closed.</summary>
public abstract record ShowStatus
{
    public sealed record Running(int Slide, bool Black, bool White, bool Done, System.Windows.Rect? Frame) : ShowStatus;
    public sealed record NotRunning : ShowStatus;
    public sealed record PresentationClosed : ShowStatus;
}

public sealed class PowerPointException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Drives PowerPoint for Windows through its COM object model (the Windows counterpart of the Mac app's
/// AppleScript). Every call runs on one STA thread, in order. PowerPoint runs the slideshow; Bema never
/// draws the slides for the audience.
/// </summary>
public sealed class PowerPointController : IDisposable
{
    // PowerPoint constants (from its type library).
    const int MsoTrue = -1, MsoFalse = 0;
    const int PpShowTypeSpeaker = 1, PpShowTypeWindow = 2;
    const int PpSlideShowRunning = 1, PpSlideShowPaused = 2, PpSlideShowBlackScreen = 3, PpSlideShowWhiteScreen = 4, PpSlideShowDone = 5;
    const int PpPlaceholderBody = 2;

    readonly StaWorker worker = new("Bema PowerPoint");
    dynamic? app;
    dynamic? presentation;
    string? presentationKey;

    public string AppName => "PowerPoint";

    /// <summary>Whether PowerPoint for Windows is installed (its COM class is registered).</summary>
    public static bool IsInstalled => Type.GetTypeFromProgID("PowerPoint.Application") is not null;

    public static bool IsRunning => System.Diagnostics.Process.GetProcessesByName("POWERPNT").Length > 0;

    dynamic App(bool launch)
    {
        if (app is not null)
        {
            try { _ = app.Version; return app; }
            catch (COMException) { app = null; presentation = null; }
        }
        app = Native.ActiveObject("PowerPoint.Application");
        if (app is null && launch)
        {
            var type = Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new PowerPointException("PowerPoint isn't installed on this PC.");
            app = Activator.CreateInstance(type);
            app!.Visible = MsoTrue;
        }
        return app ?? throw new PowerPointException("PowerPoint isn't running.");
    }

    dynamic Presentation()
    {
        if (presentation is not null)
        {
            try { _ = presentation.FullName; return presentation; }
            catch (COMException) { presentation = null; }
        }
        // Find it again by its name (after PowerPoint restarted, or the deck was reopened).
        if (presentationKey is not null && app is not null)
        {
            foreach (dynamic p in App(false).Presentations)
            {
                if (SameDeck((string)p.FullName, presentationKey)) return presentation = p;
            }
        }
        throw new PowerPointException("The presentation is no longer open in PowerPoint.");
    }

    static bool SameDeck(string fullName, string key) =>
        string.Equals(CloudDeck.IsCloud(fullName) ? fullName : SafeFull(fullName), key, StringComparison.OrdinalIgnoreCase);

    static string SafeFull(string p) { try { return Path.GetFullPath(p); } catch (Exception) { return p; } }

    /// <summary>The deck in PowerPoint's front window: a file path or a OneDrive/SharePoint address. Never launches PowerPoint.</summary>
    public Task<string?> ActivePresentation() => worker.Run<string?>(() =>
    {
        try
        {
            var a = Native.ActiveObject("PowerPoint.Application");
            if (a is null) return null;
            dynamic d = a;
            if ((int)d.Presentations.Count == 0) return null;
            string name = d.ActivePresentation.FullName;
            // An unsaved deck is just "Presentation1": nothing to present from.
            return CloudDeck.IsCloud(name) || Path.IsPathRooted(name) ? name : null;
        }
        catch (COMException) { return null; }
    });

    public Task Open(string location) => worker.Run(() =>
    {
        var a = App(true);
        var key = CloudDeck.Key(location);
        foreach (dynamic p in a.Presentations)
        {
            if (SameDeck((string)p.FullName, key)) { presentation = p; presentationKey = key; return; }
        }
        if (CloudDeck.IsCloud(location)) throw new PowerPointException("Open the deck in PowerPoint first, then present it with Bema.");
        try
        {
            presentation = a.Presentations.Open(location, MsoFalse, MsoFalse, MsoTrue);
            presentationKey = key;
        }
        catch (COMException e) { throw new PowerPointException($"PowerPoint couldn't open the deck: {e.Message}", e); }
    });

    /// <summary>Titles and notes read through PowerPoint (for decks with no file to read, like OneDrive ones).</summary>
    public Task<IReadOnlyList<SlideInfo>> ReadSlides() => worker.Run<IReadOnlyList<SlideInfo>>(() =>
    {
        var p = Presentation();
        var list = new List<SlideInfo>();
        int count = p.Slides.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic slide = p.Slides[i];
            var hidden = (int)slide.SlideShowTransition.Hidden == MsoTrue;
            var title = "";
            try { if ((int)slide.Shapes.HasTitle == MsoTrue) title = Lines((string)slide.Shapes.Title.TextFrame.TextRange.Text).Split('\n')[0].Trim(); }
            catch (COMException) { }
            list.Add(new SlideInfo(i, title, Lines(NotesText(slide)), hidden));
        }
        return list;
    });

    static string NotesText(dynamic slide)
    {
        try
        {
            foreach (dynamic shape in slide.NotesPage.Shapes.Placeholders)
            {
                if ((int)shape.PlaceholderFormat.Type == PpPlaceholderBody && (int)shape.HasTextFrame == MsoTrue)
                    return (string)shape.TextFrame.TextRange.Text;
            }
        }
        catch (COMException) { }
        return "";
    }

    /// <summary>PowerPoint separates paragraphs with \r and line breaks with a vertical tab.</summary>
    static string Lines(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\v', '\n').Trim();

    /// <summary>Writes speaker notes into the open deck and saves it. Returns the slides written.</summary>
    public Task<IReadOnlySet<int>> WriteNotes(IReadOnlyDictionary<int, string> notes) => worker.Run<IReadOnlySet<int>>(() =>
    {
        var p = Presentation();
        var written = new HashSet<int>();
        int count = p.Slides.Count;
        foreach (var (id, text) in notes)
        {
            if (id < 1 || id > count) continue;
            dynamic slide = p.Slides[id];
            foreach (dynamic shape in slide.NotesPage.Shapes.Placeholders)
            {
                if ((int)shape.PlaceholderFormat.Type != PpPlaceholderBody) continue;
                shape.TextFrame.TextRange.Text = text.Replace("\n", "\r");
                written.Add(id);
                break;
            }
        }
        if (written.Count > 0) p.Save();
        return written;
    });

    /// <summary>All the text on a slide (its shapes, in order), for the notes writer.</summary>
    public Task<string> SlideText(int id) => worker.Run(() =>
    {
        var p = Presentation();
        if (id < 1 || id > (int)p.Slides.Count) return "";
        var lines = new List<string>();
        foreach (dynamic shape in p.Slides[id].Shapes)
        {
            try
            {
                if ((int)shape.HasTextFrame == MsoTrue && (int)shape.TextFrame.HasText == MsoTrue)
                    lines.Add(Lines((string)shape.TextFrame.TextRange.Text));
            }
            catch (COMException) { }
        }
        return string.Join("\n", lines.Where(l => l.Length > 0));
    });

    /// <summary>Saves each slide as a PNG (PowerPoint draws them; Bema only shows them to the presenter).</summary>
    public Task ExportSlides(string folder, int width) => worker.Run(() =>
    {
        var p = Presentation();
        Directory.CreateDirectory(folder);
        double w = p.PageSetup.SlideWidth, h = p.PageSetup.SlideHeight;
        var height = (int)Math.Round(width * h / w);
        int count = p.Slides.Count;
        for (var i = 1; i <= count; i++) p.Slides[i].Export(Path.Combine(folder, $"{i}.png"), "PNG", width, height);
    });

    /// <summary>
    /// Starts the slideshow from a slide on a display. Full screen hides PowerPoint's own presenter view
    /// (Bema is the presenter view); otherwise the show opens in a window, for rehearsing on one display.
    /// </summary>
    public Task<bool> StartShow(System.Windows.Rect monitorPixels, int fromSlide, bool fullScreen) => worker.Run(() =>
    {
        var p = Presentation();
        dynamic settings = p.SlideShowSettings;
        settings.ShowPresenterView = MsoFalse;
        settings.ShowType = fullScreen ? PpShowTypeSpeaker : PpShowTypeWindow;
        dynamic window = settings.Run();
        try { window.View.GotoSlide(Math.Max(1, fromSlide)); } catch (COMException) { }
        var hwnd = new IntPtr((int)window.HWND);
        if (fullScreen)
        {
            return Native.SetWindowPos(hwnd, Native.HWND_TOP, (int)monitorPixels.X, (int)monitorPixels.Y,
                                       (int)monitorPixels.Width, (int)monitorPixels.Height, Native.SWP_SHOWWINDOW | Native.SWP_FRAMECHANGED);
        }
        var r = monitorPixels;
        Native.SetWindowPos(hwnd, Native.HWND_TOP, (int)(r.X + r.Width * 0.2), (int)(r.Y + r.Height * 0.2),
                            (int)(r.Width * 0.6), (int)(r.Height * 0.6), Native.SWP_SHOWWINDOW);
        return true;
    });

    dynamic? ShowWindow()
    {
        var a = App(false);
        if ((int)a.SlideShowWindows.Count == 0) return null;
        foreach (dynamic w in a.SlideShowWindows)
        {
            if (presentationKey is null || SameDeck((string)w.Presentation.FullName, presentationKey)) return w;
        }
        return null;
    }

    Task View(Action<dynamic> act) => worker.Run(() =>
    {
        var w = ShowWindow() ?? throw new PowerPointException("The slideshow isn't running.");
        act(w.View);
    });

    public Task Next() => View(v => v.Next());
    public Task Previous() => View(v => v.Previous());
    public Task GoTo(int slide) => View(v => v.GotoSlide(slide));
    public Task SetScreen(ScreenState state) => View(v => v.State = state switch
    {
        ScreenState.Black => PpSlideShowBlackScreen,
        ScreenState.White => PpSlideShowWhiteScreen,
        _ => PpSlideShowRunning,
    });

    public Task EndShow() => worker.Run(() =>
    {
        if (Native.ActiveObject("PowerPoint.Application") is null) return;   // never launch PowerPoint to end a show
        ShowWindow()?.View.Exit();
    });

    public Task<ShowStatus> Status() => worker.Run<ShowStatus>(() =>
    {
        if (Native.ActiveObject("PowerPoint.Application") is null) return new ShowStatus.PresentationClosed();
        try { Presentation(); }
        catch (PowerPointException) { return new ShowStatus.PresentationClosed(); }
        var w = ShowWindow();
        if (w is null) return new ShowStatus.NotRunning();
        dynamic view = w.View;
        int state = view.State;
        var slide = 0;
        try { slide = (int)view.Slide.SlideIndex; } catch (COMException) { }   // the black "end of show" screen has no slide
        System.Windows.Rect? frame = null;
        if (Native.GetWindowRect(new IntPtr((int)w.HWND), out var r)) frame = new System.Windows.Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return new ShowStatus.Running(slide, state == PpSlideShowBlackScreen, state == PpSlideShowWhiteScreen,
                                      state == PpSlideShowDone, frame);
    });

    /// <summary>The slideshow window's handle, to place Bema's overlay above it.</summary>
    public Task<IntPtr> ShowWindowHandle() => worker.Run(() => ShowWindow() is { } w ? new IntPtr((int)w.HWND) : IntPtr.Zero);

    /// <summary>Whether PowerPoint is set to show its own presenter view on a second display (Bema turns it off per show).</summary>
    public static bool PresenterViewIsDefault()
    {
        try { return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\PowerPoint\Options")?.GetValue("UsePresenterView") is int v && v != 0; }
        catch (Exception) { return false; }
    }

    public void Dispose()
    {
        worker.Run(() =>
        {
            if (presentation is not null) Marshal.FinalReleaseComObject(presentation);
            if (app is not null) Marshal.FinalReleaseComObject(app);
            presentation = null; app = null;
        }).Wait(2000);
        worker.Dispose();
    }
}
