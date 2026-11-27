# 24seven 0.4.0 for Android

<div align="center">
<img src="public/logo.svg" alt="24seven logo" width="96" height="96">
</div>

_Changes since `24seven-v0.3.9`._

✨ New Features

- CalDAV accounts — No 24seven server needed: choose **CalDAV server** when adding an account and sign in to Nextcloud, Radicale, Baïkal, iCloud (`caldav.icloud.com` with an app-specific password) or any other CalDAV server. You can type the server's address or a calendar's own address.
- Everything works with them: appointments (including "only this", "this and following" and "all"), invitations, tasks with subtasks, reminders on the phone, the phone's calendar and the month widget — offline too.
- Edits keep what 24seven doesn't show: attachments, other apps' alarms and extra fields stay as they were on the server.
- A change made elsewhere first wins, and you're told, with "Apply mine anyway".

🔧 Improvements

- Task-only lists (as in Nextcloud) are offered only for tasks, and calendars that take no tasks only for appointments.

📦 Deployment & Docs

- **After updating, add your accounts again.** The app's local storage starts over in this version, so the first start is empty. Everything on your server comes back with the first sync; changes made on the phone that hadn't synced yet are lost, so sync before you update.
- CalDAV accounts sync every 15 minutes and after each change; instant updates stay a 24seven server feature. Their categories come from the events' own categories and can't be edited.
- Needs Android 14 or newer.

[<img src="https://raw.githubusercontent.com/Richy1989/keepIT/main/docs/bymeacoffee.png" alt="Buy Me A Coffee" height="60">](https://buymeacoffee.com/spaceelephant)
