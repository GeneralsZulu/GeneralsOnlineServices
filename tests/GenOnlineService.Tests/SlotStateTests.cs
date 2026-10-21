using GenOnlineService;
using GenOnlineService.Controllers;
using Xunit;

namespace GenOnlineService.Tests
{
	public class SlotStateTests
	{
		// the Zulu client sends Tactical AI as 6; an upstream merge once dropped it (2.1.0)
		[Fact]
		public void Host_can_set_every_ai_kind_including_tactical()
		{
			Assert.True(LobbyController.TryParseSlotState(6, out EPlayerType state));
			Assert.Equal(EPlayerType.SLOT_TACTICAL_AI, state);
			Assert.True(LobbyController.TryParseSlotState((ushort)EPlayerType.SLOT_BRUTAL_AI, out _));
		}

		[Fact]
		public void Host_cannot_mark_a_slot_human_or_send_unknown_values()
		{
			Assert.False(LobbyController.TryParseSlotState((ushort)EPlayerType.SLOT_PLAYER, out _));
			Assert.False(LobbyController.TryParseSlotState(7, out _));
		}
	}
}
