# Signing in to the admin UI

The admin UI asks for an **access key** and its **secret key**, the same pair an S3 client uses.

## Who can sign in

| Key | Can sign in | What the UI shows |
|---|---|---|
| Root key (`TimeStore:S3:RootAccessKey` / `RootSecretKey` in the configuration) | Always, as **admin** | Everything, including create and delete actions |
| A key with role `admin` | Yes | Everything |
| A key with role `viewer` | Yes | All pages, without create, delete or other write actions; the right panel shows **Viewer · read-only** |
| A key with role `none` (the default for new keys) | No | Such keys are for S3 clients only |

A wrong key, a wrong secret, a disabled key and a key without a role all get the same message, so the page does not
reveal which keys exist.

## Signing in

1. Open the admin address: `http://<server>:9001` (`TimeStore:AdminPort`; loopback only unless
   `TimeStore:AdminAddress` says otherwise). In development `pnpm dev` serves it at `http://localhost:5173`.
   Any page you open sends you to the sign-in page first and back to that page afterwards.
2. Enter the access key and the secret key, then **Sign in**. The eye icon shows the secret while you type it.

After five failed attempts within 15 minutes (for one key or from one address) the server makes you wait before the
next attempt, doubling the wait each time up to 15 minutes. The page shows the remaining seconds and enables
**Sign in** again when the wait is over.

## Your session

- The signed-in access key and its role are at the top of the right panel; a click on them shows when the session
  ends.
- A session lasts `TimeStore:Admin:SessionHours` (24 hours by default). There is no "keep me signed in": every
  session has that lifetime.
- **Sign out** in that menu ends the session on the server.
- The session also ends when an admin disables or deletes the key or changes its role. The next action then brings
  you back to the sign-in page, and after signing in you return to the page you were on.
