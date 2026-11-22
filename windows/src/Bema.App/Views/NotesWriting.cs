using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Bema.App.Model;
using Bema.Core;

namespace Bema.App.Views;

/// <summary>
/// Speaker notes for slides that have none, written by the writer the presenter chose (a local model,
/// or Claude or OpenAI with their key). Nothing is sent until they ask; the notes stay drafts in Bema
/// until saved into the deck.
/// </summary>
public static class NotesWriting
{
    static readonly HttpClient Http = new();
    static readonly NoteWriter Writer = new(Http, Settings.ReadKey);
    static CancellationTokenSource? running;

    public static NoteWriterSettings Load() => new()
    {
        Kind = Settings.Get<NoteWriterKind?>("noteWriter", null),
        ClaudeModel = Settings.Get("noteWriterClaudeModel", NoteWriterSettings.DefaultClaudeModel),
        OpenAIModel = Settings.Get("noteWriterOpenAIModel", NoteWriterSettings.DefaultOpenAIModel),
        LocalUrl = Settings.Get("noteWriterLocalURL", NoteWriterSettings.DefaultLocalUrl),
        LocalModel = Settings.Get("noteWriterLocalModel", ""),
        LocalSendsPicture = Settings.Get("noteWriterLocalPicture", false),
    };

    public static void Save(NoteWriterSettings s)
    {
        Settings.Set("noteWriter", s.Kind);
        Settings.Set("noteWriterClaudeModel", s.ClaudeModel);
        Settings.Set("noteWriterOpenAIModel", s.OpenAIModel);
        Settings.Set("noteWriterLocalURL", s.LocalUrl);
        Settings.Set("noteWriterLocalModel", s.LocalModel);
        Settings.Set("noteWriterLocalPicture", s.LocalSendsPicture);
    }

    public static Task<IReadOnlyList<string>> LocalModels(string url) => Writer.LocalModels(url);

    public static void WriteOne(Window? owner, int slide) => _ = Write(owner, [slide]);

    public static void WriteMissing(Window? owner) => _ = Write(owner, DraftNotes.NeedingNotes(Presenter.Shared.Slides));

    static async Task Write(Window? owner, IReadOnlyList<int> slides)
    {
        var m = Presenter.Shared;
        var settings = Load();
        if (settings.Kind is not { } kind || !kind.WritesInBema())
        {
            new NoteWriterSettingsWindow { Owner = owner }.ShowDialog();
            settings = Load();
            if (settings.Kind is null || !settings.Kind.Value.WritesInBema()) return;
        }
        running?.Cancel();
        running = new CancellationTokenSource();
        var ct = running.Token;
        var visible = m.Slides.Where(s => !s.Hidden).ToList();
        var done = 0;
        foreach (var id in slides)
        {
            if (ct.IsCancellationRequested) break;
            var i = visible.FindIndex(s => s.Id == id);
            if (i < 0) continue;
            var s = visible[i];
            m.Notice = $"Writing notes for slide {i + 1} with {settings.Kind!.Value.ShortName()}… ({done + 1} of {slides.Count})";
            var wantsPicture = settings.Kind != NoteWriterKind.Local || settings.LocalSendsPicture;
            var brief = new SlideBrief(m.DeckName, i + 1, visible.Count, s.Title, await m.SlideText(s.Id),
                i > 0 ? visible[i - 1].Title : null, i + 1 < visible.Count ? visible[i + 1].Title : null,
                wantsPicture ? Png(m.Image(s.Id, 1280)) : null);
            try
            {
                var notes = await Writer.Write(brief, settings, ct);
                m.SetDraft(s.Id, notes);
                done++;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) { m.Notice = e.Message; return; }
        }
        m.Notice = done > 0 ? $"Wrote notes for {done} slide{(done == 1 ? "" : "s")}. They're drafts until you save them into the deck." : null;
    }

