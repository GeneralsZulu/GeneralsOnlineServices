using System;
using System.Linq;
using GenOnlineService;
using Xunit;

namespace GenOnlineService.Tests
{
	// Fixtures are synthetic but follow the shapes /api/playerstats,
	// /api/player_ratings/ and /api/generalstats returned on 2026-10-16.
	public class RadarvanStatsTests
	{
		private const string PlayerStatsJson = @"{""playerStats"":[
			{""playerName"":""Alpha"",""stats"":{""0"":{""wins"":10,""losses"":5},""4"":{""wins"":30,""losses"":20},""11"":{""wins"":1,""losses"":0}},
			 ""factionStats"":[],""overTime"":[],""gameCounts"":{""total"":66,""1v1"":6,""2v2"":60}},
			{""playerName"":""Bravo"",""stats"":{""8"":{""wins"":2,""losses"":9}},""factionStats"":[],""overTime"":[],""gameCounts"":{""total"":11}},
			{""playerName"":"""",""stats"":{}}
		]}";

		private const string RatingsJson = @"{""player_rating"":[],""player_rating_overtime"":{},
			""player_form"":{""Alpha"":[true,false,true,true,true],""Bravo"":[true,false,false],""Nobody"":[true]}}";

		private const string GeneralStatsJson = @"{""general_stats"":[
			{""general"":0,""stats"":[],""total"":{""wins"":304,""losses"":314}},
			{""general"":10,""stats"":[],""total"":{""wins"":7,""losses"":3}},
			{""general"":-1,""stats"":[],""total"":{""wins"":1,""losses"":1}}
		]}";

		[Fact]
		public void Parse_reads_players_generals_and_totals()
		{
			RadarvanSnapshot snap = RadarvanStats.Parse(PlayerStatsJson, null, null);

			Assert.Equal(2, snap.Players.Count); // the blank name is skipped
			Assert.True(snap.Players.TryGetValue("alpha", out RadarvanPlayerRecord? alpha)); // case-insensitive
			Assert.Equal("Alpha", alpha!.Name);
			Assert.Equal(10, alpha.Wins[0]);
			Assert.Equal(20, alpha.Losses[4]);
			Assert.Equal(41, alpha.TotalWins);
			Assert.Equal(25, alpha.TotalLosses);
			Assert.False(alpha.HasForm);
			Assert.Null(snap.GlobalMatches);
		}

		[Fact]
		public void Parse_attaches_form_streaks_only_to_known_players()
		{
			RadarvanSnapshot snap = RadarvanStats.Parse(PlayerStatsJson, RatingsJson, null);

			Assert.Equal(2, snap.FormCount);
			Assert.Equal(3, snap.Players["Alpha"].CurrentWinStreak);
			Assert.Equal(0, snap.Players["Alpha"].CurrentLossStreak);
			Assert.Equal(0, snap.Players["Bravo"].CurrentWinStreak);
			Assert.Equal(2, snap.Players["Bravo"].CurrentLossStreak);
			Assert.False(snap.Players.ContainsKey("Nobody"));
		}

		[Fact]
		public void Parse_maps_general_stats_into_daily_stats_slots()
		{
			RadarvanSnapshot snap = RadarvanStats.Parse(PlayerStatsJson, null, GeneralStatsJson);

			Assert.NotNull(snap.GlobalMatches);
			// USA is template 2 -> slot 0; Stealth is template 13 -> slot 11; general -1 is ignored
			Assert.Equal(618, snap.GlobalMatches![0]);
			Assert.Equal(304, snap.GlobalWins![0]);
			Assert.Equal(10, snap.GlobalMatches[11]);
			Assert.Equal(7, snap.GlobalWins[11]);
			Assert.Equal(628, snap.GlobalMatches.Sum());
		}

		[Theory]
		[InlineData(new bool[0], 0, 0)]
		[InlineData(new[] { true }, 1, 0)]
		[InlineData(new[] { false, false }, 0, 2)]
		[InlineData(new[] { true, true, false, true, true, true }, 3, 0)]
		[InlineData(new[] { true, false, false }, 0, 2)]
		public void CurrentStreaks_counts_the_trailing_run(bool[] form, int wins, int losses)
		{
			Assert.Equal((wins, losses), RadarvanStats.CurrentStreaks(form));
		}

		[Fact]
		public void Apply_writes_template_indexed_arrays_and_streaks()
		{
			RadarvanSnapshot snap = RadarvanStats.Parse(PlayerStatsJson, RatingsJson, null);
			RadarvanStats.SetSnapshot(snap);

			PlayerStats ps = new PlayerStats(42, 1234, 9, 1300);
			ps.wins[2] = 99; ps.games[2] = 99; // GO's own USA counters, to be replaced
			ps.unitsKilled[2] = 777;           // not something radarvan knows, must survive
			ps.winsInARow = 5;

			Assert.True(RadarvanStats.Apply(ps, " alpha "));

			Assert.Equal(10, ps.wins[2]);  Assert.Equal(5, ps.losses[2]);  Assert.Equal(15, ps.games[2]);   // USA -> 2
			Assert.Equal(30, ps.wins[3]);  Assert.Equal(20, ps.losses[3]); Assert.Equal(50, ps.games[3]);   // China -> 3
			Assert.Equal(1, ps.wins[12]);  Assert.Equal(0, ps.losses[12]); Assert.Equal(1, ps.games[12]);   // Demo -> 12
			Assert.Equal(0, ps.games[7]);                                                                  // Air never played
			Assert.Equal(777, ps.unitsKilled[2]);
			Assert.Equal(3, ps.winsInARow);
			Assert.Equal(0, ps.lossesInARow);
			Assert.Equal(1234, ps.EloRating); // untouched: the radarvan rating never enters the wire object
			Assert.Equal(9, ps.EloMatches);
		}

		[Fact]
		public void Apply_leaves_unknown_players_alone()
		{
			RadarvanStats.SetSnapshot(RadarvanStats.Parse(PlayerStatsJson, null, null));

			PlayerStats ps = new PlayerStats(7, 1000, 0, 1000);
			ps.wins[4] = 3; ps.games[4] = 4; ps.losses[4] = 1;

			Assert.False(RadarvanStats.Apply(ps, "Charlie"));
			Assert.False(RadarvanStats.Apply(ps, (string?)null));
			Assert.Equal(3, ps.wins[4]);
			Assert.Equal(4, ps.games[4]);
		}

		[Fact]
		public void GeneralToTemplate_is_a_permutation_of_the_twelve_general_templates()
		{
			int[] t = RadarvanStats.GeneralToTemplate;
			Assert.Equal(12, t.Length);
			Assert.Equal(Enumerable.Range(2, 12), t.OrderBy(x => x));
		}
	}
}
