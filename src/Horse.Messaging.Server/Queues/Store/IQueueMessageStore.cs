using System;
using System.Collections;
using System.Collections.Generic;

namespace Horse.Messaging.Server.Queues.Store
{
    /// <summary>
    /// Queue message store implementation stores queue messages.
    /// </summary>
    public interface IQueueMessageStore
    {
        /// <summary>
        /// Returns count of all stored messages
        /// </summary>
        /// <returns></returns>
        int CountAll();

        /// <summary>
        /// Returns count of stored regular messages
        /// </summary>
        /// <returns></returns>
        int CountRegular();

        /// <summary>
        /// Returns count of high priority marked messages
        /// </summary>
        /// <returns></returns>
        int CountPriority();

        /// <summary>
        /// Puts a message into message store 
        /// </summary>
        void Put(QueueMessage message);

        /// <summary>
        /// Returns id list of all messages
        /// </summary>
        IEnumerable<string> GetMessageIdList(bool priorityMessages);

        /// <summary>
        /// Gets next message from store
        /// </summary>
        QueueMessage GetNext(bool remove, bool fromEnd = false);

        /// <summary>
        /// Get next regular message
        /// </summary>
        QueueMessage GetRegularNext(bool remove, bool fromEnd = false);

        /// <summary>
        /// Get next priority message
        /// </summary>
        QueueMessage GetPriorityNext(bool remove, bool fromEnd = false);

        /// <summary>
        /// Finds message, removes from store and returns
        /// </summary>
        QueueMessage FindAndRemove(Func<QueueMessage, bool> predicate);

        /// <summary>
        /// Finds message, removes from store and returns
        /// </summary>
        List<QueueMessage> FindAll(Func<QueueMessage, bool> predicate);

        /// <summary>
        /// Finds in regular
        /// </summary>
        List<QueueMessage> FindAndRemoveRegular(Func<QueueMessage, bool> predicate);
        
        /// <summary>
        /// Finds in high priority messages
        /// </summary>
        List<QueueMessage> FindAndRemovePriority(Func<QueueMessage, bool> predicate);

        /// <summary>
        /// Gets all messages.
        /// That method returns the messages without thread safe
        /// </summary>
        IEnumerable<QueueMessage> GetUnsafe();
        
        /// <summary>
        /// Gets all priority messages.
        /// That method returns the messages without thread safe
        /// </summary>
        IEnumerable<QueueMessage> GetUnsafePriority();
        
        /// <summary>
        /// Finds and removes message from store
        /// </summary>
        void Remove(QueueMessage message);

        /// <summary>
        /// Clears all regular messages from the queue
        /// </summary>
        void ClearRegular();
        
        /// <summary>
        /// Clears all high priority messages from the queue
        /// </summary>
        void ClearPriority();
        
        /// <summary>
        /// Clears all messages from the queue
        /// </summary>
        void ClearAll();
    }
}