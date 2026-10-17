using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using GenOnlineService.Controllers;
using Microsoft.Extensions.Configuration;

namespace GenOnlineService
{
	// Player stats from radarvan.
	//
	// radarvan (the community stats server) already holds every match the Zulu
	// clients upload, so the per-general win/loss record it computes is far more
	// complete than the counters the GO client PUTs after each game on this
	// service. RadarvanStatsWorker polls the whole community table every few
	// minutes (it is one 10 KB response) and RadarvanStats.Apply overwrites the
	// per-general wins/losses/games and the current streak in the PlayerStats
	// object the client already understands. Nothing on the wire changes.
	//
	// Identity is the display name: self-serve login already makes it the
	// radarvan canonical name through /api/v1/players/by_discord_id/. A name
	// radarvan does not know (or one below its competitive-games floor) is left
	// exactly as this service built it, never zeroed.
	//
	// The radarvan rating (OpenSkill ordinal) is deliberately not carried over.

	public sealed class RadarvanPlayerRecord
	{
		public string Name { get; set; } = "";

		// Indexed by radarvan general id, 0 USA .. 11 Demo.
		public int[] Wins { get; } = new int[RadarvanStats.NumGenerals];
		public int[] Losses { get; } = new int[RadarvanStats.NumGenerals];

		public int TotalWins { get; set; }
		public int TotalLosses { get; set; }

		// From the rating table's last-10 form, when the player is rated.
		public bool HasForm { get; set; }
		public int CurrentWinStreak { get; set; }
		public int CurrentLossStreak { get; set; }
	}

	public sealed class RadarvanSnapshot
	{
		public Dictionary<string, RadarvanPlayerRecord> Players { get; } = new(StringComparer.OrdinalIgnoreCase);

		// Community per-side totals in DailyStatsStructure layout (slot = player
		// template index - 2), or null when /api/generalstats was not fetched.
		public int[]? GlobalMatches { get; set; }
		public int[]? GlobalWins { get; set; }

		public int FormCount { get; set; }
		public DateTime FetchedUtc { get; set; } = DateTime.MinValue;

		public bool IsEmpty => Players.Count == 0 && GlobalMatches == null;
	}

	public static class RadarvanStats
	{
		public const int NumGenerals = 12;

		// radarvan general id -> player template index, which is what the client's
		// per-general arrays (PlayerStats.wins[] etc.) are indexed by. Inverse of
		// templateIndexToGeneral() in the client's StatsUploader.cpp.
		//                                                 USA Air Las Sup Chi Nuk Tnk Inf GLA Tox Ste Dem
		public static readonly int[] GeneralToTemplate = { 2,  7,  6,  5,  3,  10, 8,  9,  4,  11, 13, 12 };

		private static RadarvanSnapshot s_snapshot = new();

		public static RadarvanSnapshot Snapshot => Volatile.Read(ref s_snapshot);

		public static void SetSnapshot(RadarvanSnapshot snapshot)
		{
			Volatile.Write(ref s_snapshot, snapshot);
		}

		// ---- configuration -------------------------------------------------

		private static IConfigurationSection? Config()
		{
			return Program.g_Config?.GetSection("RadarvanStats");
		}

		private static string Setting(string key, string fallbackSection, string fallbackKey)
		{
			string? value = Config()?.GetValue<string>(key);
			if (!String.IsNullOrWhiteSpace(value))
				return value;

			// The self-serve login already carries the radarvan base URL and key.
			return Program.g_Config?.GetSection(fallbackSection)?.GetValue<string>(fallbackKey) ?? String.Empty;
		}

		public static bool IsEnabled => Config()?.GetValue<bool>("enabled") ?? false;

		public static string BaseUrl
		{
			get
			{
				string configured = Setting("base_url", "SelfServeLogin", "radarvan_base_url");
				return String.IsNullOrWhiteSpace(configured) ? "https://www.radarvan.com" : configured.TrimEnd('/');
			}
		}

