/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

// Self-hosted login for a private instance. The public service hands the login
// code to its website, which authenticates the player through Discord or Steam
// and then calls the LoginCode endpoint with a web server key. A private
// instance has no such website, so this stands in for it: /login redirects the
// player to Discord, and /login/discord/callback identifies them and completes
// the pending login in process.
//
// The display name comes from radarvan, which maps a Discord ID to the name the
// player's matches are already recorded under, so stats stay attached to one
// name across the stack. Players radarvan does not know fall back to their
// Discord name.
//
// Off unless SelfServeLogin:enabled is true in the config, and it needs a
// Discord OAuth client id and secret, so an instance that does not opt in is
// unchanged.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public static class SelfServeLoginThrottle
	{
		private const int MaxAttemptsPerWindow = 20;
		private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

		private sealed class Bucket
		{
			public int Count;
			public DateTime WindowStart = DateTime.UtcNow;
		}

		private static readonly ConcurrentDictionary<string, Bucket> s_buckets = new();

		public static bool Allow(string key)
		{
			Bucket bucket = s_buckets.GetOrAdd(key, _ => new Bucket());
			lock (bucket)
			{
				DateTime now = DateTime.UtcNow;
				if (now - bucket.WindowStart >= Window)
				{
					bucket.WindowStart = now;
					bucket.Count = 0;
				}

				bucket.Count++;
				return bucket.Count <= MaxAttemptsPerWindow;
			}
		}
	}

	// Ties the OAuth round trip back to the game's login code. The state value is
	// random and single use, so a callback cannot be replayed or pointed at someone
	// else's pending login.
	public static class SelfServeLoginStateStore
	{
		private sealed class Entry
		{
			public string GameCode = String.Empty;
			public DateTime Created = DateTime.UtcNow;
		}

		// same lifetime as the pending login it is carrying, there is no point
		// outliving it
		private static readonly TimeSpan Lifetime = PendingLoginManager.PendingLoginLifetime;

		private static readonly ConcurrentDictionary<string, Entry> s_states = new();

		public static string Issue(string gameCode)
		{
			Cleanup();

			string state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
			s_states[state] = new Entry { GameCode = gameCode };
			return state;
		}

		// returns null if the state is unknown, expired, or already used
		public static string? Consume(string? state)
		{
			if (String.IsNullOrEmpty(state))
			{
				return null;
			}

			if (!s_states.TryRemove(state, out Entry? entry) || entry == null)
			{
				return null;
			}

			if (DateTime.UtcNow - entry.Created >= Lifetime)
			{
				return null;
			}

			return entry.GameCode;
		}

		private static void Cleanup()
		{
			DateTime utcNow = DateTime.UtcNow;
			foreach (var kvp in s_states)
			{
				if (utcNow - kvp.Value.Created >= Lifetime)
				{
					s_states.TryRemove(kvp);
				}
			}
		}
	}

	[AllowAnonymous]
	[Route("login")]
	public class SelfServeLoginController : ControllerBase
	{
		private const int MaxDisplayNameLength = 20;
		private const int MinDisplayNameLength = 3;

		// discord_username is varchar(32)
		private const int MaxDiscordUsernameLength = 32;

		private const string DiscordDefaultBaseURL = "https://discord.com";

		// One shared client. A new HttpClient per login re-resolves DNS and leaks
		// sockets under any real login rate.
		private static readonly Lazy<HttpClient> s_httpClient = new Lazy<HttpClient>(() =>
		{
			var handler = new SocketsHttpHandler
			{
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2)
			};

			return new HttpClient(handler)
			{
				Timeout = TimeSpan.FromSeconds(10)
			};
		}, true);

		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public SelfServeLoginController(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		private static IConfigurationSection? Config()
		{
			return Program.g_Config?.GetSection("SelfServeLogin");
		}

		private static bool IsEnabled()
		{
			return Config()?.GetValue<bool>("enabled") ?? false;
		}

		private static string Setting(string key)
		{
			return Config()?.GetValue<string>(key) ?? String.Empty;
		}

		private static string DiscordClientID()
		{
			return Setting("discord_client_id");
		}

		private static string DiscordClientSecret()
		{
			return Setting("discord_client_secret");
		}

		// Points at Discord itself unless a lab stubs it out.
		private static string DiscordBaseURL()
		{
			string configured = Setting("discord_base_url");
			return String.IsNullOrWhiteSpace(configured) ? DiscordDefaultBaseURL : configured.TrimEnd('/');
		}

		private static string DiscordAuthorizeEndpoint()
		{
			return DiscordBaseURL() + "/oauth2/authorize";
		}

		private static string DiscordTokenEndpoint()
		{
			return DiscordBaseURL() + "/api/oauth2/token";
		}

		private static string DiscordUserEndpoint()
		{
			return DiscordBaseURL() + "/api/users/@me";
		}

		private static string RadarvanBaseURL()
		{
			string configured = Setting("radarvan_base_url");
			return String.IsNullOrWhiteSpace(configured) ? "https://www.radarvan.com" : configured.TrimEnd('/');
		}

		private static string RadarvanAPIKey()
		{
			return Setting("radarvan_api_key");
		}

		// Must match a redirect URI registered on the Discord application exactly.
		// Derived from the request when not configured, which needs the forwarded
		// headers to be trusted so the public scheme/host/prefix survive the proxy.
		private string RedirectURI()
		{
			string configured = Setting("discord_redirect_uri");
			if (!String.IsNullOrWhiteSpace(configured))
			{
				return configured;
			}

			return $"{Request.Scheme}://{Request.Host}{Request.PathBase}/login/discord/callback";
		}

		private static bool IsConfigured()
		{
			return !String.IsNullOrWhiteSpace(DiscordClientID()) && !String.IsNullOrWhiteSpace(DiscordClientSecret());
		}

		private static string Escape(string? value)
		{
			return WebUtility.HtmlEncode(value ?? String.Empty);
		}

		private static string Page(string body)
		{
			StringBuilder sb = new();
			sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">");
			sb.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
			sb.Append("<title>Sign in to play</title><style>");
			sb.Append("body{background:#14170f;color:#e8e6dc;font:16px/1.6 system-ui,sans-serif;display:flex;justify-content:center;padding:3rem 1rem}");
			sb.Append("main{width:22rem}h1{font-size:1.3rem;margin:0 0 1.2rem}");
			sb.Append("a.btn{display:inline-block;padding:.6rem 1.2rem;background:#5865f2;color:#fff;text-decoration:none;font:inherit}");
			sb.Append("p.err{color:#e08076}p.ok{color:#8fbf7f}code{color:#d7a742}");
			sb.Append("</style></head><body><main>");
			sb.Append(body);
			sb.Append("</main></body></html>");
			return sb.ToString();
		}

		private ContentResult Html(string body, HttpStatusCode status = HttpStatusCode.OK)
		{
			Response.StatusCode = (int)status;
			return new ContentResult
			{
				Content = Page(body),
				ContentType = "text/html; charset=utf-8",
				StatusCode = (int)status
			};
		}

		private ContentResult Error(string message, HttpStatusCode status)
		{
			return Html($"<h1>Sign in</h1><p class=\"err\">{Escape(message)}</p>", status);
		}

		[HttpGet]
		public IActionResult Get([FromQuery] string? gamecode)
		{
			if (!IsEnabled())
			{
				return Error("Self serve login is not enabled on this server.", HttpStatusCode.NotFound);
			}

			if (!IsConfigured())
			{
				return Error("This server has no Discord application configured, so nobody can sign in yet.", HttpStatusCode.ServiceUnavailable);
			}

			string code = (gamecode ?? String.Empty).Trim();
			if (String.IsNullOrEmpty(code))
			{
				return Error("This page needs the login code from the game. Click LOGIN in the game and it will open this page for you.", HttpStatusCode.BadRequest);
			}

			if (PendingLoginManager.GetPendingLogin(code) == null)
			{
				return Error("That login code has expired. Click LOGIN in the game again.", HttpStatusCode.NotFound);
			}

			string state = SelfServeLoginStateStore.Issue(code);

			string authorizeURL = String.Concat(
				DiscordAuthorizeEndpoint(),
				"?client_id=", Uri.EscapeDataString(DiscordClientID()),
				"&response_type=code",
				"&scope=identify",
				"&redirect_uri=", Uri.EscapeDataString(RedirectURI()),
				"&state=", Uri.EscapeDataString(state));

			return Redirect(authorizeURL);
		}

		[HttpGet("discord/callback")]
		public async Task<IActionResult> DiscordCallback([FromQuery] string? code, [FromQuery] string? state, [FromQuery(Name = "error")] string? oauthError)
		{
			if (!IsEnabled())
			{
				return Error("Self serve login is not enabled on this server.", HttpStatusCode.NotFound);
			}

			if (!IsConfigured())
			{
				return Error("This server has no Discord application configured, so nobody can sign in yet.", HttpStatusCode.ServiceUnavailable);
			}

			string throttleKey = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
			if (!SelfServeLoginThrottle.Allow(throttleKey))
			{
				return Error("Too many attempts. Wait a few minutes and try again.", HttpStatusCode.TooManyRequests);
			}

			if (!String.IsNullOrEmpty(oauthError))
			{
				return Error("Discord did not authorize the sign in. Click LOGIN in the game to try again.", HttpStatusCode.Forbidden);
			}

			// consumes the state whatever happens next, so a callback is single use
			string? gameCode = SelfServeLoginStateStore.Consume(state);
			if (gameCode == null)
			{
				return Error("This sign in link is no longer valid. Click LOGIN in the game again.", HttpStatusCode.BadRequest);
			}

			if (String.IsNullOrEmpty(code))
			{
				return Error("Discord did not return an authorization code. Click LOGIN in the game to try again.", HttpStatusCode.BadRequest);
			}

			if (PendingLoginManager.GetPendingLogin(gameCode) == null)
			{
				return Error("That login code has expired. Click LOGIN in the game again.", HttpStatusCode.NotFound);
			}

			DiscordIdentity? identity = await FetchDiscordIdentity(code, RedirectURI());
			if (identity == null)
			{
				return Error("Could not confirm the sign in with Discord. Click LOGIN in the game to try again.", HttpStatusCode.BadGateway);
			}

			// radarvan owns the mapping from a Discord account to the name matches are
			// recorded under. A player it does not know keeps their Discord name.
			string? radarvanName = await FetchRadarvanPlayerName(identity.ID);
			bool bNameFromRadarvan = !String.IsNullOrWhiteSpace(radarvanName);

			string resolvedName = SanitizeDisplayName(bNameFromRadarvan ? radarvanName : identity.PreferredName, identity.ID);

			await using AppDbContext db = await _dbFactory.CreateDbContextAsync();

			User? user = await db.Users.FirstOrDefaultAsync(u => u.DiscordID == identity.ID);

			if (user == null && bNameFromRadarvan)
			{
				// radarvan says this Discord account already plays under this name, so an
				// unlinked account holding it is the same person. Adopt it rather than
				// stranding its history behind a duplicate row. Only done for a radarvan
				// name: adopting on a self chosen Discord name would let anyone claim an
				// existing account by renaming themselves.
				user = await db.Users.FirstOrDefaultAsync(u => u.DisplayName == resolvedName && (u.DiscordID == null || u.DiscordID <= 0));
			}

			if (user != null && user.IsBanned)
			{
				return Error("That account is banned on this server.", HttpStatusCode.Forbidden);
			}

			string discordUsername = Truncate(identity.Username, MaxDiscordUsernameLength);

			if (user == null)
			{
				user = new User
				{
					AccountType = EAccountType.Discord,
					DiscordID = identity.ID,
					DiscordUsername = discordUsername,
					DisplayName = await MakeNameUnique(db, resolvedName, -1),
					LastLogin = DateTime.UtcNow
				};

				db.Users.Add(user);
				await db.SaveChangesAsync();
			}
			else
			{
				string displayName = user.DisplayName ?? String.Empty;

				// A radarvan name is authoritative, so keep it in sync on every login. A
				// Discord name is only a fallback, so it must not overwrite a name the
				// account already has, or a radarvan outage would rename the player.
				if ((bNameFromRadarvan || String.IsNullOrWhiteSpace(displayName)) && displayName != resolvedName)
				{
					displayName = await MakeNameUnique(db, resolvedName, user.ID);
				}

				// The context is NoTracking, so assigning to the entity would save nothing.
				Int64 userID = user.ID;
				await db.Users.Where(u => u.ID == userID).ExecuteUpdateAsync(setters => setters
					.SetProperty(u => u.AccountType, EAccountType.Discord)
					.SetProperty(u => u.DiscordID, identity.ID)
					.SetProperty(u => u.DiscordUsername, discordUsername)
					.SetProperty(u => u.DisplayName, displayName)
					.SetProperty(u => u.LastLogin, DateTime.UtcNow));

				user.DisplayName = displayName;
			}

			if (!PendingLoginManager.UpdatePendingLogin(gameCode, EPendingLoginState.LoginSuccess, user.ID))
			{
				return Error("That login code has already been used. Click LOGIN in the game again.", HttpStatusCode.Conflict);
			}

			return Html($"<h1>Signed in</h1><p class=\"ok\">Welcome, <code>{Escape(user.DisplayName)}</code>.</p><p>Go back to the game. You can close this page.</p>");
		}

		private sealed class DiscordIdentity
		{
			public Int64 ID;
			public string Username = String.Empty;
			public string? GlobalName;

			// what Discord shows other people: the display name when they have set one,
			// otherwise the handle
			public string PreferredName => String.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName!;
		}

		private static async Task<DiscordIdentity?> FetchDiscordIdentity(string authorizationCode, string redirectURI)
		{
			try
			{
				HttpClient client = s_httpClient.Value;

				using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, DiscordTokenEndpoint())
				{
					Content = new FormUrlEncodedContent(new Dictionary<string, string>
					{
						["client_id"] = DiscordClientID(),
						["client_secret"] = DiscordClientSecret(),
						["grant_type"] = "authorization_code",
						["code"] = authorizationCode,
						["redirect_uri"] = redirectURI
					})
				};

				using HttpResponseMessage tokenResponse = await client.SendAsync(tokenRequest);
				if (!tokenResponse.IsSuccessStatusCode)
				{
					Console.WriteLine($"[WARNING] SelfServeLogin: Discord token exchange failed with {(int)tokenResponse.StatusCode}");
					return null;
				}

				string tokenBody = await tokenResponse.Content.ReadAsStringAsync();
				using JsonDocument tokenDoc = JsonDocument.Parse(tokenBody);
				if (!tokenDoc.RootElement.TryGetProperty("access_token", out JsonElement accessTokenElem))
				{
					Console.WriteLine("[WARNING] SelfServeLogin: Discord token response had no access_token");
					return null;
				}

				string? accessToken = accessTokenElem.GetString();
				if (String.IsNullOrEmpty(accessToken))
				{
					return null;
				}

				using var userRequest = new HttpRequestMessage(HttpMethod.Get, DiscordUserEndpoint());
				userRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

				using HttpResponseMessage userResponse = await client.SendAsync(userRequest);
				if (!userResponse.IsSuccessStatusCode)
				{
					Console.WriteLine($"[WARNING] SelfServeLogin: Discord user lookup failed with {(int)userResponse.StatusCode}");
					return null;
				}

				string userBody = await userResponse.Content.ReadAsStringAsync();
				using JsonDocument userDoc = JsonDocument.Parse(userBody);
				JsonElement root = userDoc.RootElement;

				// snowflakes come back as strings
				if (!root.TryGetProperty("id", out JsonElement idElem)
					|| !Int64.TryParse(idElem.GetString(), out Int64 discordID)
					|| discordID <= 0)
				{
					Console.WriteLine("[WARNING] SelfServeLogin: Discord user response had no usable id");
					return null;
				}

				return new DiscordIdentity
				{
					ID = discordID,
					Username = root.TryGetProperty("username", out JsonElement nameElem) ? (nameElem.GetString() ?? String.Empty) : String.Empty,
					GlobalName = root.TryGetProperty("global_name", out JsonElement globalElem) && globalElem.ValueKind == JsonValueKind.String ? globalElem.GetString() : null
				};
			}
			catch (Exception e)
			{
				Console.WriteLine($"[WARNING] SelfServeLogin: Discord sign in threw: {e.Message}");
				return null;
			}
		}

		// null when radarvan does not know this Discord account, or is unreachable. A
		// radarvan outage must not block a login, it just costs the nicer name.
		private static async Task<string?> FetchRadarvanPlayerName(Int64 discordID)
		{
			try
			{
				string url = $"{RadarvanBaseURL()}/api/v1/players/by_discord_id/{discordID}";

				using var request = new HttpRequestMessage(HttpMethod.Get, url);

				string apiKey = RadarvanAPIKey();
				if (!String.IsNullOrWhiteSpace(apiKey))
				{
					request.Headers.TryAddWithoutValidation("X-API-Key", apiKey);
				}

				using HttpResponseMessage response = await s_httpClient.Value.SendAsync(request);

				if (response.StatusCode == HttpStatusCode.NotFound)
				{
					// radarvan has no name for this player, which is normal for a newcomer
					return null;
				}

				if (!response.IsSuccessStatusCode)
				{
					Console.WriteLine($"[WARNING] SelfServeLogin: radarvan name lookup failed with {(int)response.StatusCode}");
					return null;
				}

				string body = await response.Content.ReadAsStringAsync();
				using JsonDocument doc = JsonDocument.Parse(body);

				if (doc.RootElement.TryGetProperty("playerName", out JsonElement nameElem) && nameElem.ValueKind == JsonValueKind.String)
				{
					string? name = nameElem.GetString();
					return String.IsNullOrWhiteSpace(name) ? null : name;
				}

				return null;
			}
			catch (Exception e)
			{
				Console.WriteLine($"[WARNING] SelfServeLogin: radarvan name lookup threw: {e.Message}");
				return null;
			}
		}

		private static string Truncate(string? value, int maxLength)
		{
			string text = value ?? String.Empty;
			return text.Length <= maxLength ? text : text.Substring(0, maxLength);
		}

		// Discord names allow spaces and the whole of unicode, the game's name field
		// does not, so keep the characters the game accepts and fall back to something
		// stable when nothing usable survives.
		private static string SanitizeDisplayName(string? raw, Int64 discordID)
		{
			StringBuilder sb = new();

			foreach (char c in raw ?? String.Empty)
			{
				if (sb.Length >= MaxDisplayNameLength)
				{
					break;
				}

				if (Char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-' || c == '[' || c == ']')
				{
					sb.Append(c);
				}
				else if (c == ' ')
				{
					sb.Append('_');
				}
			}

			string name = sb.ToString().Trim('_');

			if (name.Length < MinDisplayNameLength)
			{
				string digits = discordID.ToString();
				name = "Player" + digits.Substring(Math.Max(0, digits.Length - 6));
			}

			return Truncate(name, MaxDisplayNameLength);
		}

		// Display names are how players tell each other apart in a lobby, so two
		// accounts must not share one. Suffix until free.
		private static async Task<string> MakeNameUnique(AppDbContext db, string desiredName, Int64 excludeUserID)
		{
			string candidate = desiredName;

			for (int suffix = 2; suffix < 1000; ++suffix)
			{
				bool bTaken = await db.Users.AnyAsync(u => u.DisplayName == candidate && u.ID != excludeUserID);
				if (!bTaken)
				{
					return candidate;
				}

				string tail = "_" + suffix.ToString();
				candidate = Truncate(desiredName, MaxDisplayNameLength - tail.Length) + tail;
			}

			return candidate;
		}
	}
}
