using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

Console.WriteLine("=== Mell Drone VPS UDP Relay ===");

int port = 50555;

if (args.Length >= 1 && int.TryParse(args[0], out int argPort))
{
    port = argPort;
}

using UdpClient udp = new UdpClient(port);

Console.WriteLine($"Listening UDP port: {port}");

Dictionary<string, IPEndPoint> operators = new();
Dictionary<string, IPEndPoint> drones = new();

while (true)
{
    UdpReceiveResult result = await udp.ReceiveAsync();

    IPEndPoint from = result.RemoteEndPoint;
    string text = Encoding.UTF8.GetString(result.Buffer).Trim();

    if (string.IsNullOrWhiteSpace(text))
        continue;

    try
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;

        string type = GetString(root, "type");
        string role = GetString(root, "role");
        string room = GetString(root, "room");

        if (string.IsNullOrWhiteSpace(room))
        {
            room = FindRoomByOperator(from);

            if (string.IsNullOrWhiteSpace(room))
                room = "mell-drone";
        }

        if (type == "join")
        {
            if (role == "operator" || role == "sender")
            {
                operators[room] = from;
                Console.WriteLine($"JOIN operator room={room} from {from}");
                await SendText(udp, from, "{\"type\":\"joined\",\"role\":\"operator\"}");
                continue;
            }

            if (role == "drone" || role == "receiver")
            {
                drones[room] = from;
                Console.WriteLine($"JOIN drone room={room} from {from}");
                await SendText(udp, from, "{\"type\":\"joined\",\"role\":\"drone\"}");
                continue;
            }

            Console.WriteLine($"Unknown join role from {from}: {text}");
            continue;
        }

        bool isKnownOperator = IsKnownOperator(from);

        bool looksLikeControl =
            isKnownOperator ||
            root.TryGetProperty("roll", out _) ||
            root.TryGetProperty("pitch", out _) ||
            root.TryGetProperty("throttle", out _) ||
            root.TryGetProperty("yaw", out _) ||
            root.TryGetProperty("ch1", out _) ||
            root.TryGetProperty("ch2", out _) ||
            root.TryGetProperty("ch3", out _) ||
            root.TryGetProperty("ch4", out _);

        if (looksLikeControl)
        {
            if (drones.TryGetValue(room, out IPEndPoint? droneEp))
            {
                await udp.SendAsync(result.Buffer, result.Buffer.Length, droneEp);
                Console.WriteLine($"CONTROL room={room} {from} -> {droneEp} bytes={result.Buffer.Length}");
            }
            else
            {
                Console.WriteLine($"No drone in room={room}. Control from {from}: {text}");
            }

            continue;
        }

        Console.WriteLine($"Unknown from {from}: {text}");
    }
    catch
    {
        if (text.StartsWith("TO_DRONE;", StringComparison.OrdinalIgnoreCase))
        {
            string room = GetRoomFromTextPacket(text);

            if (string.IsNullOrWhiteSpace(room))
                room = "mell-drone";

            if (drones.TryGetValue(room, out IPEndPoint? droneEp))
            {
                await udp.SendAsync(result.Buffer, result.Buffer.Length, droneEp);
                Console.WriteLine($"CONTROL TEXT room={room} {from} -> {droneEp} bytes={result.Buffer.Length}");
            }
            else
            {
                Console.WriteLine($"No drone in room={room}. Text control from {from}: {text}");
            }

            continue;
        }

        if (text.StartsWith("TO_OPERATOR;", StringComparison.OrdinalIgnoreCase))
        {
            string room = GetRoomFromTextPacket(text);

            if (operators.TryGetValue(room, out IPEndPoint? operatorEp))
            {
                await udp.SendAsync(result.Buffer, result.Buffer.Length, operatorEp);
                Console.WriteLine($"TELEMETRY TEXT room={room} {from} -> {operatorEp} bytes={result.Buffer.Length}");
            }
            else
            {
                Console.WriteLine($"No operator in room={room}");
            }

            continue;
        }

        Console.WriteLine($"Bad packet from {from}: {text}");
    }
}

string FindRoomByOperator(IPEndPoint from)
{
    foreach (var pair in operators)
    {
        if (SameEndpoint(pair.Value, from))
            return pair.Key;
    }

    return "";
}

bool IsKnownOperator(IPEndPoint from)
{
    foreach (var pair in operators)
    {
        if (SameEndpoint(pair.Value, from))
            return true;
    }

    return false;
}

static bool SameEndpoint(IPEndPoint a, IPEndPoint b)
{
    return a.Address.Equals(b.Address) && a.Port == b.Port;
}

static string GetString(JsonElement root, string name)
{
    if (!root.TryGetProperty(name, out JsonElement el))
        return "";

    if (el.ValueKind == JsonValueKind.String)
        return el.GetString() ?? "";

    return el.ToString();
}

static async Task SendText(UdpClient udp, IPEndPoint to, string text)
{
    byte[] bytes = Encoding.UTF8.GetBytes(text);
    await udp.SendAsync(bytes, bytes.Length, to);
}

static string GetRoomFromTextPacket(string text)
{
    string[] parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (string part in parts)
    {
        if (part.StartsWith("room=", StringComparison.OrdinalIgnoreCase))
            return part.Substring("room=".Length);
    }

    return "";
}
