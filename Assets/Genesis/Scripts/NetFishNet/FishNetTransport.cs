using System;
using System.Collections.Generic;
using FishNet.Broadcast;
using FishNet.Connection;
using FishNet.Managing;
using FishNet.Transporting;
using UnityEngine;

namespace Genesis.Net.FishNetTransport
{
    /// <summary>
    /// Пакет Genesis поверх FishNet: просто байты протокола. FishNet здесь — только транспорт
    /// (Tugboat для LAN или FishySteamworks для Steam P2P/Relay), вся логика репликации — в Genesis.Net.
    /// Сборка компилируется, только если определён GENESIS_FISHNET (FishNet 4.x установлен).
    /// </summary>
    public struct GenesisPacket : IBroadcast
    {
        public byte[] Data;
    }

    public sealed class FishNetProvider : ITransportProvider
    {
        public string Name => "FishNet";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register() => TransportRegistry.Register(new FishNetProvider());

        /// <summary>
        /// Берёт NetworkManager из сцены (там можно настроить FishySteamworks),
        /// иначе создаёт свой — с транспортом по умолчанию (Tugboat, UDP).
        /// </summary>
        internal static NetworkManager EnsureManager()
        {
            var nm = UnityEngine.Object.FindObjectOfType<NetworkManager>();
            if (nm != null) return nm;
            var go = new GameObject("FishNet NetworkManager");
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go.AddComponent<NetworkManager>();
        }

        public IServerTransport CreateServer(int port) => new FishNetServer(EnsureManager(), port);
        public IClientTransport CreateClient(string address, int port) => new FishNetClient(EnsureManager(), address, port);
    }

    internal sealed class FishNetServer : IServerTransport
    {
        private readonly NetworkManager _nm;
        private readonly Queue<Action> _events = new Queue<Action>();

        public event Action<int> ClientConnected;
        public event Action<int> ClientDisconnected;
        public event Action<int, byte[]> Received;

        public FishNetServer(NetworkManager nm, int port)
        {
            _nm = nm;
            _nm.TransportManager.Transport.SetPort((ushort)port);
            _nm.ServerManager.OnRemoteConnectionState += OnRemoteState;
            _nm.ServerManager.RegisterBroadcast<GenesisPacket>(OnPacket, false);
            if (!_nm.ServerManager.StartConnection())
                throw new InvalidOperationException("FishNet server failed to start on port " + port);
        }

        private void OnRemoteState(NetworkConnection conn, RemoteConnectionStateArgs args)
        {
            int id = conn.ClientId;
            if (args.ConnectionState == RemoteConnectionState.Started)
                _events.Enqueue(() => ClientConnected?.Invoke(id));
            else if (args.ConnectionState == RemoteConnectionState.Stopped)
                _events.Enqueue(() => ClientDisconnected?.Invoke(id));
        }

        private void OnPacket(NetworkConnection conn, GenesisPacket packet, Channel channel)
        {
            int id = conn.ClientId;
            var data = packet.Data;
            _events.Enqueue(() => Received?.Invoke(id, data));
        }

        public void Send(int connection, byte[] data, bool reliable)
        {
            if (!_nm.ServerManager.Clients.TryGetValue(connection, out var conn)) return;
            _nm.ServerManager.Broadcast(conn, new GenesisPacket { Data = data }, false,
                reliable ? Channel.Reliable : Channel.Unreliable);
        }

        public void Disconnect(int connection)
        {
            if (_nm.ServerManager.Clients.TryGetValue(connection, out var conn)) conn.Disconnect(false);
        }

        public void Poll()
        {
            while (_events.Count > 0) _events.Dequeue()();
        }

        public void Dispose()
        {
            if (_nm == null) return;
            _nm.ServerManager.OnRemoteConnectionState -= OnRemoteState;
            _nm.ServerManager.UnregisterBroadcast<GenesisPacket>(OnPacket);
            _nm.ServerManager.StopConnection(true);
        }
    }

    internal sealed class FishNetClient : IClientTransport
    {
        private readonly NetworkManager _nm;
        private readonly Queue<Action> _events = new Queue<Action>();
        private bool _connected;

        public event Action Connected;
        public event Action<string> Disconnected;
        public event Action<byte[]> Received;

        public bool IsConnected => _connected;

        public FishNetClient(NetworkManager nm, string address, int port)
        {
            _nm = nm;
            _nm.ClientManager.OnClientConnectionState += OnState;
            _nm.ClientManager.RegisterBroadcast<GenesisPacket>(OnPacket);
            if (!_nm.ClientManager.StartConnection(address, (ushort)port))
                throw new InvalidOperationException("FishNet client failed to start");
        }

        private void OnState(ClientConnectionStateArgs args)
        {
            if (args.ConnectionState == LocalConnectionState.Started)
            {
                _connected = true;
                _events.Enqueue(() => Connected?.Invoke());
            }
            else if (args.ConnectionState == LocalConnectionState.Stopped)
            {
                bool was = _connected;
                _connected = false;
                _events.Enqueue(() => Disconnected?.Invoke(was ? "connection closed" : "could not connect"));
            }
        }

        private void OnPacket(GenesisPacket packet, Channel channel)
        {
            var data = packet.Data;
            _events.Enqueue(() => Received?.Invoke(data));
        }

        public void Send(byte[] data, bool reliable)
        {
            if (!_connected) return;
            _nm.ClientManager.Broadcast(new GenesisPacket { Data = data }, reliable ? Channel.Reliable : Channel.Unreliable);
        }

        public void Poll()
        {
            while (_events.Count > 0) _events.Dequeue()();
        }

        public void Dispose()
        {
            if (_nm == null) return;
            _nm.ClientManager.OnClientConnectionState -= OnState;
            _nm.ClientManager.UnregisterBroadcast<GenesisPacket>(OnPacket);
            _nm.ClientManager.StopConnection();
            _connected = false;
        }
    }
}
