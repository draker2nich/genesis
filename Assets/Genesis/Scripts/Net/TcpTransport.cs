using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Genesis.Net
{
    /// <summary>
    /// Встроенный TCP-транспорт без внешних зависимостей: LAN, Hamachi/Radmin, проброс порта.
    /// Кадры: [int32 длина][данные]. Чтение и запись — в фоновых потоках, события — в Poll().
    /// Для Steam (без проброса портов) используется FishNet-провайдер.
    /// </summary>
    public sealed class TcpTransportProvider : ITransportProvider
    {
        public string Name => "TCP";
        public IServerTransport CreateServer(int port) => new TcpServerTransport(port);
        public IClientTransport CreateClient(string address, int port) => new TcpClientTransport(address, port);
    }

    internal sealed class TcpPeer
    {
        public readonly int Id;
        public readonly TcpClient Client;
        public readonly NetworkStream Stream;
        public readonly BlockingCollection<byte[]> Outbox = new BlockingCollection<byte[]>();
        public volatile bool Closed;
        private volatile bool _closeAfterFlush;
        private Timer _closeTimer;
        /// <summary>Байт в очереди отправки (медленный клиент не должен раздувать память хоста без предела).</summary>
        private long _pendingBytes;
        private int _pendingCount;

        /// <summary>Очередь больше — клиент отключается как «слишком медленный».</summary>
        public const long MaxPendingBytes = 48L * 1024 * 1024;
        /// <summary>Очередь длиннее — необязательные (unreliable) пакеты пропускаются.</summary>
        public const int UnreliableBacklog = 256;

        public TcpPeer(int id, TcpClient client)
        {
            Id = id;
            Client = client;
            Client.NoDelay = true;
            try
            {
                // TCP keepalive: ОС сама обнаружит «мёртвое» соединение (плюс тайм-аут на уровне игры)
                Client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            }
            catch (Exception)
            {
                // не все платформы поддерживают
            }
            Stream = client.GetStream();
        }

        /// <summary>Поставить пакет в очередь. false — пакет отброшен (переполнение) или соединение закрыто.</summary>
        public bool Enqueue(byte[] data, bool reliable)
        {
            if (Closed || _closeAfterFlush) return false;
            if (!reliable && Volatile.Read(ref _pendingCount) > UnreliableBacklog) return false;
            if (Interlocked.Read(ref _pendingBytes) + data.Length > MaxPendingBytes)
            {
                Close(); // клиент не успевает принимать — отключаем, а не копим гигабайты
                return false;
            }
            try
            {
                Interlocked.Add(ref _pendingBytes, data.Length);
                Interlocked.Increment(ref _pendingCount);
                Outbox.Add(data);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        /// <summary>
        /// Закрыть после отправки уже поставленного в очередь (например, причина отказа в подключении):
        /// раньше сокет закрывался сразу, и клиент видел «connection closed» вместо причины.
        /// </summary>
        public void CloseAfterFlush()
        {
            if (Closed || _closeAfterFlush) return;
            _closeAfterFlush = true;
            try { Outbox.CompleteAdding(); } catch (Exception) { }
            // Страховка: если писатель завис на медленной сети — закрыть через 3 с
            _closeTimer = new Timer(_ => Close(), null, 3000, Timeout.Infinite);
        }

        public const int MaxFrame = 16 * 1024 * 1024;

        public static byte[] ReadFrame(Stream s)
        {
            var header = ReadExact(s, 4);
            if (header == null) return null;
            int len = BitConverter.ToInt32(header, 0);
            if (len < 0 || len > MaxFrame) throw new InvalidDataException("bad frame length " + len);
            return len == 0 ? Array.Empty<byte>() : ReadExact(s, len);
        }

        private static byte[] ReadExact(Stream s, int n)
        {
            var buf = new byte[n];
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        public void WriterLoop()
        {
            try
            {
                foreach (var data in Outbox.GetConsumingEnumerable())
                {
                    var header = BitConverter.GetBytes(data.Length);
                    Stream.Write(header, 0, 4);
                    Stream.Write(data, 0, data.Length);
                    Interlocked.Add(ref _pendingBytes, -data.Length);
                    Interlocked.Decrement(ref _pendingCount);
                }
                if (_closeAfterFlush)
                {
                    try { Stream.Flush(); } catch (Exception) { }
                    Close();
                }
            }
            catch (Exception)
            {
                // соединение закрыто — читатель сообщит об отключении
            }
        }

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            try { Outbox.CompleteAdding(); } catch (Exception) { }
            try { Client.Close(); } catch (Exception) { }
        }
    }

    public sealed class TcpServerTransport : IServerTransport
    {
        private readonly TcpListener _listener;
        private readonly ConcurrentDictionary<int, TcpPeer> _peers = new ConcurrentDictionary<int, TcpPeer>();
        private readonly ConcurrentQueue<Action> _events = new ConcurrentQueue<Action>();
        private int _nextId;
        private volatile bool _running = true;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[]> Received;

        public TcpServerTransport(int port)
        {
            // IPv6 + IPv4 (dual mode), если ОС умеет; иначе только IPv4
            try
            {
                _listener = new TcpListener(IPAddress.IPv6Any, port);
                _listener.Server.DualMode = true;
                _listener.Start();
            }
            catch (Exception)
            {
                try { _listener?.Stop(); } catch (Exception) { }
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
            }
            new Thread(AcceptLoop) { IsBackground = true, Name = "TcpAccept" }.Start();
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }
                int id = Interlocked.Increment(ref _nextId);
                var peer = new TcpPeer(id, client);
                _peers[id] = peer;
                _events.Enqueue(() => ClientConnected?.Invoke(id));
                new Thread(() => ReadLoop(peer)) { IsBackground = true, Name = "TcpRead-" + id }.Start();
                new Thread(peer.WriterLoop) { IsBackground = true, Name = "TcpWrite-" + id }.Start();
            }
        }

        private void ReadLoop(TcpPeer peer)
        {
            try
            {
                while (_running && !peer.Closed)
                {
                    var frame = TcpPeer.ReadFrame(peer.Stream);
                    if (frame == null) break;
                    _events.Enqueue(() => Received?.Invoke(peer.Id, frame));
                }
            }
            catch (Exception)
            {
                // обрыв соединения
            }
            peer.Close();
            if (_peers.TryRemove(peer.Id, out _))
                _events.Enqueue(() => ClientDisconnected?.Invoke(peer.Id));
        }

        public void Send(int connection, byte[] data, bool reliable)
        {
            if (_peers.TryGetValue(connection, out var p) && !p.Closed) p.Enqueue(data, reliable);
        }

        /// <summary>Отключить, сначала доставив то, что уже в очереди (причина отказа и т. п.).</summary>
        public void Disconnect(int connection)
        {
            if (_peers.TryGetValue(connection, out var p)) p.CloseAfterFlush();
        }

        public void Poll()
        {
            while (_events.TryDequeue(out var e)) e();
        }

        public void Dispose()
        {
            _running = false;
            try { _listener.Stop(); } catch (Exception) { }
            foreach (var p in _peers.Values) p.Close();
        }
    }

    public sealed class TcpClientTransport : IClientTransport
    {
        private readonly ConcurrentQueue<Action> _events = new ConcurrentQueue<Action>();
        private TcpPeer _peer;
        private volatile bool _connected;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<byte[]> Received;

        public bool IsConnected => _connected;

        public TcpClientTransport(string address, int port)
        {
            new Thread(() => Run(address, port)) { IsBackground = true, Name = "TcpClient" }.Start();
        }

        private void Run(string address, int port)
        {
            string reason = "connection closed";
            try
            {
                var client = new TcpClient();
                client.Connect(address, port);
                _peer = new TcpPeer(0, client);
                new Thread(_peer.WriterLoop) { IsBackground = true, Name = "TcpClientWrite" }.Start();
                _connected = true;
                _events.Enqueue(() => Connected?.Invoke());
                while (!_peer.Closed)
                {
                    var frame = TcpPeer.ReadFrame(_peer.Stream);
                    if (frame == null) break;
                    _events.Enqueue(() => Received?.Invoke(frame));
                }
            }
            catch (Exception e)
            {
                reason = e.Message;
            }
            _connected = false;
            _peer?.Close();
            _events.Enqueue(() => Disconnected?.Invoke(reason));
        }

        public void Send(byte[] data, bool reliable)
        {
            var p = _peer;
            if (p == null || p.Closed) return;
            p.Enqueue(data, reliable);
        }

        public void Poll()
        {
            while (_events.TryDequeue(out var e)) e();
        }

        public void Dispose()
        {
            _peer?.Close();
            _connected = false;
        }
    }
}
