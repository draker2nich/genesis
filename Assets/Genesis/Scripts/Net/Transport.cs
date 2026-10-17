using System;
using System.Collections.Generic;

namespace Genesis.Net
{
    /// <summary>
    /// Транспорт хоста: доставка пакетов (byte[]) клиентам по id соединения.
    /// Реализации: Loopback (тесты), TCP (встроенный, для LAN/проброса порта), FishNet (Steam P2P/Relay).
    /// События вызываются только из Poll() — в главном потоке.
    /// </summary>
    public interface IServerTransport : IDisposable
    {
        event Action<int> ClientConnected;
        event Action<int> ClientDisconnected;
        event Action<int, byte[]> Received;
        void Send(int connection, byte[] data, bool reliable);
        void Disconnect(int connection);
        void Poll();
    }

    public interface IClientTransport : IDisposable
    {
        event Action Connected;
        event Action<string> Disconnected;
        event Action<byte[]> Received;
        bool IsConnected { get; }
        void Send(byte[] data, bool reliable);
        void Poll();
    }

    /// <summary>
    /// Фабрика транспортов, которую регистрирует конкретная сетевая библиотека.
    /// Клиентский код Unity не ссылается на FishNet напрямую — только на это.
    /// </summary>
    public interface ITransportProvider
    {
        string Name { get; }
        IServerTransport CreateServer(int port);
        IClientTransport CreateClient(string address, int port);
    }

    public static class TransportRegistry
    {
        private static readonly List<ITransportProvider> Providers = new List<ITransportProvider>();

        public static IReadOnlyList<ITransportProvider> All => Providers;

        public static void Register(ITransportProvider provider)
        {
            Providers.RemoveAll(p => p.Name == provider.Name);
            // Внешние библиотеки (FishNet) — в начало списка, встроенный TCP остаётся запасным.
            Providers.Insert(0, provider);
        }

        public static ITransportProvider Default
        {
            get
            {
                if (Providers.Count == 0) Providers.Add(new TcpTransportProvider());
                return Providers[0];
            }
        }

        public static void EnsureBuiltIn()
        {
            if (!Providers.Exists(p => p is TcpTransportProvider)) Providers.Add(new TcpTransportProvider());
        }
    }

    // ================================================================ Loopback

    /// <summary>Хост и клиенты в одном процессе (тесты). Пакеты доставляются на следующем Poll.</summary>
    public sealed class LoopbackServer : IServerTransport
    {
        private readonly Dictionary<int, LoopbackClient> _clients = new Dictionary<int, LoopbackClient>();
        private readonly Queue<(int, byte[])> _inbox = new Queue<(int, byte[])>();
        private readonly Queue<int> _connects = new Queue<int>();
        private readonly Queue<int> _disconnects = new Queue<int>();
        private int _nextId = 1;

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[]> Received;

        public LoopbackClient CreateClient()
        {
            var c = new LoopbackClient(this, _nextId++);
            _clients[c.Id] = c;
            _connects.Enqueue(c.Id);
            return c;
        }

        internal void FromClient(int id, byte[] data) => _inbox.Enqueue((id, data));
        internal void ClientClosed(int id) => _disconnects.Enqueue(id);

        public void Send(int connection, byte[] data, bool reliable)
        {
            if (_clients.TryGetValue(connection, out var c)) c.Deliver(data);
        }

        public void Disconnect(int connection)
        {
            if (_clients.TryGetValue(connection, out var c))
            {
                _clients.Remove(connection);
                c.ServerClosed();
                _disconnects.Enqueue(connection);
            }
        }

        public void Poll()
        {
            while (_connects.Count > 0)
            {
                int id = _connects.Dequeue();
                ClientConnected?.Invoke(id);
                if (_clients.TryGetValue(id, out var c)) c.MarkConnected();
            }
            while (_inbox.Count > 0)
            {
                var (id, data) = _inbox.Dequeue();
                Received?.Invoke(id, data);
            }
            while (_disconnects.Count > 0) ClientDisconnected?.Invoke(_disconnects.Dequeue());
        }

        public void Dispose()
        {
        }
    }

    public sealed class LoopbackClient : IClientTransport
    {
        private readonly LoopbackServer _server;
        private readonly Queue<byte[]> _inbox = new Queue<byte[]>();
        private bool _connectedPending;
        private bool _closedPending;
        public readonly int Id;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<byte[]> Received;

        public bool IsConnected { get; private set; }

        internal LoopbackClient(LoopbackServer server, int id)
        {
            _server = server;
            Id = id;
        }

        internal void MarkConnected() => _connectedPending = true;
        internal void Deliver(byte[] data) => _inbox.Enqueue(data);
        internal void ServerClosed() => _closedPending = true;

        public void Send(byte[] data, bool reliable) => _server.FromClient(Id, data);

        public void Poll()
        {
            if (_connectedPending)
            {
                _connectedPending = false;
                IsConnected = true;
                Connected?.Invoke();
            }
            while (_inbox.Count > 0) Received?.Invoke(_inbox.Dequeue());
            if (_closedPending)
            {
                _closedPending = false;
                IsConnected = false;
                Disconnected?.Invoke("closed by host");
            }
        }

        public void Dispose()
        {
            if (IsConnected) _server.ClientClosed(Id);
            IsConnected = false;
        }
    }
}
