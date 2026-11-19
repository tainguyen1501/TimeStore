# TimeStore user guide

End-user documentation for running TimeStore and using its admin UI. Pages are added as user-facing flows ship
(see `docs/roadmap.md`).

- Running a node: see the repository `README.md` (configuration keys and ports).
- [Signing in to the admin UI](sign-in.md): access key and secret, roles, throttling, sessions.
- [Access keys](access-keys.md): create (secret shown once), roles, disable, delete.
- [Browsing objects](objects.md): folders, details, upload, download, delete.
- [Overview and metrics](metrics.md): storage, disk, request rates, latency.
- [Backups](backups.md): a backup key, a first backup, checking the file, a test restore.

The admin UI has the navigation on the left (Dashboard, Buckets, Access Keys, Metrics, Guide, Settings), the page
in the middle, and on wide screens a panel on the right with the signed-in key, the light/dark switch and the
storage used per bucket. This guide is also in the UI under **Guide**; **Settings** holds this browser's
preferences (appearance, default layouts).
