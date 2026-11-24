# Changelog

What was built, newest first. The reasons behind choices are in [DECISIONS.md](DECISIONS.md); how it works is in [ARCHITECTURE.md](ARCHITECTURE.md); what's next is in [ROADMAP.md](ROADMAP.md).

Each entry says what changed and, where it applies, what was **not verified**.

## 2026-11-24

### The build scripts leave only the installed Bema for macOS to open
- Updating 0.1.18 in /Applications to 0.1.20 failed with *An error occurred while launching the installer*. At the time a second Bema was running from `build/release/Bema.app`, the copy `Scripts/release.sh` builds: macOS opens the newest version of an app it knows about, and that copy was newer than the one in /Applications. `release.sh` now removes that copy once the disk image is made and tells Launch Services to forget it and the copy it checks inside the disk image. `build_app.sh --install` moves the build into ~/Applications instead of leaving a second copy in `build/`.
- Checked: both scripts parse; `lsregister -u` removes a copy from Launch Services, and the stale copies on the maintainer's Mac were removed that way. Not verified: that two running copies were what stopped Sparkle's installer (its log wasn't captured), a full release run with the change, or `build_app.sh --install` (it quits a running Bema, and a show was on).

### Show something else mid-talk, then resume the slides (Bema 0.1.20)
- During a show, a new button next to **End show** (also **Slideshow → Show Something Else**, ⇧⌘S, and **Step out** in the menu bar panel) takes the slideshow off the audience display so a browser or demo there is what a call sharing that display sees. The timer keeps running and Bema remembers the slide; **Resume** (⌘↩ or ⇧⌘S) starts the show again on that slide and display. Ending the talk while stepped out stops the timer as usual; Esc ends it too.
- Checked: tests that stepping out keeps the timer running and remembers the slide, that ending while stepped out stops the timer, and that it only happens during a show; renders of the top bar in a show and stepped out at the narrowest width. Not verified: live with PowerPoint and a call app sharing a display, or with Keynote.

## 2026-11-23

### Decks opened from Outlook attachments load (Bema 0.1.19)
- Presenting a deck PowerPoint had opened from an Outlook attachment failed with *Couldn't read the presentation: it doesn't look like a PowerPoint (.pptx) file*. The deck was fine: macOS keeps Outlook's folder from other apps, so Bema couldn't open the file. Bema now notices a file it isn't allowed to open and reads the titles and notes through PowerPoint instead, as for OneDrive decks. A file that really isn't a deck still gets the old message.
- Checked: tests for a file Bema can't open, a missing file and a file name with brackets; live, an attachment deck open in PowerPoint loaded with its slides and previews. Not verified: writing notes into such a deck (it's a read-only copy, so saving is expected to fail), or Keynote decks in a protected folder.

## 2026-11-22

### Guide: what to do when the PowerPoint button says its certificate isn't valid
- On a network whose firewall replaces getbema.dev's certificate with its own, PowerPoint blocks the button's add-in with *Add-in Error… isn't signed by a valid security certificate*. The guide now explains the cause, how to check it with `curl`, and the ways around it (another network, asking IT to exempt getbema.dev, or ⌃⌥P). Docs only; no code changed.
- Checked: on the affected network only getbema.dev came back with the firewall's certificate; other sites had their own. Not verified: that the button works on another network on the same Mac.

## 2026-11-19

### The timer looks stopped when the show ends, and each show starts at 0:00 (Bema 0.1.18)
- When a slideshow ended, Bema paused the timer but kept showing its time in red with *over time*, in the top bar and the menu bar panel, so it looked like it was still running. A paused timer is now dimmed and never says *over time*, and the next slideshow starts it from 0:00 instead of carrying on. Pressing ▶ on the timer after a show still carries on from where it stopped.
- Closing Bema's window (Bema keeps running in the menu bar) clears the timer outside a show, and the menu bar panel shows a timer only during a show or while one is counting or paused, so an old talk's time doesn't stay there or come back with the window. During a show, closing the window leaves the timer running.
- Checked: tests of how the timer looks, of stopping at the show's end and restarting at 0:00, and of closing the window clearing it. Not verified: whether the reported timer was still counting after the show (the screenshot showed it paused; the maintainer wasn't sure), or live with PowerPoint.

