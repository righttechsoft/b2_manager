# B2 Manager

A Windows desktop app for managing [Backblaze B2](https://www.backblaze.com/cloud-storage) — buckets, files, and application keys — from one window.

Built on the **B2 Native API v4** with **zero NuGet dependencies**: just .NET 8, WPF, and the base class library.

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D6)](#requirements)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

---

## Features

### Buckets
- List, create, edit, and delete buckets
- Edit covers both bucket type (`allPrivate` / `allPublic`) and **lifecycle rules** (file prefix, days from upload to hiding, days from hiding to deletion). Buckets with multiple rules keep the extra rules untouched.
- **Total size and file count per bucket**, calculated in the background without blocking the UI and cached to disk. Hover the size cell to see when it was last calculated.
- Double-click a bucket to jump straight to its files

### Files
- Browse files with a **previous-version count** per file
- Upload, download, and **multi-select delete** (Ctrl/Shift-click)
- **Version browser** — select a file and click *Versions…* (or double-click it) to see every version with its size, date, and action. Sort by any column to find the largest or oldest versions and delete them individually.
- Size columns sort by actual byte count, not by their formatted text

### Application keys
- List keys with their capabilities, **bucket restrictions**, name prefix, and expiry
- Create keys with chosen capabilities, an optional bucket restriction, and an optional expiry
- The secret is displayed **once** on creation, with a copy button — Backblaze never returns it again
- Delete keys, with a warning if you are deleting the key you are currently signed in with

### Progress reporting
Long operations show a modal overlay with a progress bar. Uploads and downloads report **real byte progress**; multi-item deletions count `N of M`. Input dialogs and confirmations are always collected *before* the overlay appears.

---

## Security

Your master application key is stored locally, encrypted:

| | |
|---|---|
| **Location** | `%APPDATA%\B2Manager\credentials.bin` |
| **Cipher** | AES-256-GCM (authenticated encryption) |
| **Key derivation** | PBKDF2-HMAC-SHA256, 600,000 iterations, 16-byte random salt |

A password is required on **every** launch. The key is never written to disk in plaintext, and the password is never stored anywhere.

> **There is no password recovery.** This is deliberate — nothing on disk can decrypt the file without your password. If you lose it, use **Reset credentials** on the login screen and re-enter your Backblaze key.

---

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build (the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) is enough to run a published build)
- A Backblaze B2 account and an application key

## Getting started

```bash
git clone https://github.com/righttechsoft/b2_manager.git
cd b2_manager
dotnet run
```

On first launch you will be asked for your **Key ID**, **Application Key**, and a **password** to encrypt them locally. Every launch after that asks only for the password.

To produce a standalone build:

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

### Getting a Backblaze application key

In the Backblaze B2 console, go to **Account → Application Keys** and create a key. A master key gives full access; a restricted key works too, but the app will only be able to do what that key's capabilities allow.

---

## Project layout

| File | Purpose |
|---|---|
| `App.cs` | Entry point; wires the login window to the main window |
| `CredentialStore.cs` | Encrypted credential file (AES-GCM + PBKDF2) |
| `B2Client.cs` | B2 Native API v4 client — auth, buckets, files, keys, transfer progress |
| `SizeCache.cs` | On-disk cache of per-bucket sizes |
| `Dialogs.cs` | Login window, reusable form dialog, file version browser |
| `MainWindow.xaml` / `.xaml.cs` | Buckets / Files / Keys tabs and their handlers |

Local state lives in `%APPDATA%\B2Manager\`: `credentials.bin` (encrypted key) and `sizes.json` (size cache). Deleting that folder resets the app completely.

---

## Notes and limitations

- **Uploads are capped at 5 GB** — the app uses the single-call upload API and does not implement B2's large-file (multipart) flow.
- **Transfers cannot be cancelled** once started; closing the window is the only way to abort.
- **Deletion is permanent.** The app deletes file *versions* rather than hiding them, so deleted data is not recoverable.
- Bucket sizes are cached for 24 hours. They refresh automatically after an upload or delete in that bucket, and **Recalc Sizes** forces a full recount. Calculating a size lists every version in the bucket, which counts as billable class-C transactions on large accounts.
- Application keys are immutable — Backblaze offers no update endpoint, so keys can only be created and deleted.
- Bucket sizes include every file version, so they reflect true stored bytes rather than just current files.

## License

[MIT](LICENSE) © Right Tech Soft LLC