		public static string ApiKey => Setting("api_key", "SelfServeLogin", "radarvan_api_key");

		public static bool IsConfigured => IsEnabled && !String.IsNullOrWhiteSpace(ApiKey);

		public static TimeSpan RefreshInterval
		{
			get
			{
				int seconds = Config()?.GetValue<int?>("refresh_seconds") ?? 300;
				return TimeSpan.FromSeconds(Math.Max(60, seconds));
			}
		}

		private static int s_loggedNotConfigured = 0;

		public static void LogNotConfiguredOnce()
		{
			if (Interlocked.Exchange(ref s_loggedNotConfigured, 1) == 0)
				Console.WriteLine("[INFO] RadarvanStats is not configured (enabled + api_key); player stats come from this service's own counters only.");
		}

		// ---- applying a record to the wire object ---------------------------

		public static bool TryGet(string? displayName, out RadarvanPlayerRecord record)
		{
			record = null!;
			if (String.IsNullOrWhiteSpace(displayName))
				return false;
			return Snapshot.Players.TryGetValue(displayName.Trim(), out record!);
		}

		/// <summary>
		/// Overwrites the per-general wins/losses/games and the current streak
		/// from radarvan. Returns false (and changes nothing) when radarvan has no
		/// record for this name.
		/// </summary>
		public static bool Apply(PlayerStats ps, string? displayName)
		{
			if (ps == null || !TryGet(displayName, out RadarvanPlayerRecord record))
				return false;

			Apply(ps, record);
			return true;
		}

		public static void Apply(PlayerStats ps, RadarvanPlayerRecord record)
		{
			for (int g = 0; g < NumGenerals; ++g)
			{
				int t = GeneralToTemplate[g];
				if (t < 0 || t >= ps.wins.Length)
					continue;

				ps.wins[t] = record.Wins[g];
				ps.losses[t] = record.Losses[g];
				ps.games[t] = record.Wins[g] + record.Losses[g];
			}

			if (record.HasForm)
			{
				ps.winsInARow = record.CurrentWinStreak;
				ps.lossesInARow = record.CurrentLossStreak;
			}
		}

		// ---- parsing -------------------------------------------------------

		/// <summary>Current win and loss streaks from a form list, oldest first.</summary>
		public static (int wins, int losses) CurrentStreaks(IReadOnlyList<bool> form)
		{
			int wins = 0;
			int losses = 0;
			for (int i = form.Count - 1; i >= 0; --i)
			{
				if (form[i])
				{
					if (losses > 0) break;
					++wins;
				}
				else
				{
					if (wins > 0) break;
					++losses;
				}
			}
			return (wins, losses);
		}

