﻿using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;
using PacketDotNet;
using VpnHood.Common.Messaging;
using VpnHood.Tunneling;
using VpnHood.Tunneling.Factory;

namespace VpnHood.Server
{
    public class Session : IDisposable
    {
        private readonly IAccessServer _accessServer;

        private readonly SessionProxyManager _sessionProxyManager;
        private readonly SocketFactory _socketFactory;
        private readonly long _syncCacheSize;
        private readonly IPEndPoint _hostEndPoint;
        private readonly object _syncLock = new();
        private bool _isSyncing;
        private long _syncReceivedTraffic;
        private long _syncSentTraffic;

        internal Session(IAccessServer accessServer, SessionResponse sessionResponse, SocketFactory socketFactory,
            int maxDatagramChannelCount, long syncCacheSize, IPEndPoint hostEndPoint)
        {
            _accessServer = accessServer ?? throw new ArgumentNullException(nameof(accessServer));
            _sessionProxyManager = new SessionProxyManager(this);
            _socketFactory = socketFactory ?? throw new ArgumentNullException(nameof(socketFactory));
            _syncCacheSize = syncCacheSize;
            _hostEndPoint = hostEndPoint;
            SessionResponse = new ResponseBase(sessionResponse);
            SessionId = sessionResponse.SessionId;
            SessionKey = sessionResponse.SessionKey ??
                         throw new InvalidOperationException(
                             $"{nameof(sessionResponse)} does not have {nameof(sessionResponse.SessionKey)}!");
            Tunnel = new Tunnel
            {
                MaxDatagramChannelCount = maxDatagramChannelCount
            };
            Tunnel.OnPacketReceived += Tunnel_OnPacketReceived;
            Tunnel.OnTrafficChanged += Tunnel_OnTrafficChanged;
        }

        public Tunnel Tunnel { get; }
        public uint SessionId { get; }
        public byte[] SessionKey { get; }
        public ResponseBase SessionResponse { get; private set; }
        public UdpChannel? UdpChannel { get; private set; } //todo use global udp listener
        public bool IsDisposed { get; private set; }

        public int TcpConnectionCount =>
            Tunnel.StreamChannelCount + (UseUdpChannel ? 0 : Tunnel.DatagramChannels.Length);

        public int UdpConnectionCount => _sessionProxyManager.UdpConnectionCount + (UseUdpChannel ? 1 : 0);
        public DateTime LastActivityTime => Tunnel.LastActivityTime;

        public bool UseUdpChannel
        {
            get => Tunnel.DatagramChannels.Length == 1 && Tunnel.DatagramChannels[0] is UdpChannel;
            set
            {
                if (value == UseUdpChannel)
                    return;

                if (value)
                {
                    // remove tcpDatagram channels
                    foreach (var item in Tunnel.DatagramChannels.Where(x => x != UdpChannel))
                        Tunnel.RemoveChannel(item);

                    // create UdpKey
                    using var aes = Aes.Create();
                    aes.KeySize = 128;
                    aes.GenerateKey();

                    // Create the only one UdpChannel
                    UdpChannel = new UdpChannel(false, _socketFactory.CreateUdpClient(_hostEndPoint.AddressFamily), SessionId, aes.Key);
                    Tunnel.AddChannel(UdpChannel);
                }
                else
                {
                    // remove udp channels
                    foreach (var item in Tunnel.DatagramChannels.Where(x => x == UdpChannel))
                        Tunnel.RemoveChannel(item);
                    UdpChannel = null;
                }
            }
        }

        public void Dispose()
        {
            Dispose(false);
        }

        private void Tunnel_OnPacketReceived(object sender, ChannelPacketReceivedEventArgs e)
        {
            if (!IsDisposed)
                _sessionProxyManager.SendPacket(e.IpPackets);
        }

        private void Tunnel_OnTrafficChanged(object sender, EventArgs e)
        {
            _ = Sync();
        }

        private async Task Sync(bool closeSession = false)
        {
            UsageInfo usageParam;
            lock (_syncLock)
            {
                if (_isSyncing)
                    return;

                usageParam = new UsageInfo
                {
                    SentTraffic =
                        Tunnel.ReceivedByteCount -
                        _syncSentTraffic, // Intentionally Reversed: sending to tunnel means receiving form client,
                    ReceivedTraffic =
                        Tunnel.SentByteCount -
                        _syncReceivedTraffic // Intentionally Reversed: receiving from tunnel means sending for client
                };
                if (!closeSession && usageParam.SentTraffic + usageParam.ReceivedTraffic < _syncCacheSize)
                    return;
                _isSyncing = true;
            }

            try
            {
                SessionResponse = await _accessServer.Session_AddUsage(SessionId, closeSession, usageParam);
                lock (_syncLock)
                {
                    _syncSentTraffic += usageParam.SentTraffic;
                    _syncReceivedTraffic += usageParam.ReceivedTraffic;
                }

                // dispose for any error
                if (SessionResponse.ErrorCode != SessionErrorCode.Ok)
                    Dispose();
            }
            finally
            {
                lock (_syncLock)
                {
                    _isSyncing = false;
                }
            }
        }

        public void Dispose(bool closeSessionInAccessSever)
        {
            if (!IsDisposed)
            {
                Tunnel.OnPacketReceived -= Tunnel_OnPacketReceived;
                Tunnel.OnTrafficChanged -= Tunnel_OnTrafficChanged;
                Tunnel.Dispose();

                _sessionProxyManager.Dispose();
                _ = Sync(closeSessionInAccessSever);

                IsDisposed = true;
            }
        }

        private class SessionProxyManager : ProxyManager
        {
            private readonly Session _session;

            public SessionProxyManager(Session session)
            {
                _session = session;
            }

            protected override bool IsPingSupported => true;

            protected override UdpClient CreateUdpClient(AddressFamily addressFamily)
            {
                return _session._socketFactory.CreateUdpClient(addressFamily);
            }

            protected override void SendReceivedPacket(IPPacket ipPacket)
            {
                _session.Tunnel.SendPacket(ipPacket);
            }
        }
    }
}