# Decisions

Choices made while building Bema, with the reason, so they are not re-argued or quietly undone. Newest first. Add one whenever a choice is made that the code alone would not explain.

## 2026-11-22

- **The Windows version is C# and WPF in `windows/`, beside the Mac app, not a cross-platform rewrite.** Chosen by the maintainer from three options (WPF, WinUI 3, Tauri/Electron). Native Windows APIs do what Bema needs: COM drives PowerPoint far more fully than AppleScript does on the Mac, `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` keeps windows out of screen sharing, and a click-through layered window draws over the slideshow. A web stack would have meant rewriting the Mac app too and adding dependencies. Same repository, so docs, CHANGELOG and site stay one.
- **Logic shared with the Mac app is ported to `Bema.Core` (no Windows dependency), not shared as code.** Swift and C# can't share source; keeping the pure rules in a library that builds on any OS lets them be tested on the maintainer's Mac and in CI, and many tests are ports of the Mac app's, so both apps follow speech, budget time and parse commands the same way. When a rule changes on one side, change and test it on the other.
- **The whole Mac feature set in the first Windows version.** Asked for by the maintainer, over a smaller first version. The camera self view and bubble, the ribbon button and updates are left for later: each needs its own Windows plumbing (MediaCapture, an Office add-in for Windows, an update feed and signing).
- **Windows speech is SAPI (`System.Speech`), on the PC only.** Windows' newer recognizer (`Windows.Media.SpeechRecognition`) uses Microsoft's online service for dictation, against Bema's rule that speech stays on the device when it can. SAPI recognizes only the speech languages installed in Windows (often just the display language); for any other Bema says it can't listen, rather than going online.
- **.NET 10, not .NET 8.** .NET 8 leaves support in November 2026; 10 is the long-term release.
- **One self-contained `Bema.exe`.** Nothing to install first, no .NET runtime to ask people for. Bigger download, simpler start.
- **Two NuGet packages: QRCoder and System.Speech** (plus ProtectedData for keys). QR codes have no built-in Windows API, and QRCoder draws them locally with no network. None of them contacts anything.
- **Settings in a JSON file in `%APPDATA%\Bema`, API keys encrypted with DPAPI for the Windows user.** The Mac's UserDefaults and Keychain have no exact Windows twin; DPAPI ties the keys to the user's sign-in like the Keychain does.
- **Pipes are owner-only.** The single-instance pipe and the MCP pipe get an access rule for the current user only, and their names include the user's SID, so another account on the same PC can't reach them (the Mac's socket is chmod 600).
- **No update check on Windows yet.** Sparkle is Mac-only and the allowed update check is the Mac app's; a Windows feed would be a new outgoing connection, so it waits for the maintainer to choose one.

## 2026-11-19

- **The timer pauses when a show ends, and the next show starts it from 0:00.** Chosen by the maintainer over resetting at once (the time would vanish before it's read) and over resuming (a new show kept an old show's time, red and *over time*). Starting, pausing or resetting by hand clears the ended-show mark, so a presenter who ended by mistake can press ▶ and carry on. A paused timer is drawn dimmed, never red, so it can't look live.
- **Closing the presenter window clears the timer, except during a show.** Asked for by the maintainer: closing Bema's window feels like closing the app, so the menu bar panel and the reopened window shouldn't keep an old talk's time. During a show the window may be closed by accident while the slides run, so the timer keeps going then. Caught with `NSWindow.willCloseNotification` for the `main` window, since SwiftUI's `onDisappear` doesn't always run when a Window scene closes.

- **Display notices are replaced when displays change, other notices are left.** Bema has one notice line. The display notices describe the displays at one moment, so they're recognised by their text and replaced on each start and on `didChangeScreenParametersNotification`; a notice about something else (previews, the deck) isn't cleared by plugging in a display. A display plugged in mid-show doesn't move the slideshow by itself: the presenter may be rehearsing on purpose, so the notice says to restart.

- **Quick prompt is a card under the drop zone, not a second equal choice.** Two layouts were drawn in Claude Design: the card below the drop zone, and deck and prompt side by side. Bema is first a deck presenter, so the deck keeps the big target; the card still has a paste box so a script is one paste and ⌘↩ away. The box starts empty each time, so a script isn't on the start screen for anyone looking at it.
- **Right-click on the menu bar icon is caught with a local event monitor.** SwiftUI's `MenuBarExtra` answers left clicks only and doesn't expose its `NSStatusItem`. A right click in Bema's own status bar window clicks the item's button instead, which opens the same panel. It looks for AppKit's `NSStatusBarWindow` by class name, so a future macOS could rename it; then right-click just does nothing again.
- **Your own prompter text lives beside the deck's notes, not in place of them.** Asked for by the maintainer: the prompter without a presentation. `PrompterModel` keeps the deck's slides and builds the script from your text while there is some, so a deck opening (or Bema rereading its notes) never wipes what you pasted, and **Use the deck's notes** goes straight back. Your text is one slide numbered 0, which hides the slide headings and turns off following the slideshow, rather than a second script type.
- **The last text you prompted is kept in Bema's preferences.** It's your own words, kept on this Mac only, so the box isn't empty after a restart; it's replaced by the next text you prompt. It's never sent anywhere.
- **The paste box is inside the detached prompter window, not a sheet.** A sheet is a separate window that wouldn't be hidden from screen sharing like the prompter is.

## 2026-11-17

- **A slide without notes shows a faint line in the prompter, not nothing.** Asked for by the maintainer. Skipped silently, the script ran from slide 2's notes straight into slide 4's, so a slide could be missed. The line is small, dim and italic so it isn't read out, and it isn't matched against speech. A deck with no notes at all keeps its "no speaker notes yet" message instead of a column of these lines.
- **The arrow tool gets its own hover-only input on the slide.** It shares `followsHover` with the laser, but the drawing tools' input also shows click ripples and changes the pointer's shape, which the plain arrow shouldn't. So `PointerTool.slideInput` picks a view that only reports where the mouse is, and a test checks the arrow tool gets it (0.1.12 shipped without it).
- **The arrow tool shows the presenter's pointer to the audience instead of "pointer off".** Asked for by the maintainer: a regular pointer, like the red dot but plainer. Bema starts on this tool, so moving the mouse over the slide now shows on the audience screen by default; it's drawn only while the mouse is over the slide, so it never drifts around their screen. It's a drawn shape, not `NSCursor`'s image, so it scales with the audience slide and gets a white edge on dark slides.

- **Saved slide previews are checked by their text against the deck's slide titles.** The cache key (path, date, size) can't notice that PowerPoint exported an older version it still had open. Comparing titles costs reading a few PDF pages' text on opening, needs nothing from PowerPoint, and treats a deck as matching when at least half its titled slides are found, so font and spacing differences don't cause needless re-exports.
- **The top bar drops captions rather than squeezing them.** `ViewThatFits` tries the bar with captions, then with icons only, instead of a width threshold, so it adapts to the deck name, the LIVE and REHEARSAL badges and the Light button without numbers to keep in step.
- **Slide pictures in an `NSCache` with a cost limit (about 400 MB of pixels)** instead of a dictionary cleared at 80 entries: clearing everything at once made the window redraw every slide in one go.
- **The PowerPoint button opens a dialog that Bema watches for, instead of a link.** PowerPoint asks "add-in wants to open Bema.app" every time an add-in opens a `bema://` link, with no way to remember the answer. A local listener was tried first and dropped: PowerPoint's add-in web view refuses to connect to `localhost` and `127.0.0.1` alike ("Load failed"). An add-in may open a dialog, though, and PowerPoint titles it with the add-in's name. Bema already has Accessibility, so it watches PowerPoint for a new dialog titled "Bema" (deck windows are never dialogs), closes it and presents the deck in front, the same as ⌃⌥P. The add-in waits 2.5 seconds for that and otherwise opens the link as before, so an older or closed Bema still works. Nothing new listens on the Mac or the network.

- ~~**The PowerPoint button asks Bema directly, on a fixed loopback port.**~~ (Tried and dropped the same day; see above.)

## 2026-11-16

- **Bema is in the Dock only while its presenter window is open** (asked for by the maintainer). It keeps running for ⌃⌥P, the PowerPoint button, links and the Claude connection, but most of that time there's nothing to switch to, so a Dock icon is noise. The policy is checked after every event (cheap) rather than on window notifications, which miss SwiftUI reopening a closed window. Without the menu bar icon Bema stays a regular app, since it would otherwise have no visible way back.

- **One Bema at a time; a second copy hands over and quits.** All copies share the bundle ID, and macOS may give a deck or a `bema://` link to a copy that isn't running, starting a second Bema the presenter doesn't see. Rather than relying on which copy macOS registers, the later Bema forwards its files and links to the earliest one running and quits. It can't simply be refused: the second copy is what received the request.

- **Written notes are drafts kept by Bema until "Save to deck"** (chosen by the maintainer). Bema reads notes from the deck and never changed it before; writing straight into someone's deck, possibly a shared one, is a surprise. Drafts are keyed by slide number, so reordering slides in the deck before saving can put a draft on the wrong slide. Notes the presenter edits in Bema are drafts too (marked "Edited", with an undo back to the deck's notes), so editing follows the same rule: the deck changes only with "Save to deck". Writers and Claude only fill slides that have no notes; replacing notes is the presenter's own edit.
- **The subscription route is MCP from Claude's side, not a button that calls Claude.** A Claude or ChatGPT subscription can't be used by another app: MCP lets Claude call Bema's tools, not the other way round. So Bema is an MCP server and its button copies the request and opens Claude. Claude Desktop runs local servers as commands, so Bema's own binary is the command (`--mcp`) and relays to the running app over a Unix socket that only the user can open, instead of a network port. ChatGPT only connects to MCP servers on the internet, so it isn't offered; API keys cover OpenAI.
- **Writers are called with plain HTTP, not SDKs.** There's no official Swift SDK for either API, and a dependency would be another thing that could phone home. Claude's request turns on server-side fallbacks for models that support them, so a refusal on one slide gets a second try.
- **Slide text and pictures come from the preview PDF,** not the deck file, so the writer sees the same for PowerPoint files, cloud decks and Keynote, including text in shapes that aren't placeholders.
- **The on-device model is the default when it's ready.** It's free and nothing leaves the Mac; the others are a choice the person makes, and each says where the slide goes.