		/// <summary>
		/// Builds a snapshot from the raw bodies of /api/playerstats (required),
		/// /api/player_ratings/ (optional, for streaks) and /api/generalstats
		/// (optional, for the welcome screen side percentages).
		/// </summary>
		public static RadarvanSnapshot Parse(string playerStatsJson, string? ratingsJson, string? generalStatsJson)
		{
			RadarvanSnapshot snapshot = new();

			using (JsonDocument doc = JsonDocument.Parse(playerStatsJson))
			{
				if (!doc.RootElement.TryGetProperty("playerStats", out JsonElement players) || players.ValueKind != JsonValueKind.Array)
					throw new FormatException("playerstats: no playerStats array");

				foreach (JsonElement player in players.EnumerateArray())
				{
					string? name = player.TryGetProperty("playerName", out JsonElement nameElem) ? nameElem.GetString() : null;
					if (String.IsNullOrWhiteSpace(name))
						continue;

					RadarvanPlayerRecord record = new() { Name = name.Trim() };

					if (player.TryGetProperty("stats", out JsonElement stats) && stats.ValueKind == JsonValueKind.Object)
					{
						foreach (JsonProperty entry in stats.EnumerateObject())
						{
							if (!Int32.TryParse(entry.Name, out int general) || general < 0 || general >= NumGenerals)
								continue;

							int wins = entry.Value.TryGetProperty("wins", out JsonElement w) ? w.GetInt32() : 0;
							int losses = entry.Value.TryGetProperty("losses", out JsonElement l) ? l.GetInt32() : 0;
							record.Wins[general] = wins;
							record.Losses[general] = losses;
							record.TotalWins += wins;
							record.TotalLosses += losses;
						}
					}

					snapshot.Players[record.Name] = record;
				}
			}

			if (!String.IsNullOrWhiteSpace(ratingsJson))
			{
				using JsonDocument doc = JsonDocument.Parse(ratingsJson);
				if (doc.RootElement.TryGetProperty("player_form", out JsonElement forms) && forms.ValueKind == JsonValueKind.Object)
				{
					foreach (JsonProperty entry in forms.EnumerateObject())
					{
						if (entry.Value.ValueKind != JsonValueKind.Array)
							continue;
						if (!snapshot.Players.TryGetValue(entry.Name, out RadarvanPlayerRecord? record))
							continue;

						List<bool> form = new();
						foreach (JsonElement b in entry.Value.EnumerateArray())
						{
							if (b.ValueKind == JsonValueKind.True) form.Add(true);
							else if (b.ValueKind == JsonValueKind.False) form.Add(false);
						}
						if (form.Count == 0)
							continue;

						(record.CurrentWinStreak, record.CurrentLossStreak) = CurrentStreaks(form);
						record.HasForm = true;
						++snapshot.FormCount;
					}
				}
			}

			if (!String.IsNullOrWhiteSpace(generalStatsJson))
			{
				using JsonDocument doc = JsonDocument.Parse(generalStatsJson);
				if (doc.RootElement.TryGetProperty("general_stats", out JsonElement generals) && generals.ValueKind == JsonValueKind.Array)
				{
					int[] matches = new int[DailyStatsStructure.numSides];
					int[] wins = new int[DailyStatsStructure.numSides];
					bool any = false;

					foreach (JsonElement entry in generals.EnumerateArray())
					{
						if (!entry.TryGetProperty("general", out JsonElement gElem) || !gElem.TryGetInt32(out int general))
							continue;
						if (general < 0 || general >= NumGenerals)
							continue;
						if (!entry.TryGetProperty("total", out JsonElement total))
							continue;

						int w = total.TryGetProperty("wins", out JsonElement we) ? we.GetInt32() : 0;
						int l = total.TryGetProperty("losses", out JsonElement le) ? le.GetInt32() : 0;

						// DailyStatsManager.RegisterOutcome stores slot = template - 2.
						int slot = GeneralToTemplate[general] - 2;
						if (slot < 0 || slot >= DailyStatsStructure.numSides)
							continue;

						matches[slot] = w + l;
						wins[slot] = w;
						any = true;
					}

					if (any)
					{
						snapshot.GlobalMatches = matches;
						snapshot.GlobalWins = wins;
					}
				}
			}

			snapshot.FetchedUtc = DateTime.UtcNow;
			return snapshot;
		}
	}

	/// <summary>
	/// Polls radarvan on a fixed interval, swaps in the parsed snapshot and
	/// re-applies it to every online user's cached PlayerStats.
	/// </summary>
	public sealed class RadarvanStatsWorker : BackgroundService
	{
		private static readonly TimeSpan c_MaxBackoff = TimeSpan.FromMinutes(30);
		private static readonly TimeSpan c_HttpTimeout = TimeSpan.FromSeconds(20);

		private static readonly Lazy<HttpClient> s_httpClient = new Lazy<HttpClient>(() =>
		{
			var handler = new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
			};
			return new HttpClient(handler, disposeHandler: true) { Timeout = c_HttpTimeout };
		}, LazyThreadSafetyMode.ExecutionAndPublication);