### The "only one display" notice goes away when a second display is used (Bema 0.1.17)
- Starting a show with one display left *Only one display is connected…* on screen for good: plugging in a display and restarting the show didn't clear it, even when the slides went full screen on the new display. Restarting now replaces it, and plugging a display in or out updates it straight away (mid-rehearsal it says to restart the show to go full screen). A stale Accessibility notice also goes once full screen works.
- With two displays but the slides on Bema's own display, the notice now says that and points to **Slides on**, instead of claiming there's only one display.
- The pre-flight check's display line follows displays plugged in while it's open.
- Checked: tests of which notice shows after starting and after displays change. Not verified: live with a display plugged in during a show (reported by a tester, not reproduced here).

### Quick prompt on Bema's start screen, and right-click on the menu bar icon (Bema 0.1.16)
- The start screen has a **Quick prompt** card under the drop zone: paste any text and click **Prompt this text** (⌘↩) to open the detached prompter with it, or **Open empty prompter**. It says what the prompter does, and under the box how long the text is at a speaking pace. Designed in Claude Design first (two layouts; the card under the drop zone was chosen so presenting a deck stays the main path).
- Right-clicking Bema's menu bar icon now opens its panel, like a left click.
- Checked: tests of the length line and of which window counts as the menu bar icon, a rendered card, Bema's window with the card, and live by the maintainer.

### The text box in the detached prompter scrolls (Bema 0.1.16)
- In 0.1.15 the box for your own text couldn't be scrolled: the prompter's script area, which it sat in, took every scroll to move the script. The box now sits outside that area, so it scrolls like any text field and isn't mirrored or faded with the script.
- The privacy check allows four everyday words its private-names list had started to flag across the code.
- Checked: a test of when the box shows, and scrolling live by the maintainer.

### The prompter works without a deck: paste your own text (Bema 0.1.15)
- The detached prompter can prompt any text, not just a deck's notes. Open it from the menu bar (**Prompter for your own text**), **Slideshow → Prompter for Your Own Text…**, or `bema://prompter?text` (with `text=…` to hand it the text directly, for Shortcuts). With no deck open, the detached prompter starts on the box. Paste or type, click **Prompt this text** (⌘↩), and Voice, Auto and Manual, Mirror, the reading line, the camera placement and hiding from screen sharing all work as before. **Text** in the prompter's controls edits it again or goes back to the deck's notes.
- While you prompt your own text, opening a deck or changing its notes doesn't replace it, there are no slide headings, and Follow slides is hidden.
- Bema remembers the last text you prompted, on this Mac only, to start the box with next time.
- Checked: tests of the script source switching and a rendered script, and live by the maintainer. Not verified: the `text=` link from Shortcuts.

## 2026-11-17

### The prompter marks slides without notes (Bema 0.1.14)
- A slide with no speaker notes used to vanish from the prompter's script, so slide 2's notes ran straight into slide 4's. It now keeps its heading with a faint *No notes for this slide* underneath, small and dim so it isn't read out, and never matched against your speech.
- Checked: a test of the script and a rendered prompter (`bema-prompter-no-notes.png`). Seen in the prompter by the maintainer. Not verified live with Voice following across that slide.

### The audience sees your mouse pointer, with the arrow tool too (Bema 0.1.13)
- In 0.1.12 the arrow tool's pointer never reached the audience: the presenter's slide only followed the mouse when another tool was chosen. It now follows it with the arrow tool as well, without ripples on click or a change of pointer shape.
- Checked: tests that the arrow tool listens to the mouse, and live by the maintainer during a slideshow. Not verified: with Keynote.

