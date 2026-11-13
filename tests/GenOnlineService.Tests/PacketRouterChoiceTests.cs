using System.Collections.Generic;
using GenOnlineService;
using Xunit;

namespace GenOnlineService.Tests
{
	public class PacketRouterChoiceTests
	{
		static readonly List<(long, int)> Three = new() { (10, 0), (20, 1), (30, 2) };

		[Fact]
		public void Picks_the_member_whose_slowest_link_is_fastest()
		{
			var pings = new Dictionary<long, Dictionary<long, int>>
			{
				[10] = new() { [20] = 40, [30] = 150 },
				[20] = new() { [10] = 40, [30] = 60 },
				[30] = new() { [10] = 150, [20] = 60 },
			};
			Assert.Equal(20, PacketRouterChoice.Choose(Three, pings));
		}

		[Fact]
		public void A_relayed_or_missing_link_disqualifies_and_ties_go_to_the_lower_slot()
		{
			var pings = new Dictionary<long, Dictionary<long, int>>
			{
				[10] = new() { [20] = 50, [30] = 50 },
				[20] = new() { [10] = 10, [30] = -1 },
				[30] = new() { [10] = 50, [20] = 50 },
			};
			Assert.Equal(10, PacketRouterChoice.Choose(Three, pings));
		}

		[Fact]
		public void Nobody_qualifying_or_two_players_keeps_the_client_default()
		{
			var pings = new Dictionary<long, Dictionary<long, int>> { [10] = new() { [20] = 5 } };
			Assert.Equal(-1, PacketRouterChoice.Choose(Three, pings));
			Assert.Equal(-1, PacketRouterChoice.Choose(new List<(long, int)> { (10, 0), (20, 1) }, pings));
		}
	}
}
