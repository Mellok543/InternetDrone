using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OperatorStation;

internal sealed class UdpRelayClient : IDisposable
{
    private readonly string _clientId = "operator-" + Environment.MachineName;
    private UdpClient? _udp;
    private IPEndPoint? _relayEndPoint;

    public bool IsConnected => _udp != null && _relayEndPoint != null;

    public async Task ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || host == "YOUR_VPS_IP")
            throw new InvalidOperationException("Укажи VPS IP / HOST.");

        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Порт должен быть в диапазоне 1-65535.");

        IPAddress[] ips = await Dns.GetHostAddressesAsync(host.Trim(), cancellationToken);
        IPAddress ip = ips.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)
            ?? ips.FirstOrDefault()
            ?? throw new InvalidOperationException("Не удалось найти IP для VPS.");

        Disconnect();
        _relayEndPoint = new IPEndPoint(ip, port);
        _udp = new UdpClient(0);
        _udp.Client.Blocking = false;
    }

    public void SendHello(string room)
    {
        SendRaw($"HELLO;role=operator;room={NormalizeRoom(room)};id={_clientId}");
    }

    public string SendControlPacket(string room, int sequence, IReadOnlyList<int> channels, bool armed, string mode)
    {
        string inner = $"DRONECTRL2;seq={sequence};ch={string.Join(',', channels)};arm={(armed ? 1 : 0)};mode={mode};ts={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        SendRaw($"TO_DRONE;room={NormalizeRoom(room)};{inner}");
        return inner;
    }

    public void SendDroneCommand(string room, string command)
    {
        SendUdpCommand($"TO_DRONE;room={NormalizeRoom(room)};{command}");
    }

    public void SendUdpCommand(string text)
    {
        SendRaw(text);
    }

    public IReadOnlyList<string> ReceivePending()
    {
        if (_udp == null) return Array.Empty<string>();

        var packets = new List<string>();
        while (_udp.Available > 0)
        {
            IPEndPoint ep = new(IPAddress.Any, 0);
            packets.Add(Encoding.UTF8.GetString(_udp.Receive(ref ep)));
        }

        return packets;
    }

    public void Disconnect()
    {
        _udp?.Dispose();
        _udp = null;
        _relayEndPoint = null;
    }

    public void Dispose() => Disconnect();

    private void SendRaw(string packet)
    {
        if (_udp == null || _relayEndPoint == null)
            throw new InvalidOperationException("UDP relay не подключен.");

        byte[] data = Encoding.UTF8.GetBytes(packet);
        _udp.Send(data, data.Length, _relayEndPoint);
    }

    private static string NormalizeRoom(string room) =>
        string.IsNullOrWhiteSpace(room) ? "mell-drone" : room.Trim();
}
