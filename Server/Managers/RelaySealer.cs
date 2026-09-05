using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using GTANetworkServer.Constant;
using GTANetworkShared.Crypto;
using Lidgren.Network;

namespace GTANetworkServer.Managers
{
    /// <summary>
    /// Takes the per-recipient work of a send off the tick thread (T-023). The tick thread hands over one payload and the
    /// recipients; a few workers make the per-connection message (a copy sealed with that session's cipher — one AES-GCM call
    /// per recipient, ~0.5 µs each on .NET/OpenSSL — or a plain copy) and enqueue it in Lidgren. A connection always maps to
    /// the same worker (its unique identifier modulo the worker count), so the order of messages to one client is the order
    /// they were submitted in, and each session's counter is only touched by one thread.
    /// When a worker's queue is full, unreliable messages are dropped (the next sync packet supersedes them — the tick thread
    /// never waits for the network); reliable ones wait for room.
    /// </summary>
    internal sealed class RelaySealer
    {
        private const int QueueCapacity = 16384;   // per worker; at 650 k messages/s over 4 workers about 100 ms of work

        private readonly struct Item
        {
            public readonly NetConnection Connection;
            public readonly byte[] Payload;
            public readonly int Length;
            public readonly NetDeliveryMethod Method;
            public readonly int Channel;

            public Item(NetConnection connection, byte[] payload, int length, NetDeliveryMethod method, int channel)
            {
                Connection = connection; Payload = payload; Length = length; Method = method; Channel = channel;
            }
        }

        private readonly NetServer _server;
        private readonly BlockingCollection<Item>[] _queues;
        private readonly Thread[] _workers;
        private long _dropped, _lidgrenDropped;
        private volatile bool _running;

        public RelaySealer(NetServer server, int workers)
        {
            _server = server;
            _queues = new BlockingCollection<Item>[workers];
            _workers = new Thread[workers];
            for (var i = 0; i < workers; i++)
            {
                _queues[i] = new BlockingCollection<Item>(new ConcurrentQueue<Item>(), QueueCapacity);
                var queue = _queues[i];
                _workers[i] = new Thread(() => Work(queue)) { IsBackground = true, Name = "relay-" + i, Priority = ThreadPriority.AboveNormal };
            }
        }

        public int Workers => _workers.Length;
        public bool Running => _running;
        public long Dropped => Interlocked.Read(ref _dropped);
        /// <summary>Messages Lidgren refused at enqueue (its unreliable send window of 64 per connection was full).</summary>
        public long LidgrenDropped => Interlocked.Read(ref _lidgrenDropped);

        /// <summary>Messages waiting in the workers' queues right now.</summary>
        public int Queued
        {
            get { var n = 0; foreach (var q in _queues) n += q.Count; return n; }
        }

        public void Start()
        {
            _running = true;
            foreach (var w in _workers) w.Start();
        }

        /// <summary>Lets the workers drain what is queued, then stops them.</summary>
        public void Stop()
        {
            _running = false;
            foreach (var q in _queues) q.CompleteAdding();
            foreach (var w in _workers) w.Join(3000);
        }

        /// <summary>One payload for one recipient.</summary>
        public void Enqueue(NetConnection connection, byte[] payload, int length, NetDeliveryMethod method, int channel)
        {
            if (connection == null) return;
            Push(_queues[WorkerOf(connection)], new Item(connection, payload, length, method, channel));
        }

        /// <summary>One payload for many recipients: the payload array is shared (read-only) between the items.</summary>
        public void Enqueue(IList<NetConnection> recipients, byte[] payload, int length, NetDeliveryMethod method, int channel)
        {
            for (var i = 0; i < recipients.Count; i++)
            {
                var connection = recipients[i];
                if (connection == null) continue;
                Push(_queues[WorkerOf(connection)], new Item(connection, payload, length, method, channel));
            }
        }

        private int WorkerOf(NetConnection connection)
        {
            return (int)((ulong)connection.RemoteUniqueIdentifier % (ulong)_queues.Length);
        }

        private void Push(BlockingCollection<Item> queue, Item item)
        {
            try
            {
                if (item.Method == NetDeliveryMethod.Unreliable || item.Method == NetDeliveryMethod.UnreliableSequenced)
                {
                    if (!queue.TryAdd(item)) Interlocked.Increment(ref _dropped);   // superseded by the next sync packet
                }
                else
                {
                    queue.Add(item);   // reliable: wait for room rather than lose it
                }
            }
            catch (InvalidOperationException)
            {
                // completed while shutting down: nothing to deliver any more
            }
        }

        private void Work(BlockingCollection<Item> queue)
        {
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    var session = (item.Connection.Tag as Client)?.Session;
                    NetOutgoingMessage msg;
                    if (session != null)
                    {
                        msg = _server.CreateMessage(item.Length + SessionCipher.Overhead);
                        var total = session.Cipher.SealInto(item.Payload, 0, item.Length, msg.Data, 0);
                        msg.LengthBytes = total;
                    }
                    else
                    {
                        msg = _server.CreateMessage(item.Length);
                        msg.Write(item.Payload, 0, item.Length);
                    }
                    if (_server.SendMessage(msg, item.Connection, item.Method, item.Channel) == NetSendResult.Dropped) Interlocked.Increment(ref _lidgrenDropped);
                }
                catch (Exception ex)
                {
                    Program.Output("Relay worker: " + ex.GetType().Name + ": " + ex.Message, LogCat.Warn);
                }
            }
        }
    }
}
