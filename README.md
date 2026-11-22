
<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="site/brand/bema-mark-light.svg">
    <img src="site/brand/bema-mark.svg" alt="" width="72" height="72">
  </picture>
</p>

<h1 align="center">Bema</h1>

<h3 align="center">A presenter view you control, for PowerPoint and Keynote on Mac.</h3>

<p align="center">Your notes in a window you can resize and move. A prompter that follows your voice.<br>
Slides on the projector, everything else with you.</p>

---

PowerPoint for Mac locks its presenter view to full screen: you can't resize it, move it, or keep anything else next to it. Bema replaces it. PowerPoint or Keynote still runs the slideshow on the audience display, so animations, transitions and video keep working. Bema gives you your own presenter window, and a lot more.

- **Your presenter window.** Current and next slide, large speaker notes, a filmstrip, a timer that knows your planned length and tells you if you're behind. Resize it, move it, keep it on top.
- **A prompter that follows your voice.** Your notes become a script that scrolls as you speak, using Apple's speech recognition on your Mac. It keeps up when you skip a sentence or ad-lib, and works in English and Hebrew. No deck? Paste any text into the detached prompter and it works the same.
- **Hidden from screen sharing.** Detach the prompter, put it right under your camera, and it stays out of Zoom, Teams and Meet screen shares.
- **Point, spotlight, draw.** A laser pointer, spotlight, magnifier, pen, highlighter and stamps, drawn over the slide the audience sees.
- **Coach and rehearse.** Pace against a time budget for each slide, words per minute, filler words, and a report after a rehearsal.
- **Speaker notes for slides that have none.** Written on your Mac by Apple Intelligence or a local model (Ollama, LM Studio), with your Claude or OpenAI API key, or by Claude Desktop with your subscription. They stay drafts in Bema until you save them into the deck.
- **Phone remote and live polls.** Scan a QR code to drive the show from your phone, or let the audience vote from theirs, over your Wi-Fi.

A **Present with Bema** button on PowerPoint's Home tab (File → Add Bema Button to PowerPoint…), or one keystroke from PowerPoint or Keynote (⌃⌥P), hands the deck to Bema. Or right-click a `.pptx` or `.key` in Finder and choose **Present with Bema**.

<!-- Screenshot placeholder: add a render of the presenter window in docs/images once one is made from the sample deck. -->

## Download

**[Download the latest release](https://github.com/getbema/bema/releases/latest)** (a signed and notarised app).

- macOS 14 or later.
- Microsoft PowerPoint or Apple Keynote.
- Bema asks to **control PowerPoint or Keynote** (Automation) and for **Accessibility** to place the slideshow full screen on the audience display. The microphone, speech recognition and camera are asked for only when you turn on a feature that uses them.

## Privacy

Bema runs on your Mac. No account, no cloud service, no analytics.

- **Speech stays on your Mac** when your language supports on-device recognition. When it doesn't, Bema says so before listening, and Apple's speech service is used.
- **Nothing is recorded.** Audio is never saved, and neither is any transcript beyond the last few words shown as captions.
- **The phone remote and polls** run a small web server on your Mac only while they're on, reachable from your local network through a link with a random secret.
- **Writing speaker notes** sends nothing anywhere until you choose a writer. Apple Intelligence runs on your Mac and a local model only at the address you give; with a Claude or OpenAI key, a slide's text and picture go to that company only when you ask for its notes. Claude Desktop can read your slides only while you allow it, through a socket on your Mac.
- **Talk reports** (time per slide, word counts) are saved on your Mac only.
- **Updates:** once a day Bema checks https://getbema.dev/appcast.xml for a new version, sending only its version number. An update installs only with Bema's signature. Turn it off in the Bema menu: **Check for Updates Automatically**.

Full detail is in [docs/GUIDE.md](docs/GUIDE.md).

## Windows

A Windows version is in progress in [windows/](windows/README.md): the same presenter for PowerPoint on Windows. It isn't released yet.

## Build from source

You need Xcode 16 or later (or the Swift 6 command line tools). Releases are made with `Scripts/release.sh` (see the script for what it needs).

```bash
swift test                       # run the tests
./Scripts/build_app.sh --install # build Bema.app and copy it to ~/Applications
```

The build script signs with your Developer ID or Apple Development certificate when one is installed, so macOS remembers Bema's permissions across rebuilds. Without one, it signs ad hoc and macOS asks again after each rebuild.

## Learn more

- [docs/GUIDE.md](docs/GUIDE.md): every feature, keyboard shortcuts, links and permissions
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): how Bema drives PowerPoint and Keynote, and how the pieces fit
- [docs/ROADMAP.md](docs/ROADMAP.md), [docs/CHANGELOG.md](docs/CHANGELOG.md), [docs/DECISIONS.md](docs/DECISIONS.md)
- [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md)

Bema isn't affiliated with Microsoft or Apple. PowerPoint is a trademark of Microsoft; Keynote is a trademark of Apple.

## License

MIT. See [LICENSE](LICENSE).
