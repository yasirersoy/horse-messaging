using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using EnumsNET;
using Horse.Messaging.Protocol;
using Horse.Messaging.Protocol.Models;
using Horse.Messaging.Server.Clients;
using Horse.Messaging.Server.Cluster;
using Horse.Messaging.Server.Helpers;
using Horse.Messaging.Server.Queues;
using Horse.Messaging.Server.Routing;
using Horse.Messaging.Server.Security;

namespace Horse.Messaging.Server.Network
{
    internal class ServerMessageHandler : INetworkMessageHandler
    {
        #region Fields

        /// <summary>
        /// Messaging Queue Server
        /// </summary>
        private readonly HorseRider _rider;

        public ServerMessageHandler(HorseRider rider)
        {
            _rider = rider;
        }

        #endregion

        #region Handle

        public async Task Handle(MessagingClient client, HorseMessage message, bool fromNode)
        {
            try
            {
                await HandleUnsafe(client, message);
            }
            catch (OperationCanceledException)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.LimitExceeded));
            }
            catch (NotSupportedException)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.Unacceptable));
            }
            catch (DuplicateNameException)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.Duplicate));
            }
        }

        private Task HandleUnsafe(MessagingClient client, HorseMessage message)
        {
            switch (message.ContentType)
            {
                //subscribe to a queue
                case KnownContentTypes.QueueSubscribe:
                    return Subscribe(client, message);

                //unsubscribe from a queue
                case KnownContentTypes.QueueUnsubscribe:
                    return Unsubscribe(client, message);

                //get queue consumers
                case KnownContentTypes.QueueConsumers:
                    return GetQueueConsumers(client, message);

                //creates new queue
                case KnownContentTypes.CreateQueue:
                    return CreateQueue(client, message);

                //remove only queue
                case KnownContentTypes.RemoveQueue:
                    return RemoveQueue(client, message);

                //update queue
                case KnownContentTypes.UpdateQueue:
                    return UpdateQueue(client, message);

                //clear messages in queue
                case KnownContentTypes.ClearMessages:
                    return ClearMessages(client, message);

                //get queue information list
                case KnownContentTypes.QueueList:
                    return GetQueueList(client, message);

                //get queue information
                case KnownContentTypes.NodeList:
                    return GetNodeList(client, message);

                //get client information list
                case KnownContentTypes.ClientList:
                    return GetClients(client, message);

                //lists all routers
                case KnownContentTypes.ListRouters:
                    return ListRouters(client, message);

                //creates new router
                case KnownContentTypes.CreateRouter:
                    return CreateRouter(client, message);

                //removes a router
                case KnownContentTypes.RemoveRouter:
                    return RemoveRouter(client, message);

                //lists all bindings of a router
                case KnownContentTypes.ListBindings:
                    return ListRouterBindings(client, message);

                //adds new binding to a router
                case KnownContentTypes.AddBinding:
                    return CreateRouterBinding(client, message);

                //removes a binding from a router
                case KnownContentTypes.RemoveBinding:
                    return RemoveRouterBinding(client, message);

                //for not-defines content types, use user-defined message handler
                default:
                    foreach (IServerMessageHandler handler in _rider.ServerMessageHandlers.All())
                        return handler.Received(client, message);

                    return Task.CompletedTask;
            }
        }

        #endregion

        #region Queue

        /// <summary>
        /// Finds and subscribes to the queue and sends response
        /// </summary>
        private async Task Subscribe(MessagingClient client, HorseMessage message)
        {
            HorseQueue queue = _rider.Queue.Find(message.Target);

            //if auto creation active, try to create queue
            if (queue == null && _rider.Queue.Options.AutoQueueCreation)
            {
                QueueOptions options = QueueOptions.CloneFrom(_rider.Queue.Options);
                queue = await _rider.Queue.Create(message.Target, options, message, true, true, client);
            }

            if (queue == null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));

                return;
            }

            QueueClient found = queue.FindClient(client.UniqueId);
            if (found != null && client == found.Client)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));

                return;
            }

            SubscriptionResult result = await queue.AddClient(client);
            if (message.WaitResponse)
            {
                switch (result)
                {
                    case SubscriptionResult.Success:
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
                        break;

                    case SubscriptionResult.Unauthorized:
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));
                        break;

                    case SubscriptionResult.Full:
                        await client.SendAsync(message.CreateResponse(HorseResultCode.LimitExceeded));
                        break;
                }
            }
        }

        /// <summary>
        /// Unsubscribes from the queue and sends response
        /// </summary>
        private async Task Unsubscribe(MessagingClient client, HorseMessage message)
        {
            if (message.Target == "*")
            {
                IEnumerable<QueueClient> queueClients = client.GetQueues();

                foreach (QueueClient queueClient in queueClients)
                    queueClient.Queue.RemoveClient(queueClient.Client);

                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
                return;
            }

            HorseQueue queue = _rider.Queue.Find(message.Target);
            if (queue == null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));

                return;
            }

            bool success = queue.RemoveClient(client);

            if (message.WaitResponse)
                await client.SendAsync(message.CreateResponse(success ? HorseResultCode.Ok : HorseResultCode.NotFound));
        }

        /// <summary>
        /// Gets active consumers of the queue 
        /// </summary>
        public async Task GetQueueConsumers(MessagingClient client, HorseMessage message)
        {
            HorseQueue queue = _rider.Queue.Find(message.Target);

            if (queue == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanReceiveQueueConsumers(client, queue);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            List<ClientInformation> list = new List<ClientInformation>();

            foreach (QueueClient cc in queue.ClientsClone)
                list.Add(new ClientInformation
                {
                    Id = cc.Client.UniqueId,
                    Name = cc.Client.Name,
                    Type = cc.Client.Type,
                    IsAuthenticated = cc.Client.IsAuthenticated,
                    Online = cc.JoinDate.LifetimeMilliseconds(),
                });

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            message.ContentType = KnownContentTypes.QueueConsumers;
            response.Serialize(list, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        /// <summary>
        /// Creates new queue and sends response
        /// </summary>
        private async Task CreateQueue(MessagingClient client, HorseMessage message)
        {
            NetworkOptionsBuilder builder = null;
            if (message.Length > 0)
                builder = await JsonSerializer.DeserializeAsync<NetworkOptionsBuilder>(message.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            HorseQueue queue = _rider.Queue.Find(message.Target);

            //if queue exists, we can't create. return duplicate response.
            if (queue != null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Duplicate));

                return;
            }

            //check authority if client can create queue
            foreach (IClientAuthorization authorization in _rider.Client.Authorizations.All())
            {
                bool grant = await authorization.CanCreateQueue(client, message.Target, builder);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            //creates new queue
            QueueOptions options = QueueOptions.CloneFrom(_rider.Queue.Options);
            if (builder != null)
                builder.ApplyToQueue(options);

            queue = await _rider.Queue.Create(message.Target, options, message, true, false, client);

            //if creation successful, sends response
            if (message.WaitResponse)
            {
                if (queue != null)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
                else
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Failed));
            }
        }

        /// <summary>
        /// Removes a queue from a server
        /// </summary>
        private async Task RemoveQueue(MessagingClient client, HorseMessage message)
        {
            HorseQueue queue = _rider.Queue.Find(message.Target);
            if (queue == null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanRemoveQueue(client, queue);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            await _rider.Queue.Remove(queue);

            if (message.WaitResponse)
                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Creates new queue and sends response
        /// </summary>
        private async Task UpdateQueue(MessagingClient client, HorseMessage message)
        {
            NetworkOptionsBuilder builder = await JsonSerializer.DeserializeAsync<NetworkOptionsBuilder>(message.Content, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            HorseQueue queue = _rider.Queue.Find(message.Target);
            if (queue == null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));

                return;
            }

            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanUpdateQueueOptions(client, queue, builder);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            builder.Type = null;
            builder.ApplyToQueue(queue.Options);
            queue.UpdateConfiguration();

            //if creation successful, sends response
            if (message.WaitResponse)
                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));

            _rider.Cluster.SendQueueUpdated(queue);
        }

        /// <summary>
        /// Clears messages in a queue
        /// </summary>
        private async Task ClearMessages(MessagingClient client, HorseMessage message)
        {
            HorseQueue queue = _rider.Queue.Find(message.Target);
            if (queue == null)
            {
                if (message.WaitResponse)
                    await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));

                return;
            }

            string prio = message.FindHeader(HorseHeaders.PRIORITY_MESSAGES);
            string msgs = message.FindHeader(HorseHeaders.MESSAGES);
            bool clearPrio = !string.IsNullOrEmpty(prio) && prio.Equals("yes", StringComparison.InvariantCultureIgnoreCase);
            bool clearMsgs = !string.IsNullOrEmpty(msgs) && msgs.Equals("yes", StringComparison.InvariantCultureIgnoreCase);

            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanClearQueueMessages(client, queue, clearPrio, clearMsgs);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            if (clearPrio && clearMsgs)
            {
                await queue.Manager.PriorityMessageStore.Clear();
                await queue.Manager.MessageStore.Clear();
            }
            else if (clearPrio)
                await queue.Manager.PriorityMessageStore.Clear();
            else if (clearMsgs)
                await queue.Manager.MessageStore.Clear();

            //if creation successful, sends response
            if (message.WaitResponse)
                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Finds all queues in the server
        /// </summary>
        private async Task GetQueueList(MessagingClient client, HorseMessage message)
        {
            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanReceiveQueues(client);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            string filter = message.FindHeader(HorseHeaders.FILTER);

            List<QueueInformation> list = new List<QueueInformation>();
            foreach (HorseQueue queue in _rider.Queue.Queues)
            {
                if (queue == null)
                    continue;

                if (!string.IsNullOrEmpty(filter) && !Filter.CheckMatch(queue.Name, filter))
                    continue;

                string ack = "none";
                if (queue.Options.Acknowledge == QueueAckDecision.JustRequest)
                    ack = "just";
                else if (queue.Options.Acknowledge == QueueAckDecision.WaitForAcknowledge)
                    ack = "wait";

                list.Add(new QueueInformation
                {
                    Name = queue.Name,
                    Topic = queue.Topic,
                    Status = queue.Status.ToString().Trim().ToLower(),
                    PriorityMessages = queue.Manager == null ? 0 : queue.Manager.PriorityMessageStore.Count(),
                    Messages = queue.Manager == null ? 0 : queue.Manager.MessageStore.Count(),
                    ProcessingMessages = queue.ClientsClone.Count(x => x.CurrentlyProcessing != null),
                    DeliveryTrackingMessags = queue.Manager == null ? 0 : queue.Manager.DeliveryHandler.Tracker.GetDeliveryCount(),
                    Acknowledge = ack,
                    AcknowledgeTimeout = Convert.ToInt32(queue.Options.AcknowledgeTimeout.TotalMilliseconds),
                    MessageTimeout = Convert.ToInt32(queue.Options.MessageTimeout.TotalMilliseconds),
                    ReceivedMessages = queue.Info.ReceivedMessages,
                    SentMessages = queue.Info.SentMessages,
                    NegativeAcks = queue.Info.NegativeAcknowledge,
                    Acks = queue.Info.Acknowledges,
                    TimeoutMessages = queue.Info.TimedOutMessages,
                    SavedMessages = queue.Info.MessageSaved,
                    RemovedMessages = queue.Info.MessageRemoved,
                    Errors = queue.Info.ErrorCount,
                    LastMessageReceived = queue.Info.GetLastMessageReceiveUnix(),
                    LastMessageSent = queue.Info.GetLastMessageSendUnix(),
                    MessageLimit = queue.Options.MessageLimit,
                    LimitExceededStrategy = queue.Options.LimitExceededStrategy.AsString(EnumFormat.Description),
                    MessageSizeLimit = queue.Options.MessageSizeLimit,
                    DelayBetweenMessages = queue.Options.DelayBetweenMessages
                });
            }

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            message.ContentType = KnownContentTypes.QueueList;
            response.Serialize(list, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        #endregion

        #region Instance

        /// <summary>
        /// Gets connected instance list
        /// </summary>
        private async Task GetNodeList(MessagingClient client, HorseMessage message)
        {
            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanManageInstances(client, message);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            List<NodeInformation> list = new List<NodeInformation>();

            //outgoing nodes
            foreach (NodeClient node in _rider.Cluster.Clients)
            {
                string state = NodeState.Replica.ToString();
                if (node.Info.Id == _rider.Cluster.MainNode?.Id)
                    state = NodeState.Main.ToString();
                else if (node.Info.Id == _rider.Cluster.SuccessorNode?.Id)
                    state = NodeState.Successor.ToString();

                list.Add(new NodeInformation
                {
                    Id = node.Info.Id,
                    Name = node.Info.Name,
                    Host = node.Info.Host,
                    PublicHost = node.Info.PublicHost,
                    State = state,
                    IsConnected = node.IsConnected,
                    Lifetime = Convert.ToInt64((DateTime.UtcNow - node.ConnectedDate).TotalMilliseconds)
                });
            }

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            message.ContentType = KnownContentTypes.NodeList;
            response.Serialize(list, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        #endregion

        #region Client

        /// <summary>
        /// Gets all connected clients
        /// </summary>
        public async Task GetClients(MessagingClient client, HorseMessage message)
        {
            foreach (IAdminAuthorization authorization in _rider.Client.AdminAuthorizations.All())
            {
                bool grant = await authorization.CanReceiveClients(client);
                if (!grant)
                {
                    if (message.WaitResponse)
                        await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));

                    return;
                }
            }

            List<ClientInformation> list = new List<ClientInformation>();

            string filter = null;
            if (!string.IsNullOrEmpty(message.Target))
                filter = message.Target;

            foreach (MessagingClient mc in _rider.Client.Clients)
            {
                if (!string.IsNullOrEmpty(filter))
                {
                    if (string.IsNullOrEmpty(mc.Type) || !Filter.CheckMatch(mc.Type, filter))
                        continue;
                }

                list.Add(new ClientInformation
                {
                    Id = mc.UniqueId,
                    Name = mc.Name,
                    Type = mc.Type,
                    IsAuthenticated = mc.IsAuthenticated,
                    Online = mc.ConnectedDate.LifetimeMilliseconds(),
                });
            }

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            response.ContentType = KnownContentTypes.ClientList;
            response.Serialize(list, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        #endregion

        #region Router

        /// <summary>
        /// Creates new router
        /// </summary>
        private async Task CreateRouter(MessagingClient client, HorseMessage message)
        {
            IRouter found = _rider.Router.Find(message.Target);
            if (found != null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
                return;
            }

            string methodHeader = message.FindHeader(HorseHeaders.ROUTE_METHOD);
            RouteMethod method = RouteMethod.Distribute;
            if (!string.IsNullOrEmpty(methodHeader))
                method = (RouteMethod)Convert.ToInt32(methodHeader);

            //check create queue access
            foreach (IClientAuthorization authorization in _rider.Client.Authorizations.All())
            {
                bool grant = await authorization.CanCreateRouter(client, message.Target, method);
                if (!grant)
                {
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));
                    return;
                }
            }

            _rider.Router.Add(message.Target, method);
            await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Removes a router with it's bindings
        /// </summary>
        private async Task RemoveRouter(MessagingClient client, HorseMessage message)
        {
            IRouter found = _rider.Router.Find(message.Target);
            if (found == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
                return;
            }

            //check create queue access
            foreach (IClientAuthorization authorization in _rider.Client.Authorizations.All())
            {
                bool grant = await authorization.CanRemoveRouter(client, found);
                if (!grant)
                {
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));
                    return;
                }
            }

            _rider.Router.Remove(found);
            await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Sends all routers
        /// </summary>
        private async Task ListRouters(MessagingClient client, HorseMessage message)
        {
            List<RouterInformation> items = new List<RouterInformation>();
            foreach (IRouter router in _rider.Router.Routers)
            {
                RouterInformation info = new RouterInformation
                {
                    Name = router.Name,
                    IsEnabled = router.IsEnabled
                };

                if (router is Router r)
                    info.Method = r.Method;

                items.Add(info);
            }

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            response.Serialize(items, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        /// <summary>
        /// Creates new binding for a router
        /// </summary>
        private async Task CreateRouterBinding(MessagingClient client, HorseMessage message)
        {
            IRouter router = _rider.Router.Find(message.Target);
            if (router == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            BindingInformation info = message.Deserialize<BindingInformation>(_rider.MessageContentSerializer);

            //check create queue access
            foreach (IClientAuthorization authorization in _rider.Client.Authorizations.All())
            {
                bool grant = await authorization.CanCreateBinding(client, router, info);
                if (!grant)
                {
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));
                    return;
                }
            }

            if (info.BindingType.IndexOf('.') < 0)
                info.BindingType = "Horse.Messaging.Server.Routing." + info.BindingType;

            Type bindingType = Type.GetType(info.BindingType);
            if (bindingType == null && !info.BindingType.EndsWith("Binding"))
                bindingType = Type.GetType($"{info.BindingType}Binding");

            if (bindingType == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            Binding binding = (Binding)Activator.CreateInstance(bindingType);
            binding.Name = info.Name;
            binding.Target = info.Target;
            binding.ContentType = info.ContentType;
            binding.Priority = info.Priority;
            binding.Interaction = info.Interaction;
            binding.RouteMethod = info.Method;

            router.AddBinding(binding);
            await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Removes a router with it's bindings
        /// </summary>
        private async Task RemoveRouterBinding(MessagingClient client, HorseMessage message)
        {
            IRouter router = _rider.Router.Find(message.Target);
            if (router == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            string name = message.FindHeader(HorseHeaders.BINDING_NAME);
            if (string.IsNullOrEmpty(name))
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            Binding[] bindings = router.GetBindings();
            Binding binding = bindings.FirstOrDefault(x => x.Name == name);
            if (binding == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            //check create queue access
            foreach (IClientAuthorization authorization in _rider.Client.Authorizations.All())
            {
                bool grant = await authorization.CanRemoveBinding(client, binding);
                if (!grant)
                {
                    await client.SendAsync(message.CreateResponse(HorseResultCode.Unauthorized));
                    return;
                }
            }

            router.RemoveBinding(binding);
            await client.SendAsync(message.CreateResponse(HorseResultCode.Ok));
        }

        /// <summary>
        /// Sends all bindings of a router
        /// </summary>
        private async Task ListRouterBindings(MessagingClient client, HorseMessage message)
        {
            IRouter router = _rider.Router.Find(message.Target);
            if (router == null)
            {
                await client.SendAsync(message.CreateResponse(HorseResultCode.NotFound));
                return;
            }

            List<BindingInformation> items = new List<BindingInformation>();
            foreach (Binding binding in router.GetBindings())
            {
                BindingInformation info = new BindingInformation
                {
                    Name = binding.Name,
                    Target = binding.Target,
                    Priority = binding.Priority,
                    ContentType = binding.ContentType,
                    Interaction = binding.Interaction,
                    BindingType = binding.GetType().FullName,
                    Method = binding.RouteMethod
                };

                items.Add(info);
            }

            HorseMessage response = message.CreateResponse(HorseResultCode.Ok);
            response.Serialize(items, _rider.MessageContentSerializer);
            await client.SendAsync(response);
        }

        #endregion
    }
}