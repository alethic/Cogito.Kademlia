﻿using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;

namespace Alethic.Kademlia
{

    /// <summary>
    /// Handles incoming requests.
    /// </summary>
    /// <typeparam name="TNodeId"></typeparam>
    public class KRequestHandler<TNodeId> : IKRequestHandler<TNodeId>
        where TNodeId : unmanaged
    {

        readonly IKHost<TNodeId> host;
        readonly IKRouter<TNodeId> router;
        readonly IKStore<TNodeId> store;
        readonly ILogger logger;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="host"></param>
        /// <param name="router"></param>
        /// <param name="store"></param>
        /// <param name="logger"></param>
        public KRequestHandler(IKHost<TNodeId> host, IKRouter<TNodeId> router, IKStore<TNodeId> store, ILogger logger)
        {
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.router = router ?? throw new ArgumentNullException(nameof(router));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Invoked to handle incoming PING requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public ValueTask<KPingResponse<TNodeId>> OnPingAsync(in TNodeId sender, in KPingRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            logger.LogDebug("Processing {Operation} from {Sender}.", "PING", sender);
            return OnPingAsync(sender, request, cancellationToken);
        }

        /// <summary>
        /// Invoked to handle incoming PING requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KPingResponse<TNodeId>> OnPingAsync(TNodeId sender, KPingRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            await router.UpdateAsync(sender, request.Endpoints.Select(i => host.ResolveEndpoint(i)), cancellationToken);

            return request.Respond(host.Endpoints);
        }

        /// <summary>
        /// Invoked to handle incoming STORE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public ValueTask<KStoreResponse<TNodeId>> OnStoreAsync(in TNodeId sender, in KStoreRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            logger.LogDebug("Processing {Operation} from {Sender}.", "STORE", sender);
            return OnStoreAsync(sender, request, cancellationToken);
        }

        /// <summary>
        /// Invoked to handle incoming STORE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KStoreResponse<TNodeId>> OnStoreAsync(TNodeId sender, KStoreRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            await router.UpdateAsync(sender, null, cancellationToken);
            await store.SetAsync(request.Key, ToStoreMode(request.Mode), request.Value, cancellationToken);

            return request.Respond(KStoreResponseStatus.Success);
        }

        /// <summary>
        /// Converts the store request mode into the store setter mode.
        /// </summary>
        /// <param name="mode"></param>
        /// <returns></returns>
        KStoreValueMode ToStoreMode(KStoreRequestMode mode)
        {
            return mode switch
            {
                KStoreRequestMode.Primary => KStoreValueMode.Primary,
                KStoreRequestMode.Replica => KStoreValueMode.Replica,
                _ => throw new InvalidOperationException(),
            };
        }

        /// <summary>
        /// Invoked to handle incoming FIND_NODE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public ValueTask<KFindNodeResponse<TNodeId>> OnFindNodeAsync(in TNodeId sender, in KFindNodeRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            logger.LogDebug("Processing {Operation} from {Sender}.", "FIND_NODE", sender);
            return OnFindNodeAsync(sender, request, cancellationToken);
        }

        /// <summary>
        /// Invoked to handle incoming FIND_NODE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KFindNodeResponse<TNodeId>> OnFindNodeAsync(TNodeId sender, KFindNodeRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            await router.UpdateAsync(sender, null, cancellationToken);

#if NETSTANDARD2_1
            var l = await router.SelectAsync(request.Key, router.K, cancellationToken).ToArrayAsync();
#else
            var l = await router.SelectAsync(request.Key, router.K, cancellationToken);
#endif
            return request.Respond(l.Select(j => new KNodeInfo<TNodeId>(j.Id, j.Endpoints.Select(k => k.ToUri()))));
        }

        /// <summary>
        /// Invoked to handle incoming FIND_VALUE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public ValueTask<KFindValueResponse<TNodeId>> OnFindValueAsync(in TNodeId sender, in KFindValueRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            logger.LogDebug("Processing {Operation} from {Sender}.", "FIND_VALUE", sender);
            return OnFindValueAsync(sender, request, cancellationToken);
        }

        /// <summary>
        /// Invoked to handle incoming FIND_VALUE requests.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KFindValueResponse<TNodeId>> OnFindValueAsync(TNodeId sender, KFindValueRequest<TNodeId> request, CancellationToken cancellationToken)
        {
            await router.UpdateAsync(sender, null, cancellationToken);
            var r = await store.GetAsync(request.Key);

#if NETSTANDARD2_1
            var l = await router.SelectAsync(request.Key, router.K, cancellationToken).ToArrayAsync(cancellationToken);
#else
            var l = await router.SelectAsync(request.Key, router.K, cancellationToken);
#endif
            return request.Respond(l.Select(i => new KNodeInfo<TNodeId>(i.Id, i.Endpoints.Select(i => i.ToUri()))), r);
        }

    }

}
