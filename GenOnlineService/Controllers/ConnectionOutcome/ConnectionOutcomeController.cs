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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public class RouteHandler_POST_ConnectionOutcome_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return this.GetType();
		}

		public bool success { get; set; } = false;
	}

	// TODO_EFCORE: Move to a publish/subscribe model for rooms
	[ApiController]
	[Authorize(Roles = "GameClient")]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class ConnectionOutcomeController : ControllerBase
	{
		private readonly ILogger<ConnectionOutcomeController> _logger;
		private readonly LobbyManager _lobbyManager;
		private readonly IDbContextFactory<AppDbContext> _dbFactory;

		public ConnectionOutcomeController(LobbyManager lobbyManager, ILogger<ConnectionOutcomeController> logger, IDbContextFactory<AppDbContext> dbFactory)
		{
			_logger = logger;
			_lobbyManager = lobbyManager;
			_dbFactory = dbFactory;
		}

		[HttpPost]
		public async Task<APIResult> Post()
		{
			RouteHandler_POST_ConnectionOutcome_Result result = new RouteHandler_POST_ConnectionOutcome_Result();

			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();
				var options = new JsonSerializerOptions
				{
					PropertyNameCaseInsensitive = true
				};

				try
				{
					var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

					if (data != null
						&& data.ContainsKey("target")
						&& data.ContainsKey("direct")
						&& data.ContainsKey("outcome")
						&& data.ContainsKey("ipv4")
						)
					{

						// TODO_ASP: all these calls to get user ID are unnecessary, claims already have user id
						// TODO_NGMP: For all things which modify lobbies, we should check if the player is really in the lobby
						// get requesting user
						Int64 source_user = TokenHelper.GetUserID(this);
						if (source_user != -1)
						{
							Lobby? playerLobby = _lobbyManager.GetPlayerParticipantLobby(source_user);

							if (playerLobby != null)
							{
								//Int64 lobbyID = playerLobby.LobbyID;
								EIPVersion protocol = data["ipv4"].GetBoolean() ? EIPVersion.IPV4 : EIPVersion.IPV6;
								//Int64 target_user = data["target"].GetInt64();
								bool bDirect = data["direct"].GetBoolean();
								EConnectionStateClient in_outcome = (EConnectionStateClient)data["outcome"].GetInt32();

								EConnectionState outcome = EConnectionState.NOT_CONNECTED;
								if (in_outcome == EConnectionStateClient.NOT_CONNECTED)
								{
									outcome = EConnectionState.NOT_CONNECTED;
								}
								else if (in_outcome == EConnectionStateClient.CONNECTING_DIRECT)
								{
									outcome = EConnectionState.CONNECTING_DIRECT;
								}
								else if (in_outcome == EConnectionStateClient.FINDING_ROUTE)
								{
									outcome = EConnectionState.CONNECTING_DIRECT;
								}
								else if (in_outcome == EConnectionStateClient.CONNECTED_DIRECT)
								{
									if (bDirect)
									{
										outcome = EConnectionState.CONNECTED_DIRECT;
									}
									else
									{
										outcome = EConnectionState.CONNECTED_RELAY;
									}

								}
								else if (in_outcome == EConnectionStateClient.CONNECTION_FAILED)
								{
									outcome = EConnectionState.CONNECTION_FAILED;
								}
								else if (in_outcome == EConnectionStateClient.CONNECTION_DISCONNECTED)
								{
									outcome = EConnectionState.NOT_CONNECTED;
								}

								await using var db = await _dbFactory.CreateDbContextAsync();
								await Database.ConnectionOutcomes.StoreConnectionOutcome(db, protocol, outcome);

								// result.success was never set, so a stored outcome still answered
								// {"success":false} on a 200 - ~85 misleading log lines per player
								// per night, and no way to spot a real failure among them.
								result.success = true;

								Response.StatusCode = (int)HttpStatusCode.OK;
							}
							else
							{
								Response.StatusCode = (int)HttpStatusCode.NotFound;
							}
						}
						else
						{
							Response.StatusCode = (int)HttpStatusCode.NotFound;
						}
					}
				}
				catch
				{
					Response.StatusCode = (int)HttpStatusCode.InternalServerError;
				}
			}

			return result;
		}
	}
}
