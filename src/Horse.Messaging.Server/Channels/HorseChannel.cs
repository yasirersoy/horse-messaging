using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;
using Horse.Messaging.Server.Clients;
using Horse.Messaging.Server.Containers;
using Horse.Messaging.Server.Queues;

namespace Horse.Messaging.Server.Channels
{
    /// <summary>
    /// Horse Channel
    /// </summary>
    public class HorseChannel
    {
        #region Properties

        /// <summary>
        /// Unique name (not case-sensetive)
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Queue topic
        /// </summary>
        public string Topic { get; set; }

        /// <summary>
        /// Root horse rider
        /// </summary>
        public HorseRider Rider { get; }

        /// <summary>
        /// Channel status
        /// </summary>
        public ChannelStatus Status { get; set; }

        /// <summary>
        /// Channel options.
        /// If null, default channel options will be used
        /// </summary>
        public HorseChannelOptions Options { get; }

        /// <summary>
        /// Payload object for end-user usage
        /// </summary>
        public object Payload { get; set; }

        /// <summary>
        /// Clients in the queue as thread-unsafe list
        /// </summary>
        public IEnumerable<ChannelClient> ClientsUnsafe => _clients.GetUnsafeList();

        /// <summary>
        /// Clients in the queue as cloned list
        /// </summary>
        public List<ChannelClient> ClientsClone => _clients.GetAsClone();

        private readonly SafeList<ChannelClient> _clients;

        #endregion

        #region Constructors - Destroy

        internal HorseChannel(HorseRider rider, string name, HorseChannelOptions options)
        {
            Rider = rider;
            Name = name;
            Options = options;
            Status = ChannelStatus.Running;
            _clients = new SafeList<ChannelClient>(256);
        }

        internal void Destroy()
        {
            
        }

        internal void UpdateOptionsByMessage(HorseMessage message)
        {
            //todo: update options
        }

        #endregion

        #region Delivery

        /// <summary>
        /// Pushes new message into the queue
        /// </summary>
        public PushResult Push(string message)
        {
            HorseMessage msg = new HorseMessage(MessageType.QueueMessage, Name);
            msg.SetStringContent(message);
            return Push(msg);
        }

        /// <summary>
        /// Pushes new message into the queue
        /// </summary>
        public PushResult Push(HorseMessage message)
        {
            message.Type = MessageType.QueueMessage;
            message.SetTarget(Name);

            return Push(message, null);
        }

        /// <summary>
        /// Pushes a message into the queue.
        /// </summary>
        internal PushResult Push(HorseMessage message, MessagingClient sender)
        {
            if (Status == ChannelStatus.Paused)
                return PushResult.StatusNotSupported;

            if (Options.MessageSizeLimit > 0 && message.Length > Options.MessageSizeLimit)
                return PushResult.LimitExceeded;

            //remove operational headers that are should not be sent to consumers or saved to disk
            message.RemoveHeaders(HorseHeaders.CHANNEL_NAME, HorseHeaders.CC);

            try
            {
                byte[] messageData = HorseProtocolWriter.Create(message);

                //to all receivers
                foreach (ChannelClient client in ClientsClone)
                {
                    //to only online receivers
                    if (!client.Client.IsConnected)
                        continue;

                    //send the message
                    _ = client.Client.SendAsync(messageData);
                }

                return PushResult.Success;
            }
            catch (Exception ex)
            {
                Rider.SendError("PUSH", ex, $"ChannelName:{Name}");
                return PushResult.Error;
            }
        }

        #endregion

        #region Client Actions

        /// <summary>
        /// Returns client count in the queue
        /// </summary>
        /// <returns></returns>
        public int ClientsCount()
        {
            return _clients.Count;
        }

        /// <summary>
        /// Adds the client to the queue
        /// </summary>
        public async Task<SubscriptionResult> AddClient(MessagingClient client)
        {
            foreach (IChannelAuthorization authenticator in Rider.Channel.Authenticators.All())
            {
                bool allowed = await authenticator.CanSubscribe(this, client);
                if (!allowed)
                    return SubscriptionResult.Unauthorized;
            }

            if (Options.ClientLimit > 0 && _clients.Count >= Options.ClientLimit)
                return SubscriptionResult.Full;

            ChannelClient cc = new ChannelClient(this, client);
            _clients.Add(cc);
            client.AddSubscription(cc);

            foreach (IChannelEventHandler handler in Rider.Channel.EventHandlers.All())
                _ = handler.OnSubscribe(this, client);

            //todo: OnConsumerSubscribed.Trigger(cc);
            return SubscriptionResult.Success;
        }

        /// <summary>
        /// Removes client from the queue
        /// </summary>
        public void RemoveClient(ChannelClient client)
        {
            _clients.Remove(client);
            client.Client.RemoveSubscription(client);

            foreach (IChannelEventHandler handler in Rider.Channel.EventHandlers.All())
                _ = handler.OnUnsubscribe(this, client.Client);

            //todo: OnConsumerUnsubscribed.Trigger(client);
        }

        /// <summary>
        /// Removes client from the queue, does not call MqClient's remove method
        /// </summary>
        internal void RemoveClientSilent(ChannelClient client)
        {
            _clients.Remove(client);

            foreach (IChannelEventHandler handler in Rider.Channel.EventHandlers.All())
                _ = handler.OnUnsubscribe(this, client.Client);

            //todo: OnConsumerUnsubscribed.Trigger(client);
        }

        /// <summary>
        /// Removes client from the queue
        /// </summary>
        public bool RemoveClient(MessagingClient client)
        {
            ChannelClient cc = _clients.FindAndRemove(x => x.Client == client);

            if (cc == null)
                return false;

            client.RemoveSubscription(cc);

            foreach (IChannelEventHandler handler in Rider.Channel.EventHandlers.All())
                _ = handler.OnUnsubscribe(this, client);

            return true;
        }

        /// <summary>
        /// Finds client in the queue
        /// </summary>
        public ChannelClient FindClient(string uniqueId)
        {
            return _clients.Find(x => x.Client.UniqueId == uniqueId);
        }

        /// <summary>
        /// Finds client in the queue
        /// </summary>
        public ChannelClient FindClient(MessagingClient client)
        {
            return _clients.Find(x => x.Client == client);
        }

        #endregion
    }
}