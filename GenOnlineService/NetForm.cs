using Microsoft.EntityFrameworkCore;
using System.Data.Common;

namespace GenOnlineService
{
	// Each player's network and PC form over their last 5 games, for the in-game
	// profile. cncstats netmetrics computes it from the uploaded client logs into
	// zulumetrics.player_form (same MariaDB server) every 5 minutes; this keeps a
	// copy keyed by display name and hands the JSON through untouched.
	public static class NetForm
	{
		private static volatile Dictionary<string, string> s_byName = new(StringComparer.OrdinalIgnoreCase);

		public static string? Get(string? displayName)
		{
			if (string.IsNullOrEmpty(displayName))
				return null;
			return s_byName.TryGetValue(displayName, out string? form) ? form : null;
		}

		public static void Set(Dictionary<string, string> byName) => s_byName = byName;
	}

	public class NetFormWorker : BackgroundService
	{
		private static readonly TimeSpan c_Refresh = TimeSpan.FromMinutes(5);
		private readonly IDbContextFactory<AppDbContext> _dbFactory;
		private bool m_warned = false;

		public NetFormWorker(IDbContextFactory<AppDbContext> dbFactory)
		{
			_dbFactory = dbFactory;
		}

		protected override async Task ExecuteAsync(CancellationToken stoppingToken)
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					await using var db = await _dbFactory.CreateDbContextAsync(stoppingToken);
					DbConnection conn = db.Database.GetDbConnection();
					await conn.OpenAsync(stoppingToken);
					await using DbCommand cmd = conn.CreateCommand();
					cmd.CommandText = "SELECT name, form FROM zulumetrics.player_form";
					var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
					await using (DbDataReader reader = await cmd.ExecuteReaderAsync(stoppingToken))
					{
						while (await reader.ReadAsync(stoppingToken))
							byName[reader.GetString(0)] = reader.GetString(1);
					}
					NetForm.Set(byName);
					if (m_warned)
						Console.WriteLine($"[INFO] NetForm: back, {byName.Count} players");
					m_warned = false;
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
				catch (Exception ex)
				{
					// No netmetrics on this box (a lab) or no grant yet: profiles just show no form.
					if (!m_warned)
						Console.WriteLine($"[WARNING] NetForm: zulumetrics.player_form unavailable, profiles show no form: {ex.Message}");
					m_warned = true;
				}

				try
				{
					await Task.Delay(c_Refresh, stoppingToken);
				}
				catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
				{
					break;
				}
			}
		}
	}
}