### The audience sees your mouse pointer (Bema 0.1.12)
- The arrow tool (first in the toolbar, formerly "Pointer off") now shows an ordinary Mac pointer on the audience display wherever your mouse is over the slide in the presenter window, moving with it like the laser. It's drawn only for the audience (you already have your own) and disappears when the mouse leaves the slide.
- Checked: rendered tests of the audience layer (arrow drawn, white edge on a dark slide) and the presenter's (no arrow). Not verified live on an audience display during a slideshow.

### Slide pictures from an older version of a deck are replaced (Bema 0.1.11)
- Bema keeps each deck's slide pictures by file, date and size. When a deck file was replaced while PowerPoint still had the old version open, Bema saved pictures of the old version under the new file and showed them from then on: the notes were right, the slides weren't. Bema now checks the saved pictures' text against the deck's slide titles when it opens a deck and makes them again when they don't match.
- Checked: the sample deck, which had been showing an older version's slides, showed its current slides after reopening. Not verified: decks without slide titles (nothing to compare, so they're left as they are).

### Top bar buttons no longer cut off, and a smoother presenter window (Bema 0.1.11)
- In a narrow window the LIVE badge wrapped onto two lines and the camera and Stage tools captions were squeezed or cut off at the window's edge. Badges and captions now keep their size, and when the bar doesn't fit, the Self view, On slides, Light and Stage tools buttons show their icons only (tooltips stay).
- Slide pictures are kept in a cache that drops the least-used ones past about 400 MB. Before, after 80 pictures it threw all of them away, so every visible slide and thumbnail was redrawn on the main thread at once, which showed as stutter in longer decks. The filmstrip also worked out the talk's per-slide timing once per thumbnail on every redraw, reading every slide's notes each time; it now does it once.
- Checked: the top bar rendered at 1320 and 1700 points wide, and Bema's window at its minimum width. Not verified: the bar while presenting with the camera on at the minimum width (PowerPoint wasn't started here), and whether the lag you saw is gone; it was not reproduced, only the causes above found by reading the code.

### The PowerPoint button no longer asks to open Bema.app (Bema 0.1.11)
- **Present with Bema** in PowerPoint used to bring up PowerPoint's "Bema add-in wants to open Bema.app" prompt every time. Now it shows "Opening Bema…" for a moment: Bema spots that dialog through Accessibility, closes it and presents the deck in front. If Bema doesn't (not running, older than this version, no Accessibility), the button opens the `bema://` link and the prompt as before; that link now also carries a cloud deck's address, and a short reason Bema writes to its log.
- A listener on 127.0.0.1 was tried first and taken out: PowerPoint blocks its add-ins from connecting to it.
- Checked in PowerPoint: clicking the button opened the dialog, Bema closed it and presented with no prompt. Not verified: with Bema's window closed, on a cloud deck, or on PowerPoint versions other than this one.

## 2026-11-16

### Bema leaves the Dock when its window is closed (Bema 0.1.10)
- Bema is in the Dock and the app switcher only while its presenter window is open (or minimised). With the window closed it's just the menu bar icon. Opening the window again (from the menu bar, ⌃⌥P, a deck, a link, or opening Bema from Finder) puts it back. With the menu bar icon turned off it always stays in the Dock.
- **Show Bema window** is now the first item in the menu bar menu. Opening a deck with the window closed now reopens it; before, the deck could load with no window showing.
- Checked live: closing the window took Bema out of the Dock; opening Bema from Finder and a `bema://present?path=` link each brought the window and the Dock icon back. Not verified: the order of items in the menu bar menu (not opened here), ⌃⌥P and the PowerPoint button with the window closed.

