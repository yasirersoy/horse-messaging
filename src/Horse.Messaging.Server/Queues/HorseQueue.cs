using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;
using Horse.Messaging.Protocol.Events;
using Horse.Messaging.Server.Clients;
using Horse.Messaging.Server.Cluster;
using Horse.Messaging.Server.Containers;
using Horse.Messaging.Server.Events;
using Horse.Messaging.Server.Helpers;
using Horse.Messaging.Server.Options;
using Horse.Messaging.Server.Queues.Delivery;
using Horse.Messaging.Server.Queues.States;
using Horse.Messaging.Server.Queues.Store;
using Horse.Messaging.Server.Security;

namespace Horse.Messaging.Server.Queues
{
    /// <summary>
    /// Event handler for queues
    /// </summary>
    public delegate void QueueEventHandler(HorseQueue queue);

    /// <summary>
    /// Horse message queue.
    /// Keeps queued messages and subscribed clients.
    /// </summary>
    public class HorseQueue
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
        /// Server of the queue
        /// </summary>
        public HorseRider Rider { get; }

        /// <summary>
        /// Queue status
        /// </summary>
        public QueueStatus Status { get; private set; }

        /// <summary>
        /// Queue type
        /// </summary>
        public QueueType Type { get; private set; }

        /// <summary>
        /// Current status state object
        /// </summary>
        internal IQueueState State { get; private set; }

        /// <summary>
        /// Message store of the queue
        /// </summary>
        internal IQueueMessageStore Store { get; private set; }

        /// <summary>
        /// Queue options.
        /// If null, queue default options will be used
        /// </summary>
        public QueueOptions Options { get; }

        /// <summary>
        /// Queue messaging handler.
        /// If null, server's default delivery will be used.
        /// </summary>
        public IMessageDeliveryHandler DeliveryHandler { get; set; }

        /// <summary>
        /// Queue statistics and information
        /// </summary>
        public QueueInfo Info { get; } = new QueueInfo();

        /// <summary>
        /// Queue delivery handler name
        /// </summary>
        internal string HandlerName { get; set; }

        /// <summary>
        /// Message header data which triggers the initialization of the queue
        /// </summary>
        internal IEnumerable<KeyValuePair<string, string>> InitializationMessageHeaders { get; set; }

        /// <summary>
        /// Returns currently processing message.
        /// The message is about to send, but it might be waiting for acknowledge of previous message or delay between messages.
        /// </summary>
        public QueueMessage ProcessingMessage => State?.ProcessingMessage;

        /// <summary>
        /// Time keeper for the queue.
        /// Checks message receiver deadlines and delivery deadlines.
        /// </summary>
        internal QueueTimeKeeper TimeKeeper { get; private set; }

        /// <summary>
        /// Returns true, if there are no messages in queue
        /// </summary>
        public bool IsEmpty => Store == null || Store.CountAll() == 0;

        /// <summary>
        /// Wait acknowledge cross thread locker
        /// </summary>
        private SemaphoreSlim _ackLock;

        /// <summary>
        /// Sync object for inserting messages into queue as FIFO
        /// </summary>
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// This task holds the code until acknowledge is received
        /// </summary>
        private TaskCompletionSource<bool> _acknowledgeCallback;

        /// <summary>
        /// Trigger locker field.
        /// Used to prevent concurrent trigger method calls.
        /// </summary>
        private volatile bool _triggering;

        /// <summary>
        /// Payload object for end-user usage
        /// </summary>
        public object Payload { get; set; }

        /// <summary>
        /// Checks queue if trigger required triggers.
        /// In usual, this timer should never start to triggers, it's just plan b.
        /// </summary>
        private Timer _triggerTimer;

        /// <summary>
        /// Triggered when queue is destroyed
        /// </summary>
        public event QueueEventHandler OnDestroyed;

        /// <summary>
        /// Clients in the queue as thread-unsafe list
        /// </summary>
        public IEnumerable<QueueClient> ClientsUnsafe => _clients.GetUnsafeList();

        /// <summary>
        /// Clients in the queue as cloned list
        /// </summary>
        public List<QueueClient> ClientsClone => _clients.GetAsClone();

        private readonly SafeList<QueueClient> _clients;

        /// <summary>
        /// True if queue is destroyed
        /// </summary>
        public bool IsDestroyed { get; private set; }

        private DateTime _syncStartDate;
        private Dictionary<string, bool> _syncMethods;

