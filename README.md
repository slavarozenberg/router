# Route Manager

[![Build](https://github.com/slavarozenberg/router/actions/workflows/build.yml/badge.svg)](https://github.com/slavarozenberg/router/actions/workflows/build.yml)

A Windows desktop app for sending traffic to chosen destinations through a chosen network interface, for example through a VPN tunnel, without touching anything else. You describe *what* should go *where* as rules. Route Manager turns those rules into system routes and keeps them correct when the network changes.

It is modeled on [Rouler](https://rouler.io/) by Photon Lane, and can import Rouler's rules.

## Features

- **Rule groups and rules.** You can add, edit, duplicate, delete, reorder and switch rules on or off. Each group can also be switched on or off as a whole.
- **Flexible destinations.** A destination can be a single address (`10.20.0.5`), an inclusive range (`10.20.1.0-10.20.1.255`) or a subnet (`10.30.0.0/22`). IPv4 and IPv6 are both supported, each with its own target interface. Ranges are split into the minimal set of CIDR routes.
- **Status for each rule.** A rule shows as *Active*, *Partial* or *Inactive*, with the reason, for example: interface down, no usable address, or destination already claimed by a higher rule.
- **Follows the network.** When routes, addresses or connectivity change, rules are re-applied after a short debounce. When a VPN comes up, its routes appear. If the gateway changes, the routes move to the new one.
- **Touches only its own routes.** Every route the app creates is recorded, and only those are ever removed. Routes created by Windows, VPN clients or you are never modified.
- **Master switch.** Turning *Steering* off removes all of the app's routes. Turning it on restores them.
- **Rouler import.** Reads Rouler's `rules.json`, together with interface names from `interfaces.json`.
- **Runs in the tray.** Closing the window keeps steering running in the background. *Exit* from the tray menu removes the app's routes.
- **Live connections.** Shows every TCP connection: the process, the remote address, the interface and gateway it leaves through, the route that matched, and the rule behind that route, if any. Connections steered by a rule are highlighted. You can filter by any column, and pause updates.
- **Activity log** and an **Applied routes** view showing exactly what is in the routing table.

## How it differs from Rouler

Rouler uses a WFP kernel driver, so its rules can apply to **individual apps**. Route Manager uses the standard Windows routing table, so every rule applies to **all apps**, the way Rouler's "All Other Apps" group does.

When you import from Rouler, app-specific groups are imported **switched off**, with a warning. Enabling such a group would steer every app, not just the one it was written for.

Rouler can also block traffic while a rule's interface is down. Routes can't do that: while an interface is unavailable, its rules show as *Inactive* and the traffic follows the normal routing table.

## Requirements

- Windows 10 or 11 (x64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build, or the .NET 10 Desktop Runtime to run a build
- Administrator rights to change routes

## Download

Get `RouteManager.exe` from the [latest release](https://github.com/slavarozenberg/router/releases/latest). It is a single self-contained file: nothing to install, no .NET runtime needed.

## Build and run

```sh
dotnet build
dotnet run --project src/RouteManager.App
```

The app starts **unelevated** and read-only. You can create and edit rules, and the statuses show what *would* be applied ("Planned"). Click **Restart as administrator** in the banner to apply rules for real. Alternatively, run `src/RouteManager.App/bin/Debug/net10.0-windows/RouteManager.exe` as administrator.

To build a self-contained executable:

```sh
dotnet publish src/RouteManager.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

The result is a single `RouteManager.exe` in `src/RouteManager.App/bin/Release/net10.0-windows/win-x64/publish/`.

## Usage

1. Optionally, click **Import from Rouler…** and pick `C:\ProgramData\Rouler\rules.json`.
2. Select a group and click **Add rule**.
3. Enter destinations (one per line) and choose the interface for IPv4 and/or IPv6 traffic. Each interface in the list shows whether it is up and which gateway it would use ("on-link" for tunnels without a gateway).
4. Turn on **Steering**.

To check that a rule works, open the **Connections** tab under the rules list and tick **Only through rules**. The list refreshes every 2 seconds while it is on screen. It shows TCP only: Windows does not record a remote address for UDP sockets. For each connection, Windows itself is asked which route it takes (`GetBestRoute2` from the connection's local address), so the list reflects the real routing table, including routes the app did not create.

When two rules claim the same destination, the rule higher in the list wins, and the other one reports the conflict. Use ▲/▼ to change the order.

Routes are created in the active routing table only, so they disappear on reboot. When the app starts, it restores them from your rules.

### Where data is stored

Settings are machine-wide, because routes are:

| File | Contents |
|---|---|
| `%ProgramData%\RouteManager\rules.json` | Groups, rules and the master switch |
| `%ProgramData%\RouteManager\applied-routes.json` | Routes created by the app (the only ones it will delete) |

## How it works

```
rules + interfaces ──► RoutePlanner ──► planned routes ──► Reconciler ──► add / delete
                                                              ▲
                              ledger (routes we own) + live routing table
```

- **`RoutePlanner`** (pure logic) expands each enabled rule into routes, using the target interface's gateway, or on-link when it has none. It also works out each rule's status.
- **`Reconciler`** (pure logic) compares the plan with the ledger and the live table: it adds what's missing and deletes owned routes that are no longer wanted.
- **`SteeringEngine`** runs the reconcile step through the IP Helper API (`CreateIpForwardEntry2` / `DeleteIpForwardEntry2`). It adds new routes before removing old ones, so moving a rule to another interface never leaves a gap. It re-runs on `NotifyRouteChange2` and on network address and availability events.
- **`ConnectionTracer`** reads TCP connections with their process IDs (`GetExtendedTcpTable`), asks Windows for each connection's route, and matches that route against the plan to find the rule.

The repository has three projects:

| Path | Contents |
|---|---|
| `src/RouteManager.Core` | Rule model, IP math, planner, reconciler, engine, storage, Rouler importer, native interop |
| `src/RouteManager.App` | WPF UI (MVVM), tray icon, elevation handling |
| `tests/RouteManager.Core.Tests` | xUnit tests for the core |

## Continuous integration

`.github/workflows/build.yml` runs on every push to `master` and on every pull request. It runs the tests and publishes `RouteManager.exe` as a build artifact.

To make a release, push a version tag:

```sh
git tag v1.0.0
git push origin v1.0.0
```

The tag's version is stamped into the exe, and a GitHub Release is created with the exe attached and notes generated from the commits. A tag with a suffix (`v1.1.0-beta.1`) becomes a pre-release.

## Tests

```sh
dotnet test
```

The tests cover address parsing and range-to-CIDR splitting, rule planning, reconciliation, the engine (run against an in-memory routing table), the Rouler importer, connection tracing, and the layout of the native routing structures.
