using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Horse.Messaging.Client.Internal;
using Horse.Messaging.Client.Queues.Annotations;
using Horse.Messaging.Protocol;

namespace Horse.Messaging.Client.Queues
{
    internal class QueueConsumerExecuter<TModel> : ExecuterBase
    {
        private readonly Type _consumerType;
        private readonly IQueueConsumer<TModel> _consumer;
        private readonly Func<IHandlerFactory> _consumerFactoryCreator;
        private QueueConsumerRegistration _registration;

        public QueueConsumerExecuter(Type consumerType, IQueueConsumer<TModel> consumer, Func<IHandlerFactory> consumerFactoryCreator)
        {
            _consumerType = consumerType;
            _consumer = consumer;
            _consumerFactoryCreator = consumerFactoryCreator;
        }

        public override void Resolve(object registration)
        {
            _registration = registration as QueueConsumerRegistration;
            ResolveAttributes(_registration!.ConsumerType);
            ResolveQueueAttributes();
        }

        private void ResolveQueueAttributes()
        {
            if (!SendPositiveResponse)
            {
                AutoAckAttribute ackAttribute = _consumerType.GetCustomAttribute<AutoAckAttribute>();
                SendPositiveResponse = ackAttribute != null;
            }

            if (!SendNegativeResponse)
            {
                AutoNackAttribute nackAttribute = _consumerType.GetCustomAttribute<AutoNackAttribute>();
                SendNegativeResponse = nackAttribute != null;
                NegativeReason = nackAttribute?.Reason ?? NegativeReason.None;
            }
        }

        public override async Task Execute(HorseClient client, HorseMessage message, object model)
        {
            TModel t = (TModel) model;
            ProvidedHandler providedHandler = null;

            try
            {
                if (_consumer != null)
                {
                    await RunBeforeInterceptors(message, client);
                    await Consume(_consumer, message, t, client);
                    await RunAfterInterceptors(message, client);
                }

                else if (_consumerFactoryCreator != null)
                {
                    IHandlerFactory handlerFactory = _consumerFactoryCreator();
                    providedHandler = handlerFactory.CreateHandler(_consumerType);
                    IQueueConsumer<TModel> consumer = (IQueueConsumer<TModel>) providedHandler.Service;
                    await RunBeforeInterceptors(message, client, handlerFactory);
                    await Consume(consumer, message, t, client);
                    await RunAfterInterceptors(message, client, handlerFactory);
                }
                else
                    throw new NullReferenceException("There is no consumer defined");

                if (SendPositiveResponse)
                    await client.SendAck(message);
            }
            catch (Exception e)
            {
                if (SendNegativeResponse)
                    await SendNegativeAck(message, client, e);

                await SendExceptions(message, client, e);
            }
            finally
            {
                providedHandler?.Dispose();
            }
        }

        private async Task Consume(IQueueConsumer<TModel> consumer, HorseMessage message, TModel model, HorseClient client)
        {
            if (Retry == null)
            {
                await consumer.Consume(message, model, client);
                return;
            }

            int count = Retry.Count == 0 ? 100 : Retry.Count;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    await consumer.Consume(message, model, client);
                    return;
                }
                catch (Exception e)
                {
                    Type type = e.GetType();
                    if (Retry.IgnoreExceptions is { Length: > 0 })
                    {
                        if (Retry.IgnoreExceptions.Any(x => x.IsAssignableFrom(type)))
                            throw;
                    }

                    if (Retry.DelayBetweenRetries > 0)
                        await Task.Delay(Retry.DelayBetweenRetries);

                    if (i == count - 1)
                        throw;
                }
            }
        }
        
        /// <summary>
        /// Run before interceptors
        /// </summary>
        /// <param name="message"></param>
        /// <param name="client"></param>
        /// <param name="handlerFactory"></param>
        protected async Task RunBeforeInterceptors(HorseMessage message, HorseClient client, IHandlerFactory handlerFactory = null)
        {
            if (_registration.InterceptorDescriptors.Count == 0) return;
            var beforeInterceptors = _registration.InterceptorDescriptors.Where(m => m.RunBefore);
            IEnumerable<IHorseInterceptor> interceptors = handlerFactory is null
                ? beforeInterceptors.Select(m => m.Instance)
                : beforeInterceptors.Select(m => handlerFactory.CreateInterceptor(m.InterceptorType));

            foreach (var interceptor in interceptors)
                await interceptor!.Intercept(message, client);
        }

        /// <summary>
        /// Run after interceptors
        /// </summary>
        /// <param name="message"></param>
        /// <param name="client"></param>
        /// <param name="handlerFactory"></param>
        protected async Task RunAfterInterceptors(HorseMessage message, HorseClient client, IHandlerFactory handlerFactory = null)
        {
            if (_registration.InterceptorDescriptors.Count == 0) return;
            var afterInterceptors = _registration.InterceptorDescriptors.Where(m => !m.RunBefore);
            IEnumerable<IHorseInterceptor> interceptors = handlerFactory is null
                ? afterInterceptors.Select(m => m.Instance)
                : afterInterceptors.Select(m => handlerFactory.CreateInterceptor(m.InterceptorType));

            foreach (var interceptor in interceptors)
                try
                {
                    await interceptor!.Intercept(message, client);
                }
                catch (Exception e)
                {
                    client.OnException(e, message);
                }
        }
    }
}