        #endregion

        #region Events

        /// <summary>
        /// Event Manage for HorseEventType.MessagePushedToQueue
        /// </summary>
        public EventManager PushEvent { get; }

        /// <summary>
        /// Event Manage for HorseEventType.QueueMessageAck
        /// </summary>
        public EventManager MessageAckEvent { get; }

        /// <summary>
        /// Event Manage for HorseEventType.QueueMessageNack
        /// </summary>
        public EventManager MessageNackEvent { get; }

        /// <summary>
        /// Event Manage for HorseEventType.QueueMessageUnack
        /// </summary>
        public EventManager MessageUnackEvent { get; }

        /// <summary>
        /// Event Manage for HorseEventType.QueueMessageTimeout
        /// </summary>
        public EventManager MessageTimeoutEvent { get; }

        #endregion

        #region Constructors - Destroy

        internal HorseQueue(HorseRider rider, string name, QueueOptions options)
        {
            Rider = rider;
            Name = name;
            Options = options;
            Type = options.Type;
            _clients = new SafeList<QueueClient>(256);
            Store = new LinkedMessageStore(this);
            Status = QueueStatus.NotInitialized;

            PushEvent = new EventManager(rider, HorseEventType.QueuePush, name);
            MessageAckEvent = new EventManager(rider, HorseEventType.QueueMessageAck, name);
            MessageNackEvent = new EventManager(rider, HorseEventType.QueueMessageNack, name);
            MessageUnackEvent = new EventManager(rider, HorseEventType.QueueMessageUnack, name);
            MessageTimeoutEvent = new EventManager(rider, HorseEventType.QueueMessageTimeout, name);
        }

        /// <summary>
        /// Sets queue status
        /// </summary>
        public void SetStatus(QueueStatus newStatus)
        {
            if (newStatus == QueueStatus.NotInitialized)
                return;

            Rider.Queue.StatusChangeEvent.Trigger(Name,
                                                  new KeyValuePair<string, string>($"Previous-{HorseHeaders.STATUS}", Status.ToString()),
                                                  new KeyValuePair<string, string>($"Next-{HorseHeaders.STATUS}", newStatus.ToString()));

            Status = newStatus;
        }