- **Bema asks for the audience display at Start, not only in the top bar** (asked for by the maintainer). PowerPoint hides the choice in Set Up Slide Show, and people find out it's wrong when the slides appear on their laptop. The usual display is preselected so Return keeps it one keystroke; asking can be turned off. It's asked only when there's a choice (two or more displays) and only from Bema itself: the phone remote and clickers start on the remembered display, since nobody is at the Mac to answer.
- **When the deck disappears mid-show, Bema stops instead of waiting.** It checks that the app is still running before each status poll (AppleScript to a quit app relaunches it), and ends the show after ten failed polls in a row (about four seconds) rather than staying "presenting" forever. A show ended in PowerPoint leaves a slideshow view whose state can't be read; that counts as "no show", not a failure.

- **Cloud decks are read through PowerPoint, not downloaded.** PowerPoint reports a web address for a deck opened from OneDrive, SharePoint or Teams. Bema could have PowerPoint save a copy to disk and read that, but saving a copy can move PowerPoint's open document to the copy, and downloading it ourselves would need the person's Microsoft sign-in. Reading titles and notes over AppleScript (as for Keynote) keeps the presenter on the cloud copy and sends nothing anywhere. It costs a few seconds per load (about 12 for 57 slides), and previews are re-exported every time because there's no modification date to cache by. Recents stay files only: a web address can't be reopened without PowerPoint already having it open.

