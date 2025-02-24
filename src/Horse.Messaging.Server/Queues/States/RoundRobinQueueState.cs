using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;
using Horse.Messaging.Server.Clients;
using Horse.Messaging.Server.Queues.Delivery;

namespace Horse.Messaging.Server.Queues.States
{
    internal class RoundRobinQueueState : IQueueState
    {
        public QueueMessage ProcessingMessage { get; private set; }
        public bool TriggerSupported => true;

        private readonly HorseQueue _queue;

        /// <summary>
        /// Round robin client list index
        /// </summary>
        private int _roundRobinIndex = -1;

        public RoundRobinQueueState(HorseQueue queue)
        {
            _queue = queue;
        }

        public Task<PullResult> Pull(QueueClient client, HorseMessage request)
        {
            return Task.FromResult(PullResult.StatusNotSupported);
        }

        public async Task<PushResult> Push(QueueMessage message)
        {
            try
            {
                if (!message.Deadline.HasValue && _queue.Options.MessageTimeout > TimeSpan.Zero)
                    message.Deadline = DateTime.UtcNow.Add(_queue.Options.MessageTimeout);

                Tuple<QueueClient, int> tuple = await GetNextAvailableRRClient(_roundRobinIndex, message,
                    _queue.Options.Acknowledge == QueueAckDecision.WaitForAcknowledge);
                QueueClient cc = tuple.Item1;

                if (cc != null)
                    _roundRobinIndex = tuple.Item2;

                if (cc == null)
                {
                    PushResult pushResult = _queue.AddMessage(message, false);
                    if (pushResult != PushResult.Success)
                        return pushResult;
                    
                    return PushResult.NoConsumers;
                }

                ProcessingMessage = message;
                PushResult result = await ProcessMessage(message, cc);

                return result;
            }
            catch (Exception e)
            {
                _queue.Rider.SendError("PUSH", e, $"QueueName:{_queue.Name}, State:RoundRobin");
                return PushResult.Error;
            }
            finally
            {
                ProcessingMessage = null;
            }
        }

        private async Task<PushResult> ProcessMessage(QueueMessage message, QueueClient receiver)
        {
            //if we need acknowledge from receiver, it has a deadline.
            DateTime? deadline = null;
            if (_queue.Options.Acknowledge != QueueAckDecision.None)
                deadline = DateTime.UtcNow.Add(_queue.Options.AcknowledgeTimeout);

            //return if client unsubsribes while waiting ack of previous message
            if (!_queue.ClientsClone.Contains(receiver))
            {
                PushResult pushResult = _queue.AddMessage(message, false);
                if (pushResult != PushResult.Success)
                    return pushResult;
                
                return PushResult.NoConsumers;
            }

            if (message.CurrentDeliveryReceivers.Count > 0)
                message.CurrentDeliveryReceivers.Clear();

            IQueueDeliveryHandler deliveryHandler = _queue.Manager.DeliveryHandler;

            message.Decision = await deliveryHandler.BeginSend(_queue, message);
            if (!await _queue.ApplyDecision(message.Decision, message))
                return PushResult.Success;

            //create prepared message data
            byte[] messageData = HorseProtocolWriter.Create(message.Message);

            //create delivery object
            MessageDelivery delivery = new MessageDelivery(message, receiver, deadline);

            //send the message
            bool sent = await receiver.Client.SendAsync(messageData);

            if (sent)
            {
                if (_queue.Options.Acknowledge != QueueAckDecision.None)
                {
                    receiver.CurrentlyProcessing = message;
                    receiver.ProcessDeadline = deadline ?? DateTime.UtcNow;
                    message.CurrentDeliveryReceivers.Add(receiver);
                }

                //adds the delivery to time keeper to check timing up
                deliveryHandler.Tracker.Track(delivery);

                //mark message is sent
                delivery.MarkAsSent();
                _queue.Info.AddMessageSend();

                foreach (IQueueMessageEventHandler handler in _queue.Rider.Queue.MessageHandlers.All())
                    _ = handler.OnConsumed(_queue, delivery, receiver.Client);
            }
            else
                message.Decision = await deliveryHandler.ConsumerReceiveFailed(_queue, delivery, receiver.Client);

            if (!await _queue.ApplyDecision(message.Decision, message))
                return PushResult.Success;

            message.Decision = await deliveryHandler.EndSend(_queue, message);
            await _queue.ApplyDecision(message.Decision, message);

            return PushResult.Success;
        }

        public Task<QueueStatusAction> EnterStatus(QueueStatus previousStatus)
        {
            return Task.FromResult(QueueStatusAction.AllowAndTrigger);
        }

        public Task<QueueStatusAction> LeaveStatus(QueueStatus nextStatus)
        {
            return Task.FromResult(QueueStatusAction.Allow);
        }

        /// <summary>
        /// Gets next available client which is not currently consuming any message.
        /// Used for wait for acknowledge situations
        /// </summary>
        private async Task<Tuple<QueueClient, int>> GetNextAvailableRRClient(int currentIndex, QueueMessage message, bool waitForAcknowledge)
        {
            List<QueueClient> clients = _queue.ClientsClone;
            if (clients.Count == 0)
                return new Tuple<QueueClient, int>(null, currentIndex);

            DateTime retryExpiration = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                int index = currentIndex < 0 ? 0 : currentIndex;
                for (int i = 0; i < clients.Count; i++)
                {
                    if (index >= clients.Count)
                        index = 0;

                    QueueClient client = clients[index];

                    if (waitForAcknowledge && client.CurrentlyProcessing != null)
                    {
                        if (client.ProcessDeadline < DateTime.UtcNow)
                        {
                            client.CurrentlyProcessing = null;
                        }
                        else
                        {
                            index++;
                            continue;
                        }
                    }

                    var deliveryHandler = _queue.Manager.DeliveryHandler;
                    bool canReceive = await deliveryHandler.CanConsumerReceive(_queue, message, client.Client);
                    if (!canReceive)
                    {
                        index++;
                        continue;
                    }

                    index++;
                    return new Tuple<QueueClient, int>(client, index);
                }

                await Task.Delay(3);
                clients = _queue.ClientsClone;
                if (clients.Count == 0)
                    break;

                //don't try hard so much, wait for next trigger operation of the queue.
                //it will be triggered in 5 secs, anyway
                if (DateTime.UtcNow > retryExpiration)
                    break;
            }

            return new Tuple<QueueClient, int>(null, currentIndex);
        }
    }
}