        /// <summary>
        /// Initializes queue to first use
        /// </summary>
        internal async Task InitializeQueue(IMessageDeliveryHandler deliveryHandler = null)
        {
            await _lock.WaitAsync();
            try
            {
                if (Status != QueueStatus.NotInitialized)
                    return;

                if (deliveryHandler != null)
                    DeliveryHandler = deliveryHandler;

                if (DeliveryHandler == null)
                    throw new ArgumentNullException("Queue has no delivery handler: " + Name);

                var tuple = QueueStateFactory.Create(this, Options.Type);
                State = tuple.Item1;
                Type = Options.Type;

                TimeKeeper = new QueueTimeKeeper(this);
                TimeKeeper.Run();

                _ackLock = new SemaphoreSlim(1, 1);
                Status = QueueStatus.Running;

                _triggerTimer = new Timer(a =>
                {
                    if (!_triggering && State != null && State.TriggerSupported)
                        _ = Trigger();

                    _ = CheckAutoDestroy();
                }, null, TimeSpan.FromMilliseconds(5000), TimeSpan.FromMilliseconds(5000));
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Destorys the queue
        /// </summary>
        public async Task Destroy()
        {
            IsDestroyed = true;

            try
            {
                if (TimeKeeper != null)
                    await TimeKeeper.Destroy();

                Store?.ClearAll();

                if (_ackLock != null)
                {
                    _ackLock.Dispose();
                    _ackLock = null;
                }

                if (_lock != null)
                    _lock.Dispose();

                if (_triggerTimer != null)
                {
                    await _triggerTimer.DisposeAsync();
                    _triggerTimer = null;
                }
            }
            finally
            {
                OnDestroyed?.Invoke(this);
            }

            _clients.Clear();

            Rider.Cluster.SendQueueRemoved(this);
        }

        /// <summary>
        /// If auto destroy is enabled, checks and removes queue if it should be removed
        /// </summary>
        internal async Task CheckAutoDestroy()
        {
            if (IsDestroyed || Options.AutoDestroy == QueueDestroy.Disabled)
                return;

            switch (Options.AutoDestroy)
            {
                case QueueDestroy.NoConsumers:
                    if (_clients.Count == 0)
                        await Rider.Queue.Remove(this);

                    break;

                case QueueDestroy.NoMessages:
                    if (IsEmpty && (TimeKeeper == null || !TimeKeeper.HasPendingDelivery()))
                        await Rider.Queue.Remove(this);

                    break;

                case QueueDestroy.Empty:
                    if (_clients.Count == 0 && IsEmpty && (TimeKeeper == null || !TimeKeeper.HasPendingDelivery()))
                        await Rider.Queue.Remove(this);

                    break;
            }
        }

        internal NodeQueueInfo CreateNodeQueueInfo()
        {
            return new NodeQueueInfo
            {
                Name = Name,
                Topic = Topic,
                HandlerName = HandlerName,
                Initialized = Status != QueueStatus.NotInitialized,
                PutBackDelay = Options.PutBackDelay,
                MessageSizeLimit = Options.MessageSizeLimit,
                MessageLimit = Options.MessageLimit,
                ClientLimit = Options.ClientLimit,
                MessageTimeout = Convert.ToInt32(Options.MessageTimeout.TotalSeconds),
                AcknowledgeTimeout = Convert.ToInt32(Options.AcknowledgeTimeout),
                DelayBetweenMessages = Convert.ToInt32(Options.DelayBetweenMessages),
                Acknowledge = Options.Acknowledge.FromAckDecision(),
                AutoDestroy = Options.AutoDestroy.FromQueueDestroy(),
                QueueType = Options.Type.FromQueueType(),
                Headers = InitializationMessageHeaders?.Select(x => new NodeQueueHandlerHeader
                {
                    Key = x.Key,
                    Value = x.Value
                }).ToArray()
            };
        }

        #endregion

        #region Messages

        /// <summary>
        /// Returns pending high priority messages count
        /// </summary>
        public int PriorityMessageCount()
        {
            return Store == null ? 0 : Store.CountPriority();
        }

        /// <summary>
        /// Returns pending regular messages count
        /// </summary>
        public int MessageCount()
        {
            return Store == null ? 0 : Store.CountRegular();
        }

        /// <summary>
        /// Returns unique pending message count
        /// </summary>
        public int GetAckPendingMessageCount()
        {
            if (TimeKeeper == null)
                return 0;

            return TimeKeeper.GetPendingMessageCount();
        }

        /// <summary>
        /// Returns next message but doesn't dequeue it
        /// </summary>
        public QueueMessage FindNextMessage(bool priority)
        {
            if (priority)
                return Store.GetPriorityNext(false);

            return Store.GetRegularNext(false);
        }

        /// <summary>
        /// Adds message into the queue
        /// </summary>
        internal void AddMessage(QueueMessage message, bool trigger = true)
        {
            if (Status == QueueStatus.Syncing)
            {
                try
                {
                    _lock.Wait();
                }
                finally
                {
                    _lock.Release();
                }
            }

            Store.Put(message);

            if (trigger && State != null && State.TriggerSupported && !_triggering)
                _ = Trigger();
        }

        #endregion

        #region Type Actions

        internal void UpdateOptionsByMessage(HorseMessage message)
        {
            if (!message.HasHeader)
                return;

            foreach (KeyValuePair<string, string> pair in message.Headers)
            {
                if (pair.Key.Equals(HorseHeaders.ACKNOWLEDGE, StringComparison.InvariantCultureIgnoreCase))
                    Options.Acknowledge = pair.Value.ToAckDecision();

                else if (pair.Key.Equals(HorseHeaders.QUEUE_TYPE, StringComparison.InvariantCultureIgnoreCase))
                    Options.Type = pair.Value.ToQueueType();

                else if (pair.Key.Equals(HorseHeaders.QUEUE_TOPIC, StringComparison.InvariantCultureIgnoreCase))
                    Topic = pair.Value;

                else if (pair.Key.Equals(HorseHeaders.PUT_BACK_DELAY, StringComparison.InvariantCultureIgnoreCase))
                    Options.PutBackDelay = Convert.ToInt32(pair.Value);

                else if (pair.Key.Equals(HorseHeaders.MESSAGE_TIMEOUT, StringComparison.InvariantCultureIgnoreCase))
                    Options.MessageTimeout = TimeSpan.FromSeconds(Convert.ToInt32(pair.Value));

                else if (pair.Key.Equals(HorseHeaders.ACK_TIMEOUT, StringComparison.InvariantCultureIgnoreCase))
                    Options.AcknowledgeTimeout = TimeSpan.FromSeconds(Convert.ToInt32(pair.Value));

                else if (pair.Key.Equals(HorseHeaders.DELAY_BETWEEN_MESSAGES, StringComparison.InvariantCultureIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(pair.Value))
                        Options.DelayBetweenMessages = Convert.ToInt32(pair.Value);
                }
            }
        }

        internal void UpdateOptionsByNodeInfo(NodeQueueInfo info)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Delivery

        /// <summary>
        /// Pushes new message into the queue
        /// </summary>
        public Task<PushResult> Push(string message, bool highPriority = false)
        {
            HorseMessage msg = new HorseMessage(MessageType.QueueMessage, Name);
            msg.HighPriority = highPriority;
            msg.SetStringContent(message);
            return Push(msg);
        }

        /// <summary>
        /// Pushes new message into the queue
        /// </summary>
        public Task<PushResult> Push(HorseMessage message)
        {
            message.Type = MessageType.QueueMessage;
            message.SetTarget(Name);

            return Push(new QueueMessage(message), null);
        }

        /// <summary>
        /// Pushes a message into the queue.
        /// </summary>
        internal async Task<PushResult> Push(QueueMessage message, MessagingClient sender)
        {
            if (Status == QueueStatus.NotInitialized)
            {
                try
                {
                    UpdateOptionsByMessage(message.Message);
                    DeliveryHandlerBuilder handlerBuilder = new DeliveryHandlerBuilder
                    {
                        Server = Rider,
                        Queue = this,
                        Headers = message.Message.Headers,
                        HandlerName = message.Message.FindHeader(HorseHeaders.DELIVERY_HANDLER)
                    };

                    if (string.IsNullOrEmpty(handlerBuilder.HandlerName))
                        handlerBuilder.HandlerName = "Default";

                    Func<DeliveryHandlerBuilder, Task<IMessageDeliveryHandler>> factory = Rider.Queue.DeliveryHandlerFactories[handlerBuilder.HandlerName];
                    IMessageDeliveryHandler deliveryHandler = await factory(handlerBuilder);

                    await InitializeQueue(deliveryHandler);

                    handlerBuilder.TriggerAfterCompleted();
                }
                catch (Exception e)
                {
                    Rider.SendError("INITIALIZE_IN_PUSH", e, $"QueueName:{Name}");
                    throw;
                }
            }

            if (Status is QueueStatus.OnlyConsume or QueueStatus.Paused)
                return PushResult.StatusNotSupported;

            if (Options.MessageLimit > 0 && Store.CountAll() >= Options.MessageLimit)
                return PushResult.LimitExceeded;

            if (Options.MessageSizeLimit > 0 && message.Message.Length > Options.MessageSizeLimit)
                return PushResult.LimitExceeded;

            //remove operational headers that are should not be sent to consumers or saved to disk
            message.Message.RemoveHeaders(HorseHeaders.DELAY_BETWEEN_MESSAGES,
                                          HorseHeaders.ACKNOWLEDGE,
                                          HorseHeaders.QUEUE_NAME,
                                          HorseHeaders.QUEUE_TYPE,
                                          HorseHeaders.QUEUE_TOPIC,
                                          HorseHeaders.PUT_BACK_DELAY,
                                          HorseHeaders.DELIVERY,
                                          HorseHeaders.DELIVERY_HANDLER,
                                          HorseHeaders.MESSAGE_TIMEOUT,
                                          HorseHeaders.ACK_TIMEOUT,
                                          HorseHeaders.CC);

            //prepare properties
            message.Message.WaitResponse = Options.Acknowledge != QueueAckDecision.None;

            //if message doesn't have message id, create new message id for the message
            if (string.IsNullOrEmpty(message.Message.MessageId))
                message.Message.SetMessageId(Rider.MessageIdGenerator.Create());

            //if we have an option maximum wait duration for message, set it after message joined to the queue.
            //time keeper will check this value and if message time is up, it will remove message from the queue.
            if (Options.MessageTimeout > TimeSpan.Zero)
                message.Deadline = DateTime.UtcNow.Add(Options.MessageTimeout);

            if (Status == QueueStatus.Syncing)
            {
                try
                {
                    await _lock.WaitAsync();
                }
                finally
                {
                    _lock.Release();
                }
            }

            bool isReplica = (Rider.Cluster.Options.Mode == ClusterMode.Reliable && Rider.Cluster.State > NodeState.Main);
            try
            {
                if (Rider.Cluster.Options.Mode == ClusterMode.Reliable && Rider.Cluster.State == NodeState.Main)
                {
                    bool ack = await Rider.Cluster.SendQueueMessage(message.Message);
                    if (!ack)
                        return PushResult.Error;
                }

                //fire message receive event
                Info.AddMessageReceive();
                Decision decision = await DeliveryHandler.ReceivedFromProducer(this, message, sender);
                message.Decision = decision;

                bool allow = await ApplyDecision(decision, message);

                foreach (IQueueMessageEventHandler handler in Rider.Queue.MessageHandlers.All())
                    _ = handler.OnProduced(this, message, sender);

                if (!allow)
                    return PushResult.Success;

                AddMessage(message, !isReplica);

                if (!isReplica)
                    PushEvent.Trigger(sender, new KeyValuePair<string, string>(HorseHeaders.MESSAGE_ID, message.Message.MessageId));

                return PushResult.Success;
            }
            catch (Exception ex)
            {
                Rider.SendError("PUSH", ex, $"QueueName:{Name}");
                Info.AddError();
                try
                {
                    Decision decision = await DeliveryHandler.ExceptionThrown(this, State.ProcessingMessage, ex);

                    //the message is removed from the queue and it's not sent to consumers
                    //we should put the message back into the queue
                    if (!message.IsInQueue && !message.IsSent && !decision.Delete)
                        ApplyPutBack(Decision.PutBackMessage(true), message, 1000);

                    if (State.ProcessingMessage != null)
                        await ApplyDecision(decision, State.ProcessingMessage);
                }
                catch //if developer does wrong operation, we should not stop
                {
                }
            }

            return PushResult.Success;
        }

        /// <summary>
        /// Checks all pending messages and subscribed receivers.
        /// If they should receive the messages, runs the process.
        /// This method is called automatically after a client subscribed to the queue or status has changed.
        /// You can call manual after you filled queue manually.
        /// </summary>
        public async Task Trigger()
        {
            if (_triggering)
                return;

            await _lock.WaitAsync();
            try
            {
                if (_triggering || !State.TriggerSupported)
                    return;

                _triggering = true;
                await ProcessPendingMessages();
            }
            finally
            {
                _triggering = false;
                _lock.Release();
            }
        }

        /// <summary>
        /// Start to process all pending messages.
        /// This method is called after a client is subscribed to the queue.
        /// </summary>
        private async Task ProcessPendingMessages()
        {
            while (State.TriggerSupported)
            {
                if (_clients.Count == 0)
                    return;

                QueueMessage message = Store.GetNext(true);
                if (message == null)
                    return;

                try
                {
                    PushResult pr = await State.Push(message);
                    if (pr == PushResult.NoConsumers || pr == PushResult.Empty)
                        return;
                }
                catch (Exception ex)
                {
                    Rider.SendError("PROCESS_MESSAGES", ex, $"QueueName:{Name}");
                    Info.AddError();
                    try
                    {
                        Decision decision = await DeliveryHandler.ExceptionThrown(this, message, ex);

                        //the message is removed from the queue and it's not sent to consumers
                        //we should put the message back into the queue
                        if (!message.IsInQueue && !message.IsSent && !decision.Delete)
                            ApplyPutBack(Decision.PutBackMessage(true), message, 1000);

                        await ApplyDecision(decision, message, null, 5000);
                    }
                    catch //if developer does wrong operation, we should not stop
                    {
                    }
                }

                if (Options.DelayBetweenMessages > 0)
                    await Task.Delay(Options.DelayBetweenMessages);
            }
        }

        #endregion

        #region Decision

        /// <summary>
        /// Creates final decision from multiple decisions.
        /// Final decision has bests choices for each decision.
        /// </summary>
        internal static Decision CreateFinalDecision(Decision final, Decision decision)
        {
            bool save = final.Save || decision.Save;
            bool interrupt = final.Interrupt || decision.Interrupt;
            bool delete = final.Delete || decision.Delete;

            PutBackDecision putBack = final.PutBack;
            DecisionTransmission transmission = final.Transmission;

            if (decision.PutBack != PutBackDecision.No)
                putBack = decision.PutBack;

            if (decision.Transmission != DecisionTransmission.None)
                transmission = decision.Transmission;

            return new Decision(interrupt, save, delete, putBack, transmission);
        }

        /// <summary>
        /// Applies decision.
        /// If save is chosen, saves the message.
        /// If acknowledge is chosen, sends an ack message to source.
        /// Returns true is allowed
        /// </summary>
        internal async Task<bool> ApplyDecision(Decision decision, QueueMessage message, HorseMessage customAck = null, int forceDelay = 0)
        {
            try
            {
                if (decision.Save)
                    await SaveMessage(message);

                if (decision.Transmission != DecisionTransmission.None && !message.IsProducerAckSent)
                {
                    HorseMessage acknowledge = customAck ?? message.Message.CreateAcknowledge(decision.Transmission == DecisionTransmission.Failed ? "failed" : null);
                    if (message.Source != null && message.Source.IsConnected)
                    {
                        bool sent = await message.Source.SendAsync(acknowledge);
                        message.IsProducerAckSent = sent;
                    }
                }

                if (decision.PutBack != PutBackDecision.No)
                    ApplyPutBack(decision, message, forceDelay);
                else if (decision.Delete && !message.IsRemoved)
                {
                    message.MarkAsRemoved();
                    Info.AddMessageRemove();

                    _ = DeliveryHandler.MessageDequeued(this, message);

                    if (Rider.Cluster.State == NodeState.Main && Rider.Cluster.Options.Mode == ClusterMode.Reliable)
                        Rider.Cluster.SendMessageRemoval(this, message.Message);
                }
            }
            catch (Exception e)
            {
                Rider.SendError("APPLY_DECISION", e, $"QueueName:{Name}, MessageId:{message.Message.MessageId}");
            }

            return !decision.Interrupt;
        }

        /// <summary>
        /// Executes put back decision for the message
        /// </summary>
        internal void ApplyPutBack(Decision decision, QueueMessage message, int forceDelay = 0)
        {
            message.Message.HighPriority = decision.PutBack == PutBackDecision.Priority;
            
            switch (decision.PutBack)
            {
                case PutBackDecision.Priority:
                    if (Options.PutBackDelay == 0)
                    {
                        AddMessage(message, false);

                        if (Rider.Cluster.State == NodeState.Main && Rider.Cluster.Options.Mode == ClusterMode.Reliable)
                            Rider.Cluster.SendPutBack(this, message.Message, false);
                    }
                    else
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(Options.PutBackDelay);

                                if (Rider.Cluster.State == NodeState.Main && Rider.Cluster.Options.Mode == ClusterMode.Reliable)
                                    Rider.Cluster.SendPutBack(this, message.Message, false);

                                AddMessage(message, false);
                            }
                            catch (Exception e)
                            {
                                Rider.SendError("DELAYED_PUT_BACK", e, $"QueueName:{Name}, MessageId:{message.Message.MessageId}");
                            }
                        });

                    break;

                case PutBackDecision.Regular:
                    if (Options.PutBackDelay == 0 && forceDelay == 0)
                    {
                        AddMessage(message);

                        if (Rider.Cluster.State == NodeState.Main && Rider.Cluster.Options.Mode == ClusterMode.Reliable)
                            Rider.Cluster.SendPutBack(this, message.Message, true);
                    }
                    else
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(Math.Max(forceDelay, Options.PutBackDelay));

                                if (Rider.Cluster.State == NodeState.Main && Rider.Cluster.Options.Mode == ClusterMode.Reliable)
                                    Rider.Cluster.SendPutBack(this, message.Message, true);

                                AddMessage(message);
                            }
                            catch (Exception e)
                            {
                                Rider.SendError("DELAYED_PUT_BACK", e, $"QueueName:{Name}, MessageId:{message.Message.MessageId}");
                            }
                        });

                    break;
            }
        }

        /// <summary>
        /// Saves message
        /// </summary>
        public async Task<bool> SaveMessage(QueueMessage message)
        {
            try
            {
                if (message.IsSaved)
                    return false;

                if (Status != QueueStatus.NotInitialized)
                    message.IsSaved = await DeliveryHandler.SaveMessage(this, message);

                if (message.IsSaved)
                    Info.AddMessageSave();
            }
            catch (Exception e)
            {
                Rider.SendError("SAVE_MESSAGE", e, $"QueueName:{Name}, MessageId:{message.Message.MessageId}");
            }

            return message.IsSaved;
        }

        #endregion

        #region Acknowledge

        /// <summary>
        /// When wait for acknowledge is active, this method locks the queue until acknowledge is received
        /// </summary>
        internal async Task WaitForAcknowledge(QueueMessage message)
        {
            //if we will lock the queue until ack received, we must request ack
            if (!message.Message.WaitResponse)
                message.Message.WaitResponse = true;

            await _ackLock.WaitAsync();
            try
            {
                if (_acknowledgeCallback != null)
                    await _acknowledgeCallback.Task;

                _acknowledgeCallback = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            finally
            {
                _ackLock.Release();
            }
        }

        /// <summary>
        /// Called when a acknowledge message is received from the client
        /// </summary>
        internal async Task AcknowledgeDelivered(MessagingClient from, HorseMessage deliveryMessage)
        {
            try
            {
                if (Status == QueueStatus.NotInitialized)
                    return;

                MessageDelivery delivery = TimeKeeper.FindAndRemoveDelivery(from, deliveryMessage.MessageId);

                //when server and consumer are in pc,
                //sometimes consumer sends ack before server start to follow ack of the message
                //that happens when ack message is arrived in less than 0.01ms
                //in that situation, server can't find the delivery with FindAndRemoveDelivery, it returns null
                //so we need to check it again after a few milliseconds
                if (delivery == null)
                {
                    await Task.Delay(1);
                    delivery = TimeKeeper.FindAndRemoveDelivery(from, deliveryMessage.MessageId);

                    //try again
                    if (delivery == null)
                    {
                        await Task.Delay(3);
                        delivery = TimeKeeper.FindAndRemoveDelivery(from, deliveryMessage.MessageId);
                    }
                }

                if (delivery == null || delivery.Acknowledge == DeliveryAcknowledge.Timeout)
                    return;

                bool success = !(deliveryMessage.HasHeader &&
                                 deliveryMessage.Headers.Any(x => x.Key.Equals(HorseHeaders.NEGATIVE_ACKNOWLEDGE_REASON, StringComparison.InvariantCultureIgnoreCase)));

                if (delivery.Receiver != null && delivery.Message == delivery.Receiver.CurrentlyProcessing)
                    delivery.Receiver.CurrentlyProcessing = null;

                delivery.MarkAsAcknowledged(success);

                if (success)
                    Info.AddAcknowledge();
                else
                    Info.AddNegativeAcknowledge();

                Decision decision = await DeliveryHandler.AcknowledgeReceived(this, deliveryMessage, delivery, success);

                await ApplyDecision(decision, delivery.Message, deliveryMessage);

                foreach (IQueueMessageEventHandler handler in Rider.Queue.MessageHandlers.All())
                    _ = handler.OnAcknowledged(this, deliveryMessage, delivery, success);

                ReleaseAcknowledgeLock(true);

                if (success)
                    MessageAckEvent.Trigger(from, new KeyValuePair<string, string>(HorseHeaders.MESSAGE_ID, deliveryMessage.MessageId));
                else
                    MessageNackEvent.Trigger(from,
                                             new KeyValuePair<string, string>(HorseHeaders.MESSAGE_ID, deliveryMessage.MessageId),
                                             new KeyValuePair<string, string>(HorseHeaders.REASON, deliveryMessage.FindHeader(HorseHeaders.NEGATIVE_ACKNOWLEDGE_REASON)));
            }
            catch (Exception e)
            {
                Rider.SendError("QUEUE_ACK_RECEIVED", e, $"QueueName:{Name}, MessageId:{deliveryMessage.MessageId}");
            }
        }

        /// <summary>
        /// If acknowledge lock option is enabled, releases the lock
        /// </summary>
        internal void ReleaseAcknowledgeLock(bool received)
        {
            if (_acknowledgeCallback != null)
            {
                TaskCompletionSource<bool> ack = _acknowledgeCallback;
                _acknowledgeCallback = null;
                ack.SetResult(received);
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
            foreach (IQueueAuthenticator authenticator in Rider.Queue.Authenticators.All())
            {
                bool allowed = await authenticator.Authenticate(this, client);
                if (!allowed)
                    return SubscriptionResult.Unauthorized;
            }

            if (Options.ClientLimit > 0 && _clients.Count >= Options.ClientLimit)
                return SubscriptionResult.Full;

            QueueClient cc = new QueueClient(this, client);
            _clients.Add(cc);
            client.AddSubscription(cc);

            foreach (IQueueEventHandler handler in Rider.Queue.EventHandlers.All())
                _ = handler.OnConsumerSubscribed(cc);

            if (State != null && State.TriggerSupported)
                _ = Trigger();

            Rider.Queue.SubscriptionEvent.Trigger(client, Name);

            return SubscriptionResult.Success;
        }

        /// <summary>
        /// Removes client from the queue
        /// </summary>
        public void RemoveClient(QueueClient client)
        {
            _clients.Remove(client);
            client.Client.RemoveSubscription(client);

            foreach (IQueueEventHandler handler in Rider.Queue.EventHandlers.All())
                _ = handler.OnConsumerUnsubscribed(client);

            Rider.Queue.UnsubscriptionEvent.Trigger(client.Client, Name);
        }

        /// <summary>
        /// Removes client from the queue, does not call MqClient's remove method
        /// </summary>
        internal void RemoveClientSilent(QueueClient client)
        {
            _clients.Remove(client);

            foreach (IQueueEventHandler handler in Rider.Queue.EventHandlers.All())
                _ = handler.OnConsumerUnsubscribed(client);

            Rider.Queue.UnsubscriptionEvent.Trigger(client.Client, Name);
        }

        /// <summary>
        /// Removes client from the queue
        /// </summary>
        public bool RemoveClient(MessagingClient client)
        {
            QueueClient cc = _clients.FindAndRemove(x => x.Client == client);

            if (cc == null)
                return false;

            client.RemoveSubscription(cc);

            foreach (IQueueEventHandler handler in Rider.Queue.EventHandlers.All())
                _ = handler.OnConsumerUnsubscribed(cc);

            Rider.Queue.UnsubscriptionEvent.Trigger(client, Name);

            return true;
        }

        /// <summary>
        /// Finds client in the queue
        /// </summary>
        public QueueClient FindClient(string uniqueId)
        {
            return _clients.Find(x => x.Client.UniqueId == uniqueId);
        }

        /// <summary>
        /// Finds client in the queue
        /// </summary>
        public QueueClient FindClient(MessagingClient client)
        {
            return _clients.Find(x => x.Client == client);
        }

        #endregion

        #region Sync

        internal async Task StartSync(NodeClient replica)
        {
            if (Status == QueueStatus.Syncing)
                return;

            try
            {
                await _lock.WaitAsync();
            }
            catch
            {
                _lock.Release();
                return;
            }

            _syncStartDate = DateTime.UtcNow;
            SetStatus(QueueStatus.Syncing);
            IEnumerable<string> messageIdList = GetQueueMessageIdList();
            await Rider.Cluster.SendQueueMessageIdList(replica, Name, messageIdList);
        }

        internal bool FinishSync()
        {
            _syncStartDate = DateTime.UtcNow;
            _syncMethods = null;

            SetStatus(QueueStatus.Running);

            _lock.Release();
            return true;
        }

        internal IEnumerable<string> GetQueueMessageIdList()
        {
            if (Status != QueueStatus.Syncing)
                yield break;

            IEnumerable<string> priority = Store.GetMessageIdList(true);
            foreach (string id in priority)
                yield return id;

            yield return String.Empty;

            IEnumerable<string> regular = Store.GetMessageIdList(false);
            foreach (string id in regular)
                yield return id;
        }

        internal IEnumerable<string> CheckSync(string[] mainPrioMessages, string[] mainMessages)
        {
            _syncMethods = new Dictionary<string, bool>();

            //todo: calculate and generate sync methods
            //todo: return sync method message id list

            //return _syncMethods.Keys;
            throw new NotImplementedException();
        }

        internal async Task<bool> SyncMessage(HorseMessage message)
        {
            bool completed;
            bool found = _syncMethods.TryGetValue(message.MessageId, out completed);

            if (!found)
                return false;

            if (completed)
                return true;

            PushResult result = await Push(message);

            return result == PushResult.Success;
        }

        internal IEnumerable<HorseMessage> FindMessages(string[] idList)
        {
            if (Status == QueueStatus.Running)
                yield break;

            foreach (QueueMessage queueMessage in Store.GetUnsafePriority())
            {
                if (idList.Contains(queueMessage.Message.MessageId))
                    yield return queueMessage.Message;
            }

            foreach (QueueMessage queueMessage in Store.GetUnsafe())
            {
                if (idList.Contains(queueMessage.Message.MessageId))
                    yield return queueMessage.Message;
            }
        }

        internal void CompleteSync()
        {
            _syncMethods = null;

            if (Status == QueueStatus.Syncing)
                SetStatus(QueueStatus.Running);
        }

        #endregion
    }
}