- **The repository moved to the `getbema` organization** (asked for by the maintainer), like Didja's `didja-dev`. GitHub redirects the old `assafshafran/bema` links, so 0.1.0's update feed and downloads keep working.

- **The presenter window is hidden from screen sharing by default** (asked for by the maintainer). People share their screen on calls; notes, the timer and the prompter should never show. Turning it off is one switch in Stage tools.
- **Help lives on getbema.dev, not inside the app.** It stays current without an update, can be linked from issues and the troubleshooting answers, and the Help menu opens the right section.

## 2026-11-15

- **The PowerPoint button is an Office add-in installed by Bema itself.** PowerPoint only adds ribbon buttons through add-ins loaded from an https address; getbema.dev serves it. Bema copies the manifest into PowerPoint's add-in folder rather than sending people to an add-in store, so it works without a Microsoft account and stays tied to the app.
- **The button passes a link, not data.** It reads only the deck's location and opens `bema://present?path=…`; if PowerPoint won't open custom links from an add-in, it falls back to the browser, which hands the link to Bema.

- **Automatic updates with Sparkle, checked once a day** (asked for by the maintainer). The only network traffic Bema starts on its own; it sends only the version, and an update installs only with Bema's EdDSA signature. Its own key account (`bema`), separate from Didja's. Development builds have no feed URL so they never replace themselves with a release.
- **The feed lives on getbema.dev, the files on GitHub Releases.** The site is already deployed from the repository; GitHub hosts the DMG under a stable name (`Bema.dmg`), so the site's Download button and the feed always point at the newest release.
- **Universal builds** (Apple silicon and Intel). SwiftPM builds both at no extra cost, and macOS 14 still runs on Intel Macs.

