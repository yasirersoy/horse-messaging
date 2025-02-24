using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Horse.Messaging.Protocol;

namespace Horse.Messaging.Client.Routers
{
    internal class HorseRouterBus<TIdentifier> : HorseRouterBus, IHorseRouterBus<TIdentifier>
    {
        public HorseRouterBus(HorseClient client) : base(client)
        {
        }
    }

    /// <summary>
    /// Implementation for route messages and requests
    /// </summary>
    public class HorseRouterBus : IHorseRouterBus
    {
        private readonly HorseClient _client;

        /// <summary>
        /// Creates new horse route bus
        /// </summary>
        public HorseRouterBus(HorseClient client)
        {
            _client = client;
        }

        /// <inheritdoc />
        public HorseClient GetClient()
        {
            return _client;
        }

        #region Publish

        /// <inheritdoc />
        public Task<HorseResult> Publish(string routerName,
                                         string content,
                                         bool waitForCommit = false,
                                         IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.Publish(routerName, content, null, waitForCommit, 0, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> Publish(string routerName,
                                         string content,
                                         string messageId,
                                         bool waitForCommit = false,
                                         IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.Publish(routerName, content, messageId, waitForCommit, 0, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> Publish(string routerName,
                                         MemoryStream content,
                                         bool waitForCommit = false,
                                         IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.Publish(routerName, content.ToArray(), null, waitForCommit, 0, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> Publish(string routerName,
                                         MemoryStream content,
                                         string messageId,
                                         bool waitForCommit = false,
                                         IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.Publish(routerName, content.ToArray(), messageId, waitForCommit, 0, messageHeaders);
        }

        #endregion

        #region Json

        /// <inheritdoc />
        public Task<HorseResult> PublishJson(object jsonObject,
                                             bool waitForCommit = false,
                                             IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishJson(jsonObject, waitForCommit, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> PublishJson(string routerName,
                                             object jsonObject,
                                             bool waitForCommit = false,
                                             IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return PublishJson(routerName, jsonObject, null, waitForCommit, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> PublishJson(string routerName,
                                             object jsonObject,
                                             ushort? contentType = null,
                                             bool waitForCommit = false,
                                             IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishJson(routerName, jsonObject, null, waitForCommit, contentType, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult> PublishJson(string routerName,
                                             object jsonObject,
                                             string messageId,
                                             ushort? contentType = null,
                                             bool waitForCommit = false,
                                             IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishJson(routerName, jsonObject, messageId, waitForCommit, contentType, messageHeaders);
        }

        #endregion

        #region Request

        /// <inheritdoc />
        public Task<HorseMessage> PublishRequest(string routerName,
                                                 string message,
                                                 ushort contentType = 0,
                                                 IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishRequest(routerName, message, contentType, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult<TResponse>> PublishRequestJson<TRequest, TResponse>(TRequest request,
                                                                                    IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishRequestJson<TRequest, TResponse>(request, messageHeaders);
        }

        /// <inheritdoc />
        public Task<HorseResult<TResponse>> PublishRequestJson<TRequest, TResponse>(string routerName,
                                                                                    TRequest request,
                                                                                    ushort? contentType = null,
                                                                                    IEnumerable<KeyValuePair<string, string>> messageHeaders = null)
        {
            return _client.Router.PublishRequestJson<TRequest, TResponse>(routerName, request, contentType, messageHeaders);
        }

        #endregion
    }
}