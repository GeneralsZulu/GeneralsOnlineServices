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

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_GET_GlobalStats_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public DailyStatsStructure? globalstats { get; set; } = null;
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class GlobalStatsController : ControllerBase
	{
		private readonly ILogger<GlobalStatsController> _logger;

		public GlobalStatsController(ILogger<GlobalStatsController> logger)
		{
			_logger = logger;
		}

		[HttpGet(Name = "GlobalStats")]
		[Authorize(Roles = "GameClient,ChatClient,GameLauncher,Monitor")]
		public APIResult Get()
		{
			RouteHandler_GET_GlobalStats_Result result = new RouteHandler_GET_GlobalStats_Result();

			// The welcome screen shows per-side win percentages. With radarvan configured
			// these are community-wide totals; otherwise this service's own daily tally.
			RadarvanSnapshot snapshot = RadarvanStats.Snapshot;
			if (snapshot.GlobalMatches != null && snapshot.GlobalWins != null)
			{
				result.globalstats = new DailyStatsStructure
				{
					matches = (int[])snapshot.GlobalMatches.Clone(),
					wins = (int[])snapshot.GlobalWins.Clone()
				};
			}
			else
			{
				result.globalstats = DailyStatsManager.g_StatsContainer.Stats;
			}

			return result;
		}
	}
}