- **The mark is A2, "the reading line"** (chosen by the maintainer on the design canvas). A letter with a dot beside it is a common shape (Booking.com's "B."); the amber line inside the screen-shaped bowl is unique to Bema, says "presenter" and "prompter" at once, and stays one shape at 16 px. Never put it inside a circle or a red disc (Beats, Bitly). The geometry lives in `site/brand/bema-mark.svg`; `BemaMark` and `Scripts/make_icon.swift` draw the same shape.

- **The public repository is `assafshafran/bema`** (it can move to an organization later; GitHub redirects). Like Didja, the private development history moves to a private history repository, and work continues in the public one through the privacy hooks.
- **The bundle ID is `dev.getbema.Bema`.** The earlier ID contained the maintainer's name; the project now has its own domain.
- **The website lives in the repository (`site/`), like didja.dev in Didja's.** It shows generic app icons, never Apple's or Microsoft's.

- **The public repository starts from a single fresh commit.** The private development history has the maintainer's personal email on every commit and early test text that came from a real deck. Publishing a squashed copy (as Didja did) keeps all of that out, instead of rewriting history that has already been pushed.
- **The privacy check reuses Didja's private names list when it's on the maintainer's Mac.** The same person maintains both, so the same people should never appear in either repository. The list is read locally and never copied.
- **Keynote plays in a window, placed and made full screen by Bema.** Keynote's normal full-screen show uses its own presenter display, which would cover Bema. Without Accessibility, Bema falls back to Keynote's normal show.
- **Keynote's black screen is drawn by Bema.** Keynote has no scripting command for it; the overlay already sits over the slide.
- **Google Slides goes through a .pptx download for now.** It has no AppleScript; driving the browser is fragile, and importing needs a Google sign-in setup.
- **The voice prompter never drifts backward on its own.** When recognition stalls, sliding back to the last confirmed word looked like the text jumping up and down. Only a confirmed jump back, a click, a drag or a slide change moves it up.
- **A lead of at most 1.5 words while speaking.** Three words felt too fast; the lead only hides the recognizer's delay.
- **Small backward revisions are ignored, big jumps need confirmation after a new word.** The recognizer keeps revising its last words, and repeats the same partial result several times; a jump confirmed by a repeat isn't evidence.
- **The reading line sits near the top by default.** People speed up when recognition lags and need to see the next sentence.
- **The phone remote and polls live under random secret paths, and the server runs only while one is on.** Anyone on the same Wi-Fi could otherwise drive the show.
- **The detached prompter is excluded from screen sharing by default** (`NSWindow.sharingType = .none`). Verified with macOS's own capture: on screen but absent from a full-display capture.
- **The overlay over the audience slide is a non-activating `NSPanel`.** A plain `NSWindow` from a regular app doesn't show on the Space another display is showing, even with `.canJoinAllSpaces`.
- **Leaving full screen before ending a show.** Closing a full-screen window directly left an empty black Space on that display.
- **Jumping to a slide in PowerPoint steps one slide at a time.** PowerPoint's `go to slide` fails during a slideshow on Mac.
- **The name is Bema** (βῆμα, the speaker's platform in Athens, root of the Hebrew *bimah*). "MacPoint" was considered and dropped: Apple doesn't allow "Mac" in third-party app names.
- **PowerPoint runs the show rather than Bema rendering slides.** Animations, transitions, video and fonts stay exactly as the deck's author made them.
- **The website's Dock uses our own icons, styled like current macOS, not Apple's or Microsoft's app icons.** Their icon artwork is trademarked and copyrighted, and the repository and site are public.
- **Bema opens macOS's Video Effects menu rather than switching effects itself.** Edge Light, Portrait and Studio Light belong to macOS and apps can't turn them on or off; `AVCaptureDevice.showSystemUserInterface(.videoEffects)` is the supported way in.

## The self view is found by its scene id, not a kept reference (2026-11-16)
SwiftUI reuses a `Window` scene's window when it reopens, so a reference saved when the window first appeared and cleared on close stayed empty after a reopen and closing did nothing. Looking the window up by its identifier each time can't go stale, and the toggle decides by whether that window is on screen.

## Hints shown inside the self view, not only as tooltips (2026-11-16)
The self view floats over other apps while you present from them, and macOS often doesn't show tooltips for an app that isn't active. Pointing at a control writes what it does in the view's bottom label instead.

## One microphone choice for everything that listens (2026-11-16)
The prompter, speech coach, captions and voice commands share one microphone stream (`SpeechHub`), so the choice is the hub's, made in the prompter where people look for it. The audio engine is created fresh each time listening starts and the device is set only then, so the microphone still opens only while a listening feature is on.
