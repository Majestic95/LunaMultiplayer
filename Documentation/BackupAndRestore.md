# Backup & Restore — Server Owner's Guide

This guide explains how the Luna Multiplayer server keeps backups, how to inspect a snapshot before restoring, and how to roll back to a previous state if something goes wrong.

---

## Mental model

The server uses **two parallel mechanisms** — don't confuse them.

| | Periodic flush | Archive snapshot |
|---|---|---|
| **Command** | `/backup` or `/backup now` | `/backup archive` |
| **What it does** | Writes current in-memory state over the canonical Universe files | Writes a complete timestamped copy of the Universe into `_archives/` |
| **Default cadence** | Every 30 seconds (while a player is online) | Every 24 hours |
| **Setting** | `IntervalSettings.BackupIntervalMs` | `IntervalSettings.ArchiveBackupIntervalHours` |
| **Restorable?** | **No** — each flush overwrites the previous on-disk state | **Yes** — kept as separate folders |
| **Retention** | n/a | Last 14 by default (`ArchiveBackupRetentionCount`) |

Only **archive snapshots** can be restored. The 30‑second flush is what keeps the canonical files up to date so that a crash loses at most ~30 s of play — it is not a rollback mechanism.

---

## Disk layout

There is **one** Universe folder. The server only ever reads from it. Snapshots are timestamped subfolders inside it, inert until you restore one:

```
<server.exe folder>\
└── Universe\                                  ← live state (server reads this)
    ├── Vessels\
    ├── Kerbals\
    ├── Crafts\
    ├── Groups\
    ├── Scenarios\
    ├── Agencies\                              ← only under PerAgencyCareer=true
    ├── Subspace.txt
    ├── StartTime.txt
    └── _archives\                             ← snapshots live here
        ├── 2026-05-20_03-00-00-117\
        ├── 2026-05-21_03-00-00-091\
        ├── 2026-05-22_03-00-00-205\
        └── pre-restore-2026-05-22_14-02-31-908\
```

There is **no CLI flag or env var** to point the server at a different folder. To use a different save you change the contents of `Universe\`, not the server's configuration.

---

## Defaults you may want to tune

`Settings/IntervalSettings.xml`:

| Setting | Default | Meaning |
|---|---|---|
| `BackupIntervalMs` | `30000` | Flush cadence in ms |
| `ArchiveBackupIntervalHours` | `24` | Snapshot cadence in hours (`0` disables) |
| `ArchiveBackupRetentionCount` | `14` | Snapshots to keep before pruning the oldest (`0` = unlimited) |

So out of the box: one snapshot per day, 14 days retained.

---

## Admin commands

Typed into the **server console window** (not in-game chat):

| Command | Effect |
|---|---|
| `/backup` or `/backup now` | Force an immediate flush. No separate copy kept. |
| `/backup archive` | Take an immediate timestamped snapshot. |
| `/backup list` | List available snapshots, newest first. |
| `/backup restore <timestamp>` | Restore from a named snapshot. Refuses if any player is connected. |

---

## Inspecting a snapshot before restoring

Snapshots are plain text — open them in any editor or grep tool. The fields you usually want are right at the top of each file.

**Vessel files** (`_archives\<ts>\Vessels\<guid>.txt`):
- `name = ` — vessel name
- `type = ` — Ship / Lander / Probe / …
- `sit = ` — LANDED / SPLASHED / ORBITING / FLYING / …
- `ref = ` — orbiting body index
- Plus full orbit, parts, crew

**Scenario files** (`_archives\<ts>\Scenarios\<name>.txt`):
- `Funding.txt` → `funds =`
- `ResearchAndDevelopment.txt` → `sci =`
- `Reputation.txt` → `rep =`
- `ContractSystem.txt` → active / offered / completed contracts

Useful PowerShell one-liners on the server box:

```powershell
$snap = "C:\LMP-Server\Universe\_archives\2026-05-22_03-00-00-117"

# How many vessels are in this snapshot?
(Get-ChildItem $snap\Vessels).Count

# Find a specific vessel by name
Select-String -Path $snap\Vessels\*.txt -Pattern '^name = MyMunLander$'

# Funds at snapshot time
Select-String -Path $snap\Scenarios\Funding.txt -Pattern '^\s*funds = '

# Compare funds across all snapshots to find when something jumped
Get-ChildItem C:\LMP-Server\Universe\_archives -Directory | ForEach-Object {
    $f = Join-Path $_.FullName 'Scenarios\Funding.txt'
    if (Test-Path $f) { "{0,-30} {1}" -f $_.Name, ((Select-String $f -Pattern '^\s*funds = ').Line.Trim()) }
}
```

---

## Step-by-step restore (server still running)

**Scenario:** it's 2026-05-22 14:00 UTC, a science contract corrupted the save, and you want to roll back to last night's snapshot.

### 1. Warn players

```
/say Rolling back the save in 5 minutes — please land or save what you're doing.
```

### 2. List snapshots

```
/backup list
```

```
Archive snapshots (5, newest first):
  2026-05-22_03-00-00-117
  2026-05-21_03-00-00-091
  2026-05-20_03-00-00-205
  2026-05-19_03-00-00-068
  2026-05-18_03-00-00-440