### Website: every feature, with a side panel
- The home page has an **Everything in Bema** section: 48 feature cards in eight groups, with filters, search and a count. A card opens a panel that slides in from the side (a bottom sheet on phones) with how it works, shortcuts, a link to Help and the other features in its group; arrow keys move through them, Esc closes, and the page stays where it was. Each feature has its own link (`#feature-laser`), and the "Also in Bema" tags open their feature. It replaces the four-item "What it does" list.
- The *Your screen / What the call sees* section now comes right after the opening picture. The features section sits after the pointer tools, before Privacy.
- Checked in a browser at desktop and phone widths: filters, search, opening, next and previous, Esc, focus returning to the card, no sideways scrolling on a phone. Not verified: Safari and Firefox (checked in the app's Chromium-based browser), screen readers.

### Decks and links reach the Bema that's already open (Bema 0.1.9)
- With two copies of Bema on the Mac (a release in Applications and another anywhere else), opening a deck or a `bema://` link could start the second copy, which loaded the deck in a window behind the first, so the first seemed to ignore it. A second Bema now hands what it was asked to open to the one already running, brings it forward, and quits without showing a window.
- Checked live: with the release running, a forced second launch of the new build (`open -n`) with another deck loaded that deck in the running Bema and quit within a second and a half. Not verified: the Finder Quick Action and the PowerPoint button with two copies installed.

### Edit speaker notes in Bema (Bema 0.1.8)
- A pencil next to the slide title edits any slide's notes in the notes panel; **Type notes** writes notes for a slide that has none. ⌘↩ keeps the change, Esc cancels. Edits are drafts like written notes: marked **Edited**, followed by the prompter, kept per deck, written into the deck with **Save to deck**, and ↶ returns to the deck's notes. An edit that ends up the same as the deck's notes isn't kept.
- Not verified: editing Hebrew notes (the editor doesn't align right yet), Keynote.

### Write speaker notes for slides that have none (Bema 0.1.7)
- **Write notes** on a slide with no notes, **All slides without notes** for the rest, from the notes panel, its ✨ menu or **View**. Notes are drafts: marked in the panel, followed by the prompter and phone remote, kept by Bema per deck, editable, and written into the deck (PowerPoint or Keynote speaker notes, then saved) only with **Save to deck**. A slide with notes in the deck is never overwritten.
- Writers, chosen in **View → Notes Writer Settings…**: Apple Intelligence on the Mac, a local model through the OpenAI chat API (Ollama, LM Studio), a Claude API key (Claude Opus 5.5 by default, with server-side fallbacks), an OpenAI API key, or the Claude app with the person's subscription. API keys are stored in the Keychain. The writer gets the slide's text, its neighbours' titles and, except on the Mac and by default for local models, a picture of the slide.
- The Claude app route is an MCP server: `Bema --mcp` relays between Claude Desktop and Bema over an owner-only socket, open only while **Let Claude read and write notes** is on. Tools: `get_deck`, `get_slide_image`, `set_speaker_notes` (drafts only). **Connect Claude Desktop** adds Bema to Claude's settings file and keeps a copy of the old one.
- Typing in a draft or the settings no longer moves slides (the arrow, space and letter shortcuts skip text fields and sheets).
- Checked live with a copy of the sample deck with three notes removed: Apple Intelligence wrote notes for one slide and then for the rest, **Save to deck** wrote them into the .pptx through PowerPoint (read back from the file), and the relay started Bema, answered `initialize`, listed the tools, returned the deck and a slide picture, and refused to replace a slide's deck notes.
- Not verified: the Claude and OpenAI APIs and a local model (no key or server here; the requests and responses are tested against the documented formats), Claude Desktop actually connecting (its settings file wasn't changed here), Keynote's **Save to deck**, cloud decks.

### Choose the display when you start; Bema recovers when the show ends in PowerPoint (Bema 0.1.6)
- With two or more displays, **Start slideshow** and ⌘↩ ask *Where should the slides go?*: a card per display (its shape, size and which one Bema is on), the usual one already selected, Return to start. It warns when the chosen display is Bema's own (the show then opens in a window). **Ask every time** turns it off; **Slides on → Ask Before Each Slideshow** turns it back on. The phone remote starts without asking.
- Ending the show in PowerPoint (Esc, closing the slideshow window), closing the deck or quitting PowerPoint during the show now returns Bema to the deck: the timer stops, the overlay goes, and **Start slideshow** works again. If the deck was closed, starting opens it again. Before, a closed deck left Start disabled and the timer running until Bema was restarted, and a quit PowerPoint left Bema presenting and sending AppleScript that could relaunch it. Ending the show in PowerPoint itself (Esc) also left Bema stuck presenting: PowerPoint keeps a slideshow view whose state can no longer be read, and Bema took that as a passing error.
- Checked live with the sample deck on one display: closing the deck mid-show returns to the deck with the timer stopped, Start slideshow reopens it and starts, and ending the show from PowerPoint returns to the deck.
- Not verified: quitting PowerPoint mid-show, the chooser with a second display connected (rendered with made-up displays; only one display was attached here), Keynote.

### The current slide resizes when you drag its handle (Bema 0.1.5)
- Dragging the handle under the current slide now makes it bigger or smaller. In 0.1.3 the handle set only the largest height the slide could take, and the column split its height evenly between the current slide and the up-next row, so the slide never reached it and dragging seemed to do nothing. The slide now gets exactly its share of the column (two thirds by default, so it starts bigger than before) and the up-next row takes the rest.
- Checked live with the sample deck: dragging up and down resizes it, double-click resets. Not verified: very small windows.

### Present decks from OneDrive, SharePoint and Teams (Bema 0.1.4)
- A deck PowerPoint opened from OneDrive, SharePoint or Teams can now be presented: ⌃⌥P, the welcome screen's **Present**, the Dock menu and `bema://present` all find it. Before, Bema said the deck in front "isn't a saved file on this Mac", because PowerPoint reports a web address for these decks and Bema read notes from the file.
- For such a deck Bema reads titles, notes and hidden slides through PowerPoint, and exports the slide previews each time it loads it (there's no modification date to tell when it changed). Saved slide timings and talk reports are kept under the deck's web address. Cloud decks aren't added to Open Recent.
- Checked live with a 57-slide deck PowerPoint had open from the web: notes, titles and previews loaded, and the deck stayed on its web copy after the preview export.
- Not verified: running the slideshow of a cloud deck (it uses the same commands as a local deck, by presentation name), a personal OneDrive deck, decks shared in a Teams chat, and `bema://present?url=https://…` from the PowerPoint add-in.

### Self view that closes every time, camera and microphone choice, a bigger current slide (Bema 0.1.3)
- The self view's X, the toolbar button, the menu bar and ⌥⌘V now close it every time. Bema kept a reference to the window from its first opening and dropped it on close; SwiftUI reuses the window when it reopens, so the reference stayed empty and closing did nothing. Bema now finds the window by its scene each time and decides open or close by whether it's on screen. Turning the camera on and off quickly no longer leaves it in the wrong state (start and stop run in order).
- The self view's hover controls sit on a dark bar with an outline, so they stay readable over a bright picture (Edge Light), and pointing at one says what it does at the bottom of the view. In circle mode the round mask no longer cuts them off (it hid the effects button). The effects button is now a sun, **Light**, in the self view, the top bar and Stage tools.
- Choose the camera from the self view (camera menu) or Stage tools; the bubble on the slides uses the same camera.
- Choose the microphone from the prompter's level meter; it applies to everything that listens.
- Drag the handle under the current slide to make it bigger (double-click resets); the size is remembered.
- Not verified: switching cameras and microphones with real devices (the choice logic is tested; the AVFoundation and Core Audio switching isn't), the hover hints and contrast over Edge Light, and the resize handle in the live window.

### Self view reopens; a control for macOS's video effects (Bema 0.1.2)
- Closing the self view now always turns the camera off and lets it open again. Before, closing it could leave Bema thinking it was still open: the camera kept running and the Self view button did nothing. Bema now watches the window itself close, and the button checks the window, not only its own flag. The self view also opens when Bema's main window is closed (menu bar, ⌥⌘V, `bema://selfview`).
- **Effects** in the top bar (while the camera is on), in the self view's hover controls and in Stage tools opens macOS's Video Effects menu, where Edge Light, Portrait and Studio Light are turned on or off.
- Not verified: the fix live (it's a window-closing timing issue), and the Video Effects menu opening from Bema.
- `Scripts/release.sh` uploads for notarisation and then checks the submission every 30 seconds (up to two hours), retrying a failed upload, and `NOTARY_ID=<id>` resumes a release from the DMG already uploaded. A single long wait failed twice when the connection dropped while Apple was slow.

### A designed sample deck
- `Samples/Sample.pptx` is now the dark six-slide talk from the website ("The slide is the easy part"), with notes written to be read aloud, instead of plain white title slides. `Scripts/make_sample_deck.py` builds it; the separate website copy of the generator is gone. Not verified: the deck in Keynote.

### Interactive demos on getbema.dev
- The pointer tools section is now a live demo: the same toolbar as Bema (laser, spotlight, magnifier, pen, highlighter, stamps, confetti, clear, black screen) drawing on the demo slide, with the same keys while it's on screen.
- The detached prompter section shows your screen during a call (Bema's presenter window, the prompter under the camera, your self view and the people on the call) and a toggle to **What the call sees**: just the slide, your camera bubble and the laser pointer. The prompter highlights the script word by word. Plain JavaScript, no dependencies; animation stops for reduced motion.
- The hero's Dock is redrawn in current macOS style (rounded tiles with gradients and fuller glyphs). The icons are still our own drawings, not Apple's or Microsoft's artwork.
- A square avatar for the GitHub organization (`site/brand/bema-avatar.png`, from `Scripts/make_avatar.swift`).
- Not verified: touch drawing on a real phone.

### Moved to github.com/getbema/bema
- Every link (README, security policy, issue templates, website, help, release script, update feed, Help menu) points at the organization.

### Help, camera controls in the top bar, presenter window hidden from sharing
- A Help page on getbema.dev (getting started, every feature, troubleshooting) and a Help menu in Bema that opens it, plus Report a Problem and What's New.
- **Self view** and **On slides** camera buttons in the top bar and on the welcome screen.
- The presenter window is hidden from screen sharing by default, so a screen share on a call shows the slides, not the notes. It can be turned off in Stage tools.

### Self view
- A floating, mirrored camera window that only you see (excluded from screen sharing: a direct capture of it is refused), round or rectangular, movable under the camera. It shares the camera with the camera bubble. Not verified: the picture itself (it can't be captured by design) and inside Zoom, Teams or Meet shares.

## 2026-11-15

### Menu bar app and Open at Login
- The menu bar icon is the A2 mark (a template image, so it matches light and dark menu bars). Bema keeps running in the menu bar after its window closes. The menu adds Open at login, the PowerPoint button, Check for updates and Quit. **Open Bema at Login** uses macOS's login items. Not verified: the icon in the live menu bar (on this macOS, menu bar items aren't listed per app) and Open at Login across a restart.

### A Present with Bema button in PowerPoint
- An Office add-in (`site/office/`, served from getbema.dev) adds **Present with Bema** to PowerPoint's Home tab; clicking it opens the deck in Bema through a `bema://` link (or, if PowerPoint won't open the link itself, through the browser). Installed and removed from Bema's File menu. Ribbon icons in the A2 mark (`Scripts/make_office_icons.swift`). **Not verified yet: the button in a live PowerPoint**, and whether PowerPoint for Mac opens the link directly.

### Share cards
- `site/brand/bema-social.png` (1280×640, drawn by `Scripts/make_social.swift`) is the GitHub social preview and getbema.dev's share card (Open Graph and X tags on the home and docs pages), plus an iPhone home-screen icon. Not verified: how each app (Slack, X, LinkedIn, iMessage) renders the card.

### First release and automatic updates
- `Scripts/release.sh`: a universal build signed with Developer ID and the hardened runtime, notarised and stapled, in a DMG; the update signed with Sparkle and added to `site/appcast.xml`; `--publish` creates the GitHub release and deploys the site.
- Sparkle checks for updates once a day; **Check for Updates…** and **Check for Updates Automatically** are in the Bema menu. Development builds never update themselves.
- The homepage has no placeholders left: the two missing images are illustrations with honest captions, the copy covers Keynote, and Download fetches the DMG.

### The A2 mark in the app
- The app icon, the mark on the welcome and loading screens and the menu bar panel now use A2, the logo chosen for the website: a lowercase b whose bowl is a screen, with the amber reading line inside. The README shows it too.

### Website deploys leave out Wrangler's folder
- `site/.assetsignore` now excludes `.wrangler/`. Earlier deploys had published Wrangler's local account cache file with the site; getbema.dev was redeployed without it and the file now returns 404.

### Ready for a public repository
- The app's bundle ID is now `dev.getbema.Bema` (from getbema.dev). macOS asks for Bema's permissions again once, and its settings start fresh.
- Links point at the public repository, `assafshafran/bema`.
- The website (`site/`, getbema.dev) is in the repository: generic Dock icons instead of other companies' app icons, real links, and Cloudflare's local cache left out.
- MIT license, README for visitors, the full feature guide in `docs/GUIDE.md`, architecture, roadmap, decisions, contributing and security docs, issue templates.
- A privacy check (`Scripts/privacy_check.py`) in git hooks and CI blocks private names, real email addresses, phone numbers, screenshots, database files and secrets (gitleaks).
- CI builds and runs the tests on every push.
- Tests use made-up text only.
- Secrets scan over the full history: gitleaks and trufflehog found nothing. Not verified: the CI workflows on GitHub.

### Keynote
- `.key` decks: slides, skipped slides and presenter notes read through Keynote, previews from Keynote's PDF export, the show in a Keynote window placed full screen on the audience display, jumps through the slide switcher, black and white screen drawn by Bema. Open With, Present with Bema, ⌃⌥P and the welcome card work with Keynote. **Not verified with a live Keynote show yet.**

### Prompter
- Smarter matching: a scored alignment that tolerates skipped and extra words, and words heard split or joined; the deck's unusual words are given to the recognizer as hints.
- Smooth following: a steady target, a 60 fps glide, a small lead while you speak, no drifting backward.
- The reading line sits near the top by default, with a setting; scroll with the mouse or trackpad in every mode; short labels under the prompter's icons.

### Stage tools
- Pointer effects (laser styles, spotlight, magnifier, stamps, ripples, reactions), coaching (pace, per-slide budgets, time cue, words per minute, filler words, rehearsal reports), voice commands, live captions, note cues, phone remote and live poll over local Wi-Fi, camera bubble, Daylight and high-contrast themes, a menu bar mini. Not verified on a real phone, webcam or projector.

### Voice prompter and detached prompter
- Notes as a script that follows your voice, auto-scrolls or is moved by hand; a detached window that floats under the camera and is excluded from screen sharing; present the deck open in PowerPoint with ⌃⌥P; `bema://` links.

### Bema
- Renamed from DeckPresenter; dark "backstage" design; app icon.

### First version
- A resizable presenter window for PowerPoint on Mac: current and next slide, notes, timer, filmstrip, the show full screen on a chosen display, laser pointer, pen and highlighter, Finder Open With and Services.
