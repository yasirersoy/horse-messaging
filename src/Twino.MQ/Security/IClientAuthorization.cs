using System.Threading.Tasks;
using Twino.MQ.Clients;
using Twino.MQ.Queues;
using Twino.Protocols.TMQ;

namespace Twino.MQ.Security
{
    /// <summary>
    /// Implementation of the object that checks authority of client operations
    /// </summary>
    public interface IClientAuthorization
    {
        /// <summary>
        /// Returns true, if user can create the channel
        /// </summary>
        Task<bool> CanCreateChannel(MqClient client, MqServer server, string channelName);

        /// <summary>
        /// Returns true, if user can remove the channel
        /// </summary>
        Task<bool> CanRemoveChannel(MqClient client, MqServer server, Channel channel);

        /// <summary>
        /// Returns true, if client can create new queue in the channel
        /// </summary>
        Task<bool> CanCreateQueue(MqClient client, Channel channel, ushort contentType, NetworkOptionsBuilder options);

        /// <summary>
        /// Returns true, if client can update queue options
        /// </summary>
        Task<bool> CanUpdateQueueOptions(MqClient client, Channel channel, ChannelQueue queue, NetworkOptionsBuilder options);

        /// <summary>
        /// Returns true, if client can remove the queue in a channel
        /// </summary>
        Task<bool> CanRemoveQueue(MqClient client, ChannelQueue queue);

        /// <summary>
        /// Returns true, if client can send a peer message
        /// </summary>
        Task<bool> CanMessageToPeer(MqClient sender, TmqMessage message, MqClient receiver);

        /// <summary>
        /// Returns true, if client can send a message to the queue
        /// </summary>
        Task<bool> CanMessageToQueue(MqClient client, ChannelQueue queue, TmqMessage message);

        /// <summary>
        /// Returns true, if client can pull a message from the queue
        /// </summary>
        Task<bool> CanPullFromQueue(ChannelClient client, ChannelQueue queue);

        /// <summary>
        /// Returns true, if client can manage instances
        /// </summary>
        Task<bool> CanManageInstances(MqClient client, TmqMessage request);

        /// <summary>
        /// Returns true, if client can receive all connected clients
        /// </summary>
        Task<bool> CanReceiveClients(MqClient client);

        /// <summary>
        /// Returns true, if client can receive channel info
        /// </summary>
        Task<bool> CanReceiveChannelInfo(MqClient client, Channel channel);

        /// <summary>
        /// Returns true, if client can receive all consumers of channel
        /// </summary>
        Task<bool> CanReceiveChannelConsumers(MqClient client, Channel channel);

        /// <summary>
        /// Returns true, if client can receive all queues of channel
        /// </summary>
        Task<bool> CanReceiveChannelQueues(MqClient client, Channel channel);
    }
}