```

### 3. Inspect the candidate

In a separate shell (see "Inspecting a snapshot" above) confirm the snapshot has the vessels / funds / contracts you expect. If anything looks wrong, try an older snapshot.

### 4. Disconnect all players

`/backup restore` refuses while clients are connected.

```
/listclients
/kick PlayerA Restoring save, please rejoin in 2 minutes
/kick PlayerB Restoring save, please rejoin in 2 minutes
/countclients          ← wait until this reports 0
```

### 5. Run the restore

```
/backup restore 2026-05-22_03-00-00-117
```

Expected output:

```
Taking pre-restore safety snapshot...
Archive backup written: C:\LMP-Server\Universe\_archives\2026-05-22_14-02-31-908
Restored from archive '2026-05-22_03-00-00-117'. Pre-restore safety snapshot saved
at 'pre-restore-2026-05-22_14-02-31-908'. Restart the server to load the restored
universe.
```

Two things happened:

1. The state immediately before the restore was snapshotted as `pre-restore-…` — your escape hatch if you picked the wrong one.
2. `Universe\Vessels\`, `Kerbals\`, `Scenarios\`, etc. were overwritten with the chosen snapshot's contents.

### 6. Restart the server

The running process is still holding the **pre-restore** state in memory. You **must** restart so it reloads from disk.

```
/restartserver
```

Or stop with Ctrl+C and launch `Server.exe` again.

### 7. Verify

When the server is back up:

```
/listclients          ← 0
/vessel info MyMunLander
```

Confirm the vessel info matches what you expected at snapshot time.

### 8. Let players back in

```
/say Save restored to 2026-05-22 03:00 UTC. You may reconnect.
```

---

## Reversing a restore

If you picked the wrong snapshot:

```
/backup list
```

`pre-restore-<timestamp>` will be at or near the top — that is the state your server was in just before step 5. Kick everyone again, then:

```
/backup restore pre-restore-2026-05-22_14-02-31-908
/restartserver
```

You're back to the state you had right before the restore.

---

## Manual recovery (server won't start)

If the canonical Universe is corrupted badly enough that `Server.exe` crashes on boot and `/backup restore` is unreachable:

### 1. Stop the server

Close `Server.exe` if it's still running at all.

### 2. Move the broken state aside

Don't delete it — you may want it for debugging later.

```powershell
$u = "C:\LMP-Server\Universe"
Rename-Item $u\Vessels       Vessels.broken
Rename-Item $u\Kerbals       Kerbals.broken
Rename-Item $u\Scenarios     Scenarios.broken
if (Test-Path $u\Agencies)   { Rename-Item $u\Agencies Agencies.broken }
Rename-Item $u\Subspace.txt  Subspace.txt.broken
Rename-Item $u\StartTime.txt StartTime.txt.broken
```

### 3. Copy a snapshot's contents up into `Universe\`

```powershell
$src = "C:\LMP-Server\Universe\_archives\2026-05-22_03-00-00-117"
$dst = "C:\LMP-Server\Universe"

Copy-Item "$src\Vessels"       $dst -Recurse
Copy-Item "$src\Kerbals"       $dst -Recurse
Copy-Item "$src\Scenarios"     $dst -Recurse
if (Test-Path "$src\Agencies") { Copy-Item "$src\Agencies" $dst -Recurse }
if (Test-Path "$src\Crafts")   { Copy-Item "$src\Crafts"   $dst -Recurse }
if (Test-Path "$src\Groups")   { Copy-Item "$src\Groups"   $dst -Recurse }
Copy-Item "$src\Subspace.txt"  $dst
Copy-Item "$src\StartTime.txt" $dst
```

### 4. Start the server

`Server.exe` always reads from `Universe\`. Only the contents changed.

### 5. Clean up

Once the server is running cleanly, remove the `*.broken` items at your leisure.

---

## Operational tips

- **Take an ad-hoc archive before risky changes.** Before applying a settings change, restarting after a mod update, or rolling a hand-edit, run `/backup archive` so you have a known-good restore point with a hand-pickable timestamp.
- **Keep enough retention.** The default 14 daily snapshots covers two weeks. If your group plays less often, increase `ArchiveBackupRetentionCount` so a snapshot from "the last time we played" is always available.
- **Inspect before you restore.** A wrong restore loses any session play since the snapshot. The pre-restore safety snapshot saves you from total loss, but inspecting first saves you the round-trip.
- **Archives are not off-site backups.** If the server's disk fails, all snapshots go with it. Periodically copy `Universe\_archives\` to a separate machine or cloud storage for true disaster recovery.
- **Archive snapshots can be opened on any machine.** The files are plain text — you can copy a snapshot to a development box, inspect or hand-edit it there, then copy it back into `_archives\` and restore as normal.
