# 24seven 0.3.4 for Android

<div align="center">
<img src="public/logo.svg" alt="24seven logo" width="96" height="96">
</div>

_Changes since `24seven-v0.3.3`._

🐛 Fixes

- Two accounts on servers that were restored from the same backup could overwrite each other's appointments and tasks on the phone. Every account now keeps its own copy. With more than one account, each account syncs once from scratch after the update, which restores its data; changes you haven't synced yet are kept.
- If registering the phone with a server failed (offline, or a server hiccup), the app now tries again at the next sync, so instant updates keep working.

[<img src="https://raw.githubusercontent.com/Richy1989/keepIT/main/docs/bymeacoffee.png" alt="Buy Me A Coffee" height="60">](https://buymeacoffee.com/spaceelephant)