    static byte[]? Png(BitmapSource? img)
    {
        if (img is null) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}

/// <summary>Who writes notes, and where the slide goes when they do.</summary>
public sealed class NoteWriterSettingsWindow : PanelWindow
{
    public NoteWriterSettingsWindow() : base("Notes writer", 520, 560)
    {
        var s = NotesWriting.Load();
        var col = new StackPanel { Margin = new Thickness(20) };
        col.Children.Add(Ui.Text("Who writes speaker notes for slides that have none?", 15, "Text", FontWeights.SemiBold, wrap: true));
        col.Children.Add(Note("Nothing is sent anywhere until you choose a writer and ask for notes. Notes stay drafts in Bema until you save them into the deck."));
        var privacy = Ui.Text("", 12, "Muted", wrap: true).With(t => t.Margin = new Thickness(0, 8, 0, 12));
        var details = new StackPanel();
        var kinds = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        NoteWriterKind? chosen = s.Kind;
        foreach (var k in new[] { NoteWriterKind.Local, NoteWriterKind.Claude, NoteWriterKind.OpenAI })
        {
            var r = new RadioButton { Content = k.Title(), IsChecked = s.Kind == k, GroupName = "kind", Margin = new Thickness(0, 4, 0, 4) }.Fg("Text");
            r.Checked += (_, _) => { chosen = k; Fill(); };
            kinds.Children.Add(r);
        }
        col.Children.Add(kinds);
        col.Children.Add(privacy);
        col.Children.Add(details);

        var claudeKey = new PasswordBox { Password = Settings.ReadKey(NoteWriterKind.Claude) ?? "", Padding = new Thickness(8, 6, 8, 6) };
        var openAIKey = new PasswordBox { Password = Settings.ReadKey(NoteWriterKind.OpenAI) ?? "", Padding = new Thickness(8, 6, 8, 6) };
        var claudeModel = new TextBox { Text = s.ClaudeModel };
        var openAIModel = new TextBox { Text = s.OpenAIModel };
        var localUrl = new TextBox { Text = s.LocalUrl };
        var localModel = new ComboBox { IsEditable = true, Text = s.LocalModel };
        var localPicture = new CheckBox { Content = "Send the slide's picture (for models that read images)", IsChecked = s.LocalSendsPicture };

        void Fill()
        {
            details.Children.Clear();
            privacy.Text = chosen?.Privacy() ?? "";
            switch (chosen)
            {
                case NoteWriterKind.Local:
                    details.Children.Add(Field("Server address", localUrl));
                    details.Children.Add(Field("Model", localModel));
                    details.Children.Add(localPicture);
                    _ = Task.Run(async () =>
                    {
                        var models = await NotesWriting.LocalModels(Dispatcher.Invoke(() => localUrl.Text));
                        Dispatcher.Invoke(() => { localModel.ItemsSource = models; if (localModel.Text.Length == 0 && models.Count > 0) localModel.Text = models[0]; });
                    });
                    break;
                case NoteWriterKind.Claude:
                    details.Children.Add(Field("Claude API key", claudeKey));
                    details.Children.Add(Field("Model", claudeModel));
                    details.Children.Add(Note("Kept on this PC, encrypted for your Windows account."));
                    break;
                case NoteWriterKind.OpenAI:
                    details.Children.Add(Field("OpenAI API key", openAIKey));
                    details.Children.Add(Field("Model", openAIModel));
                    details.Children.Add(Note("Kept on this PC, encrypted for your Windows account."));
                    break;
            }
        }
        Fill();

        var save = Ui.Pill("Save", () =>
        {
            NotesWriting.Save(new NoteWriterSettings
            {
                Kind = chosen,
                ClaudeModel = claudeModel.Text.Trim().Length > 0 ? claudeModel.Text.Trim() : NoteWriterSettings.DefaultClaudeModel,
                OpenAIModel = openAIModel.Text.Trim().Length > 0 ? openAIModel.Text.Trim() : NoteWriterSettings.DefaultOpenAIModel,
                LocalUrl = localUrl.Text.Trim().Length > 0 ? localUrl.Text.Trim() : NoteWriterSettings.DefaultLocalUrl,
                LocalModel = localModel.Text.Trim(),
                LocalSendsPicture = localPicture.IsChecked == true,
            });
            Settings.SaveKey(NoteWriterKind.Claude, claudeKey.Password);
            Settings.SaveKey(NoteWriterKind.OpenAI, openAIKey.Password);
            DialogResult = true;
        }, "AccentPill").With(b => b.IsDefault = true);
        var cancel = Ui.Pill("Cancel", Close).With(b => b.IsCancel = true);
        var buttons = Ui.Row(10, cancel, save).With(r => { r.HorizontalAlignment = HorizontalAlignment.Right; r.Margin = new Thickness(0, 16, 0, 0); });
        col.Children.Add(buttons);
        Content = new ScrollViewer { Content = col, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    static FrameworkElement Field(string label, Control input) =>
        Ui.Column(4, Ui.Text(label, 12, "Muted"), input).With(c => c.Margin = new Thickness(0, 0, 0, 10));
}