		private int m_consecutiveFailures = 0;

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			if (!RadarvanStats.IsConfigured)
			{
				RadarvanStats.LogNotConfiguredOnce();
				return;
			}

			Console.WriteLine($"[INFO] RadarvanStats: polling {RadarvanStats.BaseUrl} every {RadarvanStats.RefreshInterval.TotalSeconds:F0}s");

			while (!stoppingToken.IsCancellationRequested)
			{
				TimeSpan delay = RadarvanStats.RefreshInterval;

				try
				{
					await RefreshOnce(stoppingToken);
					m_consecutiveFailures = 0;
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					++m_consecutiveFailures;
					double factor = Math.Pow(2, Math.Min(m_consecutiveFailures, 6));
					delay = TimeSpan.FromTicks(Math.Min(c_MaxBackoff.Ticks, (long)(RadarvanStats.RefreshInterval.Ticks * factor)));

					// Keep the previous snapshot; a radarvan outage costs freshness, nothing else.
					if (m_consecutiveFailures == 1 || m_consecutiveFailures % 10 == 0)
						Console.WriteLine($"[WARNING] RadarvanStats refresh failed ({m_consecutiveFailures} in a row, next try in {delay.TotalSeconds:F0}s): {ex.Message}");
				}

				try
				{
					await Task.Delay(delay, stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
			}
		}

		private async Task RefreshOnce(CancellationToken stoppingToken)
		{
			Stopwatch sw = Stopwatch.StartNew();

			// Serial on purpose; radarvan is a single dyno.
			string playerStats = await Fetch("/api/playerstats", stoppingToken)
				?? throw new HttpRequestException("playerstats unavailable");
			string? ratings = await Fetch("/api/player_ratings/", stoppingToken);
			string? generalStats = await Fetch("/api/generalstats", stoppingToken);

			RadarvanSnapshot snapshot = RadarvanStats.Parse(playerStats, ratings, generalStats);
			sw.Stop();

			if (snapshot.Players.Count == 0)
				throw new FormatException("playerstats parsed to zero players");

			RadarvanStats.SetSnapshot(snapshot);

			int applied = 0;
			int online = 0;
			WebSocketManager.ForEachSharedUserData(userData =>
			{
				++online;
				if (userData.GameStats != null && RadarvanStats.Apply(userData.GameStats, userData.m_strDisplayName))
					++applied;
			});

			Console.WriteLine($"[INFO] RadarvanStats: {snapshot.Players.Count} players, {snapshot.FormCount} with form, global side stats {(snapshot.GlobalMatches != null ? "yes" : "no")}, fetched in {sw.ElapsedMilliseconds}ms; applied to {applied}/{online} online users");
		}

		// Returns null for a non-200 or non-JSON answer so the caller can decide
		// which calls are optional; throws only on transport failure of the request itself.
		private static async Task<string?> Fetch(string path, CancellationToken stoppingToken)
		{
			string url = RadarvanStats.BaseUrl + path;

			using var request = new HttpRequestMessage(HttpMethod.Get, url);
			request.Headers.TryAddWithoutValidation("X-API-Key", RadarvanStats.ApiKey);
			request.Headers.TryAddWithoutValidation("Accept", "application/json");

			using HttpResponseMessage response = await s_httpClient.Value.SendAsync(request, stoppingToken);
			if (response.StatusCode != HttpStatusCode.OK)
			{
				Console.WriteLine($"[WARNING] RadarvanStats: GET {path} returned {(int)response.StatusCode}");
				return null;
			}

			string body = await response.Content.ReadAsStringAsync(stoppingToken);

			// Heroku error pages are HTML with a 200 from the router on occasion.
			string trimmed = body.TrimStart();
			if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
			{
				Console.WriteLine($"[WARNING] RadarvanStats: GET {path} returned a non-JSON body ({body.Length} bytes)");
				return null;
			}

			return body;
		}
	}
}
