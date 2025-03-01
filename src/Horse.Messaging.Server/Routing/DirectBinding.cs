using System;
using System.Linq;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;
using Horse.Messaging.Server.Clients;

namespace Horse.Messaging.Server.Routing
{
	/// <summary>
	/// Direct message binding.
	/// Targets clients.
	/// Binding receivers are received messages as DirectMessage.
	/// </summary>
	public class DirectBinding: Binding
	{
		private DateTime _clientListUpdateTime;
		private MessagingClient[] _clients;
		private int _roundRobinIndex = -1;
		private readonly object _lock = new object();
		private Func<MessagingClient, bool> _clientFilter = null;

		/// <summary>
		/// Custom client filter.
		/// </summary>
		/// <param name="filter"></param>
		protected void SetClientFilter(Func<MessagingClient, bool> filter)
		{
			_clientFilter = filter;
		}

		/// <summary>
		/// Sends the message to binding receivers
		/// </summary>
		public override async Task<bool> Send(MessagingClient sender, HorseMessage message)
		{
			try
			{
				MessagingClient[] clients = GetClients();
				if (_clientFilter is not null) clients = clients.Where(_clientFilter).ToArray();
				if (clients.Length == 0)
					return false;

				message.Type = MessageType.DirectMessage;
				message.SetTarget(Target);

				if (ContentType.HasValue)
					message.ContentType = ContentType.Value;

				message.WaitResponse = Interaction == BindingInteraction.Response;
				switch (RouteMethod)
				{
					case RouteMethod.OnlyFirst:
						var first = clients.FirstOrDefault();
						if (first == null)
							return false;

						return await first.SendAsync(message);

					case RouteMethod.Distribute:
						bool atLeastOneSent = false;
						foreach (MessagingClient client in clients)
						{
							bool sent = await client.SendAsync(message);
							if (sent && !atLeastOneSent)
								atLeastOneSent = true;
						}

						return atLeastOneSent;

					case RouteMethod.RoundRobin:
						return await SendRoundRobin(clients, message);

					default:
						return false;
				}
			}
			catch (Exception e)
			{
				Router.Rider.SendError("BINDING_SEND", e, $"Type:Direct, Binding:{Name}");
				return false;
			}
		}

		private Task<bool> SendRoundRobin(MessagingClient[] clients, HorseMessage message)
		{
			MessagingClient client;

			lock (_lock)
			{
				_roundRobinIndex++;

				if (_roundRobinIndex >= clients.Length)
					_roundRobinIndex = 0;

				client = clients[_roundRobinIndex];
			}

			return client.SendAsync(message);
		}

		/// <summary>
		/// Gets client from cache or reload
		/// </summary>
		protected MessagingClient[] GetClients()
		{
			//using cache to prevent performance hurt thousands of message per second situations
			//receivers are reloded in every second while messages are receiving
			if (DateTime.UtcNow - _clientListUpdateTime > TimeSpan.FromMilliseconds(1000))
			{
				MessagingClient[] clients;

				if (Target.StartsWith("@type:", StringComparison.InvariantCultureIgnoreCase))
				{
					var list = Router.Rider.Client.FindByType(Target.Substring(6));
					clients = list == null ? new MessagingClient[0] : list.ToArray();
				}
				else if (Target.StartsWith("@name:", StringComparison.InvariantCultureIgnoreCase))
				{
					var list = Router.Rider.Client.FindClientByName(Target.Substring(6));
					clients = list == null ? new MessagingClient[0] : list.ToArray();
				}
				else
				{
					MessagingClient client = Router.Rider.Client.Find(Target);
					clients = client == null ? new MessagingClient[0] : new[] { client };
				}

				_clientListUpdateTime = DateTime.UtcNow;
				_clients = clients;
				return clients;
			}

			return _clients;
		}
	}
}