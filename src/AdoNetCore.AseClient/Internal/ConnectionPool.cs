﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using AdoNetCore.AseClient.Interface;

namespace AdoNetCore.AseClient.Internal
{
    internal class ConnectionPool
    {
        private class PoolItem
        {
            public InternalConnection Connection { get; set; }
            public bool Available { get; set; }
            public DateTime Created { get; set; }
            public DateTime LastActive { get; set; }
        }

        private readonly ConnectionParameters _parameters;

        private IPEndPoint _endpoint;
        private readonly object _mutex = new object();

        private const int ReserveWaitPeriodMs = 5; //TODO: figure out appropriate value

        private readonly List<PoolItem> _connections;

        public ConnectionPool(ConnectionParameters parameters)
        {
            _parameters = parameters;
            _connections = new List<PoolItem>(_parameters.MaxPoolSize);
        }

        public IInternalConnection Reserve()
        {
            if (!_parameters.Pooling)
            {
                return InitialiseNewConnection();
            }

            var wait = new ManualResetEvent(false);
            InternalConnection connection = null;

            do
            {
                lock (_mutex)
                {
                    var now = DateTime.UtcNow;
                    var item = _connections.FirstOrDefault(i => i.Available);

                    if (item != null)
                    {
                        //todo: recreate connection if broken
                        item.Available = false;
                        connection = item.Connection;
                        wait.Set();
                    }

                    //determine if we can create new items
                    else if (_connections.Count < _parameters.MaxPoolSize)
                    {
                        var newConnection = InitialiseNewConnection();
                        _connections.Add(new PoolItem
                        {
                            Connection = newConnection,
                            Created = now,
                            LastActive = now,
                            Available = false
                        });

                        connection = newConnection;
                        wait.Set();
                    }

                    //todo: if we've waited long enough, set wait
                }
            } while (!wait.WaitOne(ReserveWaitPeriodMs));

            connection?.ChangeDatabase(_parameters.Database);
            return connection;
        }

        public void Release(IInternalConnection connection)
        {
            if (!_parameters.Pooling)
            {
                connection?.Dispose();
                return;
            }

            lock (_mutex)
            {
                var item = _connections.FirstOrDefault(i => i.Connection == connection);
                if (item != null)
                {
                    item.Available = true;
                    item.LastActive = DateTime.UtcNow;
                }
            }
        }

        private InternalConnection InitialiseNewConnection()
        {
            var socket = new Socket(Endpoint.AddressFamily, SocketType.Stream, ProtocolType.IP);
            socket.Connect(Endpoint);
            var connection = new InternalConnection(_parameters, new RegularSocket(socket, new TokenParser()));
            connection.Connect();
            return connection;
        }

        private EndPoint Endpoint
        {
            get
            {
                if (_endpoint == null)
                {
                    _endpoint = CreateEndpoint(_parameters.Server, _parameters.Port);
                }
                return _endpoint;
            }
        }

        private static IPEndPoint CreateEndpoint(string server, int port)
        {
            return new IPEndPoint(
                IPAddress.TryParse(server, out var ip) ? ip : ResolveAddress(server),
                port);
        }

        private static IPAddress ResolveAddress(string server)
        {
            var dnsTask = Dns.GetHostEntryAsync(server);
            dnsTask.Wait();
            return dnsTask.Result.AddressList.First();
        }
    }
}
