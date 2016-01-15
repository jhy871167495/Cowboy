﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Cowboy.Logging;

namespace Cowboy.Sockets
{
    public class AsyncTcpSocketServer
    {
        #region Fields

        private static readonly ILog _log = Logger.Get<AsyncTcpSocketServer>();
        private IBufferManager _bufferManager;
        private TcpListener _listener;
        private readonly ConcurrentDictionary<string, AsyncTcpSocketSession> _sessions = new ConcurrentDictionary<string, AsyncTcpSocketSession>();
        private readonly IAsyncTcpSocketServerMessageDispatcher _dispatcher;
        private readonly AsyncTcpSocketServerConfiguration _configuration;

        private int _state;
        private const int _none = 0;
        private const int _listening = 1;
        private const int _disposed = 5;

        #endregion

        #region Constructors

        public AsyncTcpSocketServer(int listenedPort, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(IPAddress.Any, listenedPort, dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPAddress listenedAddress, int listenedPort, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
            : this(new IPEndPoint(listenedAddress, listenedPort), dispatcher, configuration)
        {
        }

        public AsyncTcpSocketServer(IPEndPoint listenedEndPoint, IAsyncTcpSocketServerMessageDispatcher dispatcher, AsyncTcpSocketServerConfiguration configuration = null)
        {
            if (listenedEndPoint == null)
                throw new ArgumentNullException("listenedEndPoint");
            if (dispatcher == null)
                throw new ArgumentNullException("dispatcher");

            this.ListenedEndPoint = listenedEndPoint;
            _dispatcher = dispatcher;
            _configuration = configuration ?? new AsyncTcpSocketServerConfiguration();

            Initialize();
        }

        public AsyncTcpSocketServer(
            int listenedPort,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(IPAddress.Any, listenedPort, onSessionDataReceived, onSessionStarted, onSessionClosed, configuration)
        {
        }

        public AsyncTcpSocketServer(
            IPAddress listenedAddress, int listenedPort,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(new IPEndPoint(listenedAddress, listenedPort), onSessionDataReceived, onSessionStarted, onSessionClosed, configuration)
        {
        }

        public AsyncTcpSocketServer(
            IPEndPoint listenedEndPoint,
            Func<AsyncTcpSocketSession, byte[], int, int, Task> onSessionDataReceived = null,
            Func<AsyncTcpSocketSession, Task> onSessionStarted = null,
            Func<AsyncTcpSocketSession, Task> onSessionClosed = null,
            AsyncTcpSocketServerConfiguration configuration = null)
            : this(listenedEndPoint,
                  new InternalAsyncTcpSocketServerMessageDispatcherImplementation(onSessionDataReceived, onSessionStarted, onSessionClosed),
                  configuration)
        {
        }

        private void Initialize()
        {
            _bufferManager = new GrowingByteBufferManager(_configuration.InitialBufferAllocationCount, _configuration.ReceiveBufferSize);
        }

        #endregion

        #region Properties

        public IPEndPoint ListenedEndPoint { get; private set; }
        public bool Active { get { return _state == _listening; } }
        public int SessionCount { get { return _sessions.Count; } }

        #endregion

        #region Server

        public void Start()
        {
            int origin = Interlocked.CompareExchange(ref _state, _listening, _none);
            if (origin == _disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
            else if (origin != _none)
            {
                throw new InvalidOperationException("This tcp server has already started.");
            }

            try
            {
                _listener = new TcpListener(this.ListenedEndPoint);
                ConfigureListener();

                _listener.Start(_configuration.PendingConnectionBacklog);

                Task.Run(async () =>
                {
                    await Accept();
                })
                .Forget();
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        public async Task Stop()
        {
            if (Interlocked.Exchange(ref _state, _disposed) == _disposed)
            {
                return;
            }

            try
            {
                _listener.Stop();
                _listener = null;

                foreach (var session in _sessions.Values)
                {
                    await session.Close();
                }
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        private void ConfigureListener()
        {
            _listener.AllowNatTraversal(_configuration.AllowNatTraversal);
        }

        public bool Pending()
        {
            if (!Active)
                throw new InvalidOperationException("The tcp server is not active.");

            // determine if there are pending connection requests.
            return _listener.Pending();
        }

        private async Task Accept()
        {
            try
            {
                while (Active)
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    var session = new AsyncTcpSocketSession(tcpClient, _configuration, _bufferManager, _dispatcher, this);
                    Task.Run(async () =>
                    {
                        await Process(session);
                    })
                    .Forget();
                }
            }
            catch (Exception ex) when (!ShouldThrow(ex)) { }
        }

        private async Task Process(AsyncTcpSocketSession session)
        {
            if (_sessions.TryAdd(session.SessionKey, session))
            {
                _log.DebugFormat("New session [{0}].", session);
                try
                {
                    await session.Start();
                }
                catch (TimeoutException ex)
                {
                    _log.Error(ex.Message, ex);
                }
                finally
                {
                    AsyncTcpSocketSession throwAway;
                    if (_sessions.TryRemove(session.SessionKey, out throwAway))
                    {
                        _log.DebugFormat("Close session [{0}].", throwAway);
                    }
                }
            }
        }

        private bool ShouldThrow(Exception ex)
        {
            if (ex is ObjectDisposedException
                || ex is InvalidOperationException
                || ex is SocketException
                || ex is IOException)
            {
                return false;
            }
            return true;
        }

        #endregion

        #region Send

        public async Task SendTo(string sessionKey, byte[] data)
        {
            await SendTo(sessionKey, data, 0, data.Length);
        }

        public async Task SendTo(string sessionKey, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(sessionKey, out sessionFound))
            {
                await sessionFound.Send(data, offset, count);
            }
            else
            {
                _log.WarnFormat("Cannot find session [{0}].", sessionKey);
            }
        }

        public async Task SendTo(AsyncTcpSocketSession session, byte[] data)
        {
            await SendTo(session, data, 0, data.Length);
        }

        public async Task SendTo(AsyncTcpSocketSession session, byte[] data, int offset, int count)
        {
            AsyncTcpSocketSession sessionFound;
            if (_sessions.TryGetValue(session.SessionKey, out sessionFound))
            {
                await sessionFound.Send(data, offset, count);
            }
            else
            {
                _log.WarnFormat("Cannot find session [{0}].", session);
            }
        }

        public async Task Broadcast(byte[] data)
        {
            await Broadcast(data, 0, data.Length);
        }

        public async Task Broadcast(byte[] data, int offset, int count)
        {
            foreach (var session in _sessions.Values)
            {
                await session.Send(data, offset, count);
            }
        }

        #endregion
    }
}
