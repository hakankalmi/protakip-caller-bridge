# ProTakip Caller Id Bridge

A tiny Windows tray application that forwards caller-ID events from a
CIDSHOW-series USB device (C812A / C814A) to the ProTakip web panel.

## How it fits in

```
┌────────────────────────┐
│  Secretary PC          │
│                        │
│  ProTakipCallerBridge  │
│   ├─ cid.dll (P/Invoke)│
│   └─ HTTPS POST        │
│          │             │
└──────────┼─────────────┘
           ▼
  api.protakip.com/caller-id/ingest
           │
           ▼ SignalR
  app.protakip.com (browser)
```

## First-run

1. Install: download the release zip from the web panel's Caller ID popup.
2. Extract anywhere (e.g. `C:\Program Files\ProTakipCallerBridge`).
3. Run `ProTakipCallerBridge.exe`.
4. On first launch, paste the 6-digit pair code from the web panel.
5. Bridge registers itself in `HKCU\...\Run` so it auto-starts on login.

Config lives at `%APPDATA%\ProTakipCallerBridge\config.json`. Delete this
file to unpair.

## Build from source

```
dotnet publish ProTakipCallerBridge.csproj -c Release -o publish
```

Produces a self-contained folder (~170 MB) with the .NET 8 runtime + both
`cidshow_x64/cid.dll` and `cidshow_x86/cid.dll`.
