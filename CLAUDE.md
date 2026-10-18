# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

GeneralsOnline Game Services: an ASP.NET Core (.NET 10) backend that replaces the GameSpy services of Command & Conquer: Generals – Zero Hour (login, lobbies, quick match/matchmaking, rooms & chat, friends, stats, ladders, auto-update). Single project: `GenOnlineService/`, solution `GenServices.sln`. Licensed AGPL-3.0; source files carry the AGPL header.

## Commands

```bash
# Build (CI builds Debug + Release for win/linux/linux-musl/osx, x64 + arm64)
dotnet build GenOnlineService/GenOnlineService.csproj --configuration Debug
dotnet build GenOnlineService/GenOnlineService.csproj --configuration Release   # Release has TreatWarningsAsErrors=True

# Run (needs a configured appsettings.json and a reachable MySQL/MariaDB)
dotnet run --project GenOnlineService
```

There is no test project; CI's `dotnet test` step is a no-op. Verify changes by building in **Release** so warnings-as-errors are caught.

Local setup: fill in `GenOnlineService/appsettings.json` (JwtSettings key, Database, Core cert settings) and import `GenOnlineService/Database_Structure/structure.sql`. `data/GeoLite2-City.mmdb` (MaxMind) is optional; without it GeoIP lookups fall back to defaults. Kestrel endpoints come from `appsettings.json` (HTTPS :9000, HTTP :9001). Every setting can be overridden with an environment variable using `__` as the separator (e.g. `Core__lobby_tick_interval_ms=20`).

## Architecture

**Startup (`Program.cs`)** — wires everything by hand in `Main`: Sentry, S3 credentials, optional Discord bot, rate limiter (token bucket per user/IP), auth, response compression, EF Core pooled `IDbContextFactory<AppDbContext>` (MySQL via Pomelo, NoTracking by default), WebSockets. It then starts several self-rescheduling `System.Timers.Timer` loops that drive the real-time state:
- 5 ms (`Core:lobby_tick_interval_ms`): `LobbyManager.Tick()` + `WebSocketManager.Tick()`, and a separate timer for `LobbyManager.ProcessLobbiesNeedingDestroyed()`
- 1 s: `MatchmakingManager.Tick()`, `WebSocketManager.TickRoomMemberList()`
- 5 s cleanup, 60 s daily stats flush

`ThreadPool.SetMinThreads` is set from `Core:threadpool_min_threads` (default 200; 0 keeps the .NET default). Background code outside DI gets services through the static `ServiceLocator.Services`. Config is read via the static `Program.g_Config`; newer settings use a small static `Get(key, default)` accessor read on use (see `Core:reconnect_grace_period_ms` in `Constants.cs`).

**Auth** — default scheme `JwtOrBasic` forwards to JWT Bearer or a custom `BasicAuthenticationHandler` based on the `Authorization` header. JWTs are issued by `Program.JwtTokenGenerator` (login flow: `LoginCode` / `CheckLogin` / `LoginWithToken`, refresh via `RefreshToken`) and carry roles (`GameClient`, `ChatClient`, `GameLauncher`, `Monitor`, admin) plus a `client_id` claim and a `tgen` token-generation claim. `TokenRevocation.cs` persists per-user/session-type state in `user_token_state`: bumping the generation revokes all tokens, and refresh tokens are single-use (rotating `jti`). `TokenHelper` in `Program.cs` extracts user ID, roles, client, session type from the controller's principal. Policies `MonitorOrApiKey` / `AnyClientOrMonitorOrApiKey` also accept an `x-api-key` header validated against `API:keys`.

**HTTP API (`Controllers/`)** — one controller per folder, routed as `env/{environment}/contract/{contract_version}/[controller]`. Responses are subclasses of `APIResult` (defined in `Controllers/VersionCheck/VersionCheckController.cs`) that override `GetReturnType()`; follow the `RouteHandler_<VERB>_<Name>_Result` / `<VERB>_<Name>_Result` naming. Controllers take `LobbyManager` (singleton) and `IDbContextFactory<AppDbContext>` via constructor injection.

**WebSocket (`/env/{environment}/contract/{contract_version}/ws`, legacy `/ws` kept for old clients; `Controllers/WebSocket/WebSocketController.cs`)** — the persistent per-client channel used for lobby/room chat, signalling, presence, friends, and matchmaking notifications. Messages are JSON envelopes keyed by `msg_id` (`EWebSocketMessageID`); payload classes are `WebSocketMessage_*` in `Constants.cs`. Clients reconnect with the `is-reconnect` header and re-attach to cached sessions.

**In-memory state** — the authoritative live state is in memory, not the DB:
- `Constants.cs` (despite the name, ~3.2k lines) holds most shared runtime types: `WebSocketManager` (sessions keyed by user + session type, `SharedUserData`, `UserSession`, `UserWebSocketInstance`), presence, `PlayerStats`/`EStatIndex`, S3 and TURN credential managers, enums, and all WebSocket message classes.
- `LobbyManager.cs` — `Lobby`, `LobbyMember`, `LobbyManager` (DI singleton).
- `MatchmakingManager.cs` — quick-match buckets (static `MatchmakingManager.Tick`).
- `ELO.cs`, `LeaderboardManager.cs`, `Discord.cs` (Discord.Net bot bridging room chat and admin commands), `Moderation.cs`.
- `ExternalLeaderboardsClient.cs` + `ExternalLeaderboardPublicationQueue.cs` post match results to an external leaderboard API (`ExternalLeaderboards` config) with retries tracked in `external_publication`.

**Database (`Database/`)** — `AppDbContext` in `Database.cs`; each `Database.<Area>.cs` file contains the entity class(es), an `IEntityTypeConfiguration<T>` mapping, and a static helper class under `Database.*` (e.g. `Database.Users`, `Database.AntiCheat`) with async query methods taking an `AppDbContext`. There are no EF migrations: the schema lives in `Database_Structure/structure.sql`, so schema changes must update both that file and the entity configuration, plus add an idempotent `upgrade_<date>_<name>.sql` for existing databases. MySQL access is through Pomelo on the `MySqlConnector` driver. New entities must be added as a `DbSet` and registered in `OnModelCreating`.

**Data files (`GenOnlineService/data/`)** — runtime-loaded JSON/text read from the working directory: `rooms.json`, `serviceconfig.json`, `patchdata.json`, `motd.txt`, UptimeRobot IP allowlist.

## Conventions

- Tabs for indentation, Allman braces, Hungarian-ish prefixes (`strX`, `bX`, `m_`, `g_`, `lstX`, `dictX`) and `E`-prefixed enums, matching the original game codebase style.
- Pending EF Core migration work is marked with `TODO_EFCORE`.
