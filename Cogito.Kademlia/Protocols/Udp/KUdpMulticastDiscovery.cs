﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using Cogito.Kademlia.Core;
using Cogito.Kademlia.Network;
using Cogito.Threading;

using Microsoft.Extensions.Logging;

namespace Cogito.Kademlia.Protocols.Udp
{

    /// <summary>
    /// Listens for multicast PING requests on a multicast group and provides Connect operations for joining a UDP Kademlia network.
    /// </summary>
    /// <typeparam name="TKNodeId"></typeparam>
    public class KUdpMulticastDiscovery<TKNodeId, TKPeerData>
        where TKNodeId : unmanaged, IKNodeId<TKNodeId>
        where TKPeerData : IKEndpointProvider<TKNodeId>
    {

        static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
        static readonly Random rnd = new Random();

        readonly ulong network;
        readonly IKEngine<TKNodeId, TKPeerData> engine;
        readonly IKIpProtocol<TKNodeId> protocol;
        readonly IKMessageEncoder<TKNodeId, IKIpProtocolResourceProvider<TKNodeId>> encoder;
        readonly IKMessageDecoder<TKNodeId, IKIpProtocolResourceProvider<TKNodeId>> decoder;
        readonly KIpEndpoint endpoint;
        readonly ILogger logger;
        readonly AsyncLock sync = new AsyncLock();

        readonly KResponseQueue<TKNodeId, KPingResponse<TKNodeId>, ulong> pingQueue = new KResponseQueue<TKNodeId, KPingResponse<TKNodeId>, ulong>(DefaultTimeout);

        Socket mcastSocket;
        SocketAsyncEventArgs mcastRecvArgs;

        Socket localSocket;
        SocketAsyncEventArgs localRecvArgs;

        CancellationTokenSource runCts;
        Task run;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="network"></param>
        /// <param name="engine"></param>
        /// <param name="protocol"></param>
        /// <param name="encoder"></param>
        /// <param name="decoder"></param>
        /// <param name="endpoint"></param>
        /// <param name="logger"></param>
        public KUdpMulticastDiscovery(ulong network, IKEngine<TKNodeId, TKPeerData> engine, IKIpProtocol<TKNodeId> protocol, IKMessageEncoder<TKNodeId, IKIpProtocolResourceProvider<TKNodeId>> encoder, IKMessageDecoder<TKNodeId, IKIpProtocolResourceProvider<TKNodeId>> decoder, in KIpEndpoint endpoint, ILogger logger = null)
        {
            this.network = network;
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
            this.encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
            this.decoder = decoder ?? throw new ArgumentNullException(nameof(decoder));
            this.endpoint = endpoint;
            this.logger = logger;
        }

        /// <summary>
        /// Gets the engine associated with this protocol.
        /// </summary>
        public IKEngine<TKNodeId> Engine => engine;

        /// <summary>
        /// Gets the set of endpoints available for communication with this protocol.
        /// </summary>
        public IEnumerable<IKEndpoint<TKNodeId>> Endpoints => Enumerable.Empty<IKEndpoint<TKNodeId>>();

        /// <summary>
        /// Gets the next magic value.
        /// </summary>
        /// <returns></returns>
        ulong NewMagic()
        {
            return (ulong)rnd.NextInt64();
        }

        /// <summary>
        /// Gets the wildcard endpoint.
        /// </summary>
        KIpEndpoint IpAny => endpoint.Protocol switch
        {
            KIpAddressFamily.IPv4 => KIpEndpoint.AnyV4,
            KIpAddressFamily.IPv6 => KIpEndpoint.AnyV6,
            _ => throw new InvalidOperationException(),
        };

        /// <summary>
        /// Starts listening for announcement packets.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            using (await sync.LockAsync())
            {
                if (run != null || runCts != null)
                    throw new InvalidOperationException();

                if (mcastSocket != null)
                    throw new KProtocolException(KProtocolError.Invalid, "Discovery is already started.");

                switch (endpoint.Protocol)
                {
                    case KIpAddressFamily.IPv4:
                        logger?.LogInformation("Initializing IPv4 multicast UDP discovery on {Endpoint}.", endpoint);
                        mcastSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        mcastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        mcastSocket.Bind(new IPEndPoint(IPAddress.Any, endpoint.Port));
                        mcastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(endpoint.V4.ToIPAddress(), IPAddress.Any));
                        mcastSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                        mcastRecvArgs = new SocketAsyncEventArgs();
                        mcastRecvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

                        localSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        localSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                        localSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                        localSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
                        localRecvArgs = new SocketAsyncEventArgs();
                        localRecvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                        break;
                    case KIpAddressFamily.IPv6:
                        logger?.LogInformation("Initializing IPv6 multicast UDP discovery on {Endpoint}.", endpoint);
                        mcastSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                        mcastSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                        mcastSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, endpoint.Port));
                        mcastSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.AddMembership, new MulticastOption(endpoint.V6.ToIPAddress(), IPAddress.IPv6Any));
                        mcastSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.MulticastTimeToLive, 2);
                        mcastRecvArgs = new SocketAsyncEventArgs();
                        mcastRecvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);

                        localSocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                        localSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, true);
                        localSocket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.PacketInformation, true);
                        localSocket.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
                        localRecvArgs = new SocketAsyncEventArgs();
                        localRecvArgs.RemoteEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
                        break;
                    default:
                        throw new InvalidOperationException();
                }

                mcastRecvArgs.SetBuffer(new byte[8192], 0, 8192);
                mcastRecvArgs.Completed += recvArgs_Completed;
                localRecvArgs.SetBuffer(new byte[8192], 0, 8192);
                localRecvArgs.Completed += recvArgs_Completed;

                logger?.LogInformation("Waiting for incoming multicast announcement packets.");
                mcastSocket.ReceiveMessageFromAsync(mcastRecvArgs);
                localSocket.ReceiveMessageFromAsync(localRecvArgs);

                // begin new run processes
                runCts = new CancellationTokenSource();
                run = Task.WhenAll(Task.Run(() => ConnectRunAsync(runCts.Token)));
            }
        }

        /// <summary>
        /// Stops the processes of the engine.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            using (await sync.LockAsync(cancellationToken))
            {
                // shutdown socket
                if (mcastSocket != null)
                {
                    // swap for null
                    var s = mcastSocket;
                    mcastSocket = null;

                    try
                    {
                        s.Close();
                        s.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {

                    }
                }

                // shutdown socket
                if (localSocket != null)
                {
                    // swap for null
                    var s = localSocket;
                    localSocket = null;

                    try
                    {
                        s.Close();
                        s.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {

                    }
                }

                if (runCts != null)
                {
                    runCts.Cancel();
                    runCts = null;
                }

                if (run != null)
                {
                    try
                    {
                        await run;
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore
                    }
                }
            }
        }

        /// <summary>
        /// Invoked when a receive operation completes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        void recvArgs_Completed(object sender, SocketAsyncEventArgs args)
        {
            // should only be receiving packets from our message loop
            if (args.LastOperation != SocketAsyncOperation.ReceiveMessageFrom)
            {
                logger?.LogTrace("Unexpected packet operation {Operation}.", args.LastOperation);
                return;
            }

            logger?.LogInformation("Received incoming UDP packet of {Size} from {Endpoint}.", args.BytesTransferred, (IPEndPoint)args.RemoteEndPoint);
            var p = new KIpEndpoint((IPEndPoint)args.RemoteEndPoint);
            var b = new ReadOnlySpan<byte>(args.Buffer, args.Offset, args.BytesTransferred);
            var o = MemoryPool<byte>.Shared.Rent(b.Length);
            var m = o.Memory.Slice(0, b.Length);
            b.CopyTo(m.Span);
            Task.Run(async () => { try { await OnReceiveAsync(p, m.Span, CancellationToken.None); } catch { } finally { o.Dispose(); } });

            // continue receiving if socket still available
            // this lock is blocking, but should be okay since this event handler can stall
            using (sync.LockAsync().Result)
            {
                var s = (Socket)sender;
                if (s != null)
                {
                    // reset remote endpoint
                    args.RemoteEndPoint = p.Protocol switch
                    {
                        KIpAddressFamily.IPv4 => new IPEndPoint(IPAddress.Any, 0),
                        KIpAddressFamily.IPv6 => new IPEndPoint(IPAddress.IPv6Any, 0),
                        _ => throw new InvalidOperationException(),
                    };

                    try
                    {
                        s.ReceiveMessageFromAsync(args);
                    }
                    catch (ObjectDisposedException)
                    {
                        // we must have been terminated, ignore
                    }
                }
            }
        }

        /// <summary>
        /// Periodically publishes key/value pairs to the appropriate nodes.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async Task ConnectRunAsync(CancellationToken cancellationToken)
        {
            while (cancellationToken.IsCancellationRequested == false)
            {
                try
                {
                    logger?.LogInformation("Initiating periodic multicast bootstrap.");
                    await ConnectAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // ignore
                }
                catch (Exception e)
                {
                    logger?.LogError(e, "Unexpected exception occurred during multicast bootstrapping.");
                }

                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
            }
        }

        /// <summary>
        /// Attempts to bootstrap the Kademlia engine from the available multicast group members.
        /// </summary>
        /// <returns></returns>
        async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken = CancellationTokenSource.CreateLinkedTokenSource(new CancellationTokenSource(DefaultTimeout).Token, cancellationToken).Token;

            try
            {
                // only allow connect to run for so long

                // ping the received endpoints
                var r = await PingAsync(new KPingRequest<TKNodeId>(engine.SelfData.Endpoints.ToArray()), cancellationToken);
                if (r.Status == KResponseStatus.Failure)
                {
                    logger?.LogError("Unable to PING multicast address");
                    return;
                }

                // initiate connection to received endpoints
                await engine.ConnectAsync(new KEndpointSet<TKNodeId>(r.Body.Endpoints), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (KProtocolException e) when (e.Error == KProtocolError.EndpointNotAvailable)
            {
                // ignore
            }
        }

        /// <summary>
        /// Invoked when a datagram is received.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="packet"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        ValueTask OnReceiveAsync(in KIpEndpoint endpoint, ReadOnlySpan<byte> packet, CancellationToken cancellationToken)
        {
            // check for continued connection
            var s = mcastSocket;
            if (s == null || s.IsBound == false)
                return new ValueTask(Task.CompletedTask);

            // decode incoming byte sequence
            var l = decoder.Decode(protocol, new ReadOnlySequence<byte>(packet.ToArray()));
            if (l.Network != network)
            {
                logger?.LogWarning("Received unexpected message sequence for network {NetworkId}.", l.Network);
                return new ValueTask(Task.CompletedTask);
            }

            var t = new List<Task>();

            // dispatch individual messages into infrastructure
            foreach (var m in l)
            {
                // skip messages sent from ourselves
                if (m.Header.Sender.Equals(engine.SelfId))
                    continue;

                t.Add(m switch
                {
                    KMessage<TKNodeId, KPingRequest<TKNodeId>> r => OnReceivePingRequestAsync(endpoint, r, cancellationToken).AsTask(),
                    KMessage<TKNodeId, KPingResponse<TKNodeId>> r => OnReceivePingResponseAsync(endpoint, r, cancellationToken).AsTask(),
                    _ => Task.CompletedTask,
                });
            }

            // return when all complete
            return new ValueTask(Task.WhenAll(t));
        }

        /// <summary>
        /// Initiates a send of the buffered data to the endpoint.
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="endpoint"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        ValueTask SocketSendToAsync(ArrayBufferWriter<byte> buffer, KIpEndpoint endpoint, CancellationToken cancellationToken)
        {
            var s = localSocket;
            if (s == null)
                throw new KProtocolException(KProtocolError.ProtocolNotAvailable, "Cannot send. Socket no longer available.");

            var z = new byte[buffer.WrittenCount];
            buffer.WrittenSpan.CopyTo(z);
            return new ValueTask(s.SendToAsync(new ArraySegment<byte>(z), SocketFlags.None, endpoint.ToIPEndPoint()));
        }

        /// <summary>
        /// Sends the given buffer to an endpoint and begins a wait on the specified reply queue.
        /// </summary>
        /// <typeparam name="TResponseData"></typeparam>
        /// <param name="magic"></param>
        /// <param name="queue"></param>
        /// <param name="buffer"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KResponse<TKNodeId, TResponseData>> SendAndWaitAsync<TResponseData>(ulong magic, KResponseQueue<TKNodeId, TResponseData, ulong> queue, ArrayBufferWriter<byte> buffer, CancellationToken cancellationToken)
            where TResponseData : struct, IKResponseData<TKNodeId>
        {
            logger?.LogDebug("Queuing response wait for {Magic}.", magic);

            var c = new CancellationTokenSource();
            var t = queue.WaitAsync(magic, CancellationTokenSource.CreateLinkedTokenSource(c.Token, cancellationToken).Token);

            try
            {
                logger?.LogDebug("Sending packet to {Endpoint} with {Magic}.", endpoint, magic);
                await SocketSendToAsync(buffer, endpoint.ToIPEndPoint(), cancellationToken);
            }
            catch (Exception)
            {
                // cancel item in response queue
                c.Cancel();
                throw;
            }

            // wait on response
            var r = await t;
            logger?.LogDebug("Exited wait for {Magic} to {Endpoint}.", magic, IpAny);
            return r;
        }

        /// <summary>
        /// Packages up a new message originating from this host.
        /// </summary>
        /// <typeparam name="TBody"></typeparam>
        /// <param name="magic"></param>
        /// <param name="body"></param>
        /// <returns></returns>
        KMessageSequence<TKNodeId> PackageMessage<TBody>(ulong magic, TBody body)
            where TBody : struct, IKMessageBody<TKNodeId>
        {
            return new KMessageSequence<TKNodeId>(network, new[] { (IKMessage<TKNodeId>)new KMessage<TKNodeId, TBody>(new KMessageHeader<TKNodeId>(engine.SelfId, magic), body) });
        }

        /// <summary>
        /// Initiates a PING to the multicast endpoint.
        /// </summary>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask<KResponse<TKNodeId, KPingResponse<TKNodeId>>> PingAsync(KPingRequest<TKNodeId> request, CancellationToken cancellationToken)
        {
            try
            {
                var m = NewMagic();
                var b = new ArrayBufferWriter<byte>();
                encoder.Encode(protocol, b, PackageMessage(m, request));
                return await SendAndWaitAsync(m, pingQueue, b, cancellationToken);
            }
            catch (KProtocolException e) when (e.Error == KProtocolError.EndpointNotAvailable)
            {
                logger?.LogError("No response received attempting to ping multicast peers.");
                return default;
            }
        }

        /// <summary>
        /// Invoked when a PING request is received.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="request"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        async ValueTask OnReceivePingRequestAsync(KIpEndpoint endpoint, KMessage<TKNodeId, KPingRequest<TKNodeId>> request, CancellationToken cancellationToken)
        {
            logger?.LogDebug("Received multicast PING:{Magic} from {Sender} at {Endpoint}.", request.Header.Magic, request.Header.Sender, endpoint);
            await SendPingReplyAsync(endpoint, request.Header.Magic, await engine.OnPingAsync(request.Header.Sender, request.Body.Endpoints.FirstOrDefault(), request.Body, cancellationToken), cancellationToken);
        }

        ValueTask SendPingReplyAsync(in KIpEndpoint endpoint, ulong magic, in KPingResponse<TKNodeId> response, CancellationToken cancellationToken)
        {
            logger?.LogDebug("Sending multicast PING:{Magic} reply to {Endpoint}.", magic, endpoint);
            var b = new ArrayBufferWriter<byte>();
            encoder.Encode(protocol, b, PackageMessage(magic, response));
            return SocketSendToAsync(b, endpoint, cancellationToken);
        }

        /// <summary>
        /// Invoked with a PING response is received.
        /// </summary>
        /// <param name="endpoint"></param>
        /// <param name="response"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        ValueTask OnReceivePingResponseAsync(KIpEndpoint endpoint, KMessage<TKNodeId, KPingResponse<TKNodeId>> response, CancellationToken cancellationToken)
        {
            pingQueue.Respond(response.Header.Magic, new KResponse<TKNodeId, KPingResponse<TKNodeId>>(response.Body.Endpoints.FirstOrDefault(), response.Header.Sender, KResponseStatus.Success, response.Body));
            return new ValueTask(Task.CompletedTask);
        }

    }

}
