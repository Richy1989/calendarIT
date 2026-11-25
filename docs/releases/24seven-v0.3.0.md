# 24seven 0.3.0 for Android

<div align="center">
<img src="public/logo.svg" alt="24seven logo" width="96" height="96">
</div>

_The first published release of the 24seven Android app._

✨ New Features

- Your calendar on the phone — Month, week, day and agenda views of every 24seven account you add, working offline and syncing in the background.
- Edit anywhere — Create and change appointments (including "only this", "this and following" and "all" for repeating ones), answer invitations, and undo a delete.
- Tasks — Open tasks grouped into Overdue, Today, Coming up and Someday, with subtasks, priorities and due dates.
- Reminders on the phone — Notification reminders ring as exact alarms, with Snooze and, for tasks, Done.
- Phone's calendar — Optionally show your appointments in Android's own calendar, and change them from other apps.
- Instant updates — With a UnifiedPush app such as ntfy, changes from the web arrive at once.
- Month widget — The whole month on your home screen.

🔧 Improvements

- A new look, the same as the web app: Midnight, Daylight and Graphite themes (Settings → Appearance, or "Match phone"), the web's fonts, icons and logo.
- Simpler navigation: Calendar, Tasks and Settings in a bottom bar; the views switch with one tap, ‹ › step back and forth, and "Today" jumps home.
- All switches for this phone (reminders, phone's calendar, instant updates, widget) live together in Settings.

🔒 Security

- Self-signed certificates are trusted only after you compare their fingerprint, and a changed certificate stops sync until you review it.
- Sign-in tokens are sealed with a key kept in the phone's secure hardware and never backed up.

📦 Deployment & Docs

- Needs Android 14 or newer and a 24seven server with the app sync API — the next server release after 0.10.0, or a build of `main` until then.
- Releases of the app are tagged `24seven-vX.Y.Z`, separate from the server's `vX.Y.Z`.

[<img src="https://raw.githubusercontent.com/Richy1989/keepIT/main/docs/bymeacoffee.png" alt="Buy Me A Coffee" height="60">](https://buymeacoffee.com/spaceelephant)
