using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

internal static class Program
{
    // MAVLink sender: GCS
    private const byte SenderSystemId = 255;
    private const byte SenderComponentId = 0; // MAV_COMP_ID_MISSIONPLANNER / GCS

    // ArduPilot target
    private const byte TargetSystemId = 1;
    private const byte TargetComponentId = 1;

    private static byte _mavSeq = 0;

    private static ushort _ch1Roll = 1500;
    private static ushort _ch2Pitch = 1500;
    private static ushort _ch3Throttle = 1000;
    private static ushort _ch4Yaw = 1500;
    private static ushort _ch5Arm = 1000;
    private static ushort _ch6Mode = 1000;
    private static ushort _ch7Aux = 1000;
    private static ushort _ch8Aux = 1000;

    private static DateTime _lastPacketTime = DateTime.MinValue;
    private static readonly TelemetryState _telemetry = new();
    private static readonly MavlinkV1Parser _mavlinkParser = new();

    public static void Main(string[] args)
    {
        if (args.Length < 5)
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("DroneReceiver <vps_ip> <udp_port> <room> <serial_port> <baud>");
            Console.WriteLine();
            Console.WriteLine("Example:");
            Console.WriteLine("DroneReceiver 195.133.24.163 50555 mell-drone /dev/serial0 57600");
            return;
        }

        string vpsIp = args[0];
        int udpPort = int.Parse(args[1]);
        string room = args[2];
        string serialPortName = args[3];
        int serialBaud = int.Parse(args[4]);

        Console.WriteLine("=== DroneReceiver ===");
        Console.WriteLine($"VPS: {vpsIp}:{udpPort}");
        Console.WriteLine($"Room: {room}");
        Console.WriteLine($"Serial: {serialPortName} @ {serialBaud}");
        Console.WriteLine($"MAVLink SenderSystemId: {SenderSystemId}");
        Console.WriteLine();

        using SerialPort serial = new SerialPort(serialPortName, serialBaud)
        {
            DataBits = 8,
            Parity = Parity.None,
            StopBits = StopBits.One,
            Handshake = Handshake.None,
            ReadTimeout = 50,
            WriteTimeout = 50
        };

        serial.Open();
        Console.WriteLine("Serial opened.");

        using UdpClient udp = new UdpClient();
        udp.Client.ReceiveTimeout = 1;

        IPEndPoint serverEndPoint = new IPEndPoint(IPAddress.Parse(vpsIp), udpPort);

        DateTime lastJoin = DateTime.MinValue;
        DateTime lastHeartbeat = DateTime.MinValue;
        DateTime lastRcSend = DateTime.MinValue;
        DateTime lastTelemetrySend = DateTime.MinValue;
        DateTime lastStatusPrint = DateTime.MinValue;

        Console.WriteLine("Started.");
        Console.WriteLine();

        while (true)
        {
            DateTime now = DateTime.UtcNow;

            ReadMavlinkTelemetry(serial, _mavlinkParser, _telemetry);

            // 1) Периодически регистрируемся в комнате на VPS
            if ((now - lastJoin).TotalMilliseconds >= 1000)
            {
                SendJoinPacket(udp, serverEndPoint, room);
                lastJoin = now;
            }

            // 2) Читаем UDP от VPS
            try
            {
                IPEndPoint? remote = null;
                byte[] data = udp.Receive(ref remote);
                string text = Encoding.UTF8.GetString(data).Trim();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    Console.WriteLine("UDP RX: " + text);

                    if (TryParseControlPacket(text))
                    {
                        _lastPacketTime = now;
                    }
                    else
                    {
                        Console.WriteLine("UDP parse failed");
                    }
                }
            }
            catch (SocketException)
            {
                // timeout, нормально
            }

            // 3) Если связь пропала — газ вниз, остальные в центр
            if (_lastPacketTime != DateTime.MinValue &&
                (now - _lastPacketTime).TotalMilliseconds > 1000)
            {
                _ch1Roll = 1500;
                _ch2Pitch = 1500;
                _ch3Throttle = 1000;
                _ch4Yaw = 1500;
                _ch5Arm = 1000;
                _ch6Mode = 1000;
                _ch7Aux = 1000;
                _ch8Aux = 1000;
            }

            // 4) HEARTBEAT раз в секунду
            if ((now - lastHeartbeat).TotalMilliseconds >= 1000)
            {
                SendHeartbeat(serial);
                lastHeartbeat = now;
            }

            // 5) RC override каждые 50 мс
            if ((now - lastRcSend).TotalMilliseconds >= 50)
            {
                SendRcOverride(
                    serial,
                    _ch1Roll,
                    _ch2Pitch,
                    _ch3Throttle,
                    _ch4Yaw,
                    _ch5Arm,
                    _ch6Mode,
                    _ch7Aux,
                    _ch8Aux);

                lastRcSend = now;
            }

            // 6) Печать статуса
            if ((now - lastTelemetrySend).TotalMilliseconds >= 200)
            {
                SendTelemetryPacket(udp, serverEndPoint, room, _telemetry);
                lastTelemetrySend = now;
            }

            if ((now - lastStatusPrint).TotalMilliseconds >= 1000)
            {
                Console.WriteLine(FormatTelemetryConsole(_telemetry));

                lastStatusPrint = now;
            }

            Thread.Sleep(1);
        }
    }

    private static void SendJoinPacket(UdpClient udp, IPEndPoint serverEndPoint, string room)
    {
        string json = JsonSerializer.Serialize(new
        {
            type = "join",
            role = "drone",
            room = room
        });

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        udp.Send(bytes, bytes.Length, serverEndPoint);
    }

    private static void SendTelemetryPacket(UdpClient udp, IPEndPoint serverEndPoint, string room, TelemetryState telemetry)
    {
        try
        {
            string text = string.Format(
                CultureInfo.InvariantCulture,
                "TO_OPERATOR;room={0};TEL;armed={1};mode={2};batV={3:0.0};batA={4:0.0};gpsFix={5};sats={6};alt={7:0.0};spd={8:0.0};roll={9:0.0};pitch={10:0.0};yaw={11:0.0};ts={12:O}",
                room,
                telemetry.IsArmed ? 1 : 0,
                telemetry.Mode,
                telemetry.BatteryVoltage,
                telemetry.BatteryCurrent,
                telemetry.GpsFix,
                telemetry.Satellites,
                telemetry.Altitude,
                telemetry.GroundSpeed,
                telemetry.Roll,
                telemetry.Pitch,
                telemetry.Yaw,
                DateTime.UtcNow);

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            udp.Send(bytes, bytes.Length, serverEndPoint);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Telemetry send exception: " + ex.Message);
        }
    }

    private static string FormatTelemetryConsole(TelemetryState telemetry)
    {
        return string.Format(
            CultureInfo.InvariantCulture,
            "TEL: armed={0} mode={1} bat={2:0.0}V gps={3} sats={4} alt={5:0.0} spd={6:0.0} roll={7:0.0} pitch={8:0.0} yaw={9:0.0}",
            telemetry.IsArmed,
            telemetry.Mode,
            telemetry.BatteryVoltage,
            telemetry.GpsFix,
            telemetry.Satellites,
            telemetry.Altitude,
            telemetry.GroundSpeed,
            telemetry.Roll,
            telemetry.Pitch,
            telemetry.Yaw);
    }

    private static void ReadMavlinkTelemetry(SerialPort serial, MavlinkV1Parser parser, TelemetryState telemetry)
    {
        try
        {
            int bytesToRead = serial.BytesToRead;
            if (bytesToRead <= 0)
                return;

            byte[] buffer = new byte[bytesToRead];
            int read = serial.Read(buffer, 0, buffer.Length);

            if (read > 0)
                parser.Push(buffer, read, telemetry);
        }
        catch (Exception ex)
        {
            Console.WriteLine("MAVLink read exception: " + ex.Message);
        }
    }

    private static bool TryParseControlPacket(string text)
{
    if (TryParseTextControlPacket(text))
        return true;

    try
    {
        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("type", out JsonElement typeElement))
        {
            string? type = typeElement.GetString();

            if (type == "joined" || type == "join")
                return false;
        }

        bool hasDirectChannels =
            root.TryGetProperty("ch1", out _) ||
            root.TryGetProperty("ch2", out _) ||
            root.TryGetProperty("ch3", out _) ||
            root.TryGetProperty("ch4", out _);

        if (hasDirectChannels)
        {
            _ch1Roll = NormalizeChannel(ReadInt(root, "ch1", 1500));
            _ch2Pitch = NormalizeChannel(ReadInt(root, "ch2", 1500));
            _ch3Throttle = NormalizeChannel(ReadInt(root, "ch3", 1000));
            _ch4Yaw = NormalizeChannel(ReadInt(root, "ch4", 1500));

            _ch5Arm = NormalizeChannel(ReadInt(root, "ch5", 1000));
            _ch6Mode = NormalizeChannel(ReadInt(root, "ch6", 1000));
            _ch7Aux = NormalizeChannel(ReadInt(root, "ch7", 1000));
            _ch8Aux = NormalizeChannel(ReadInt(root, "ch8", 1000));

            return true;
        }

        bool hasAxes =
            root.TryGetProperty("roll", out _) ||
            root.TryGetProperty("pitch", out _) ||
            root.TryGetProperty("throttle", out _) ||
            root.TryGetProperty("yaw", out _);

        if (!hasAxes)
            return false;

        int roll = ReadInt(root, "roll", 0);
        int pitch = ReadInt(root, "pitch", 0);
        int throttle = ReadInt(root, "throttle", -32767);
        int yaw = ReadInt(root, "yaw", 0);

        _ch1Roll = AxisToPwmCenter(roll);
        _ch2Pitch = AxisToPwmCenter(pitch);
        _ch3Throttle = AxisToPwmThrottle(throttle);
        _ch4Yaw = AxisToPwmCenter(yaw);

        _ch5Arm = NormalizeChannel(ReadInt(root, "ch5", 1000));
        _ch6Mode = NormalizeChannel(ReadInt(root, "ch6", 1000));
        _ch7Aux = NormalizeChannel(ReadInt(root, "ch7", 1000));
        _ch8Aux = NormalizeChannel(ReadInt(root, "ch8", 1000));

        return true;
    }
    catch
    {
        return TryParseCsvPacket(text);
    }
}
    
    private static bool TryParseTextControlPacket(string text)
    {
        try
        {
            if (!text.StartsWith("TO_DRONE;", StringComparison.OrdinalIgnoreCase))
                return false;

            string[] parts = text.Split(
                ';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            string? chPart = null;

            foreach (string part in parts)
            {
                if (part.StartsWith("ch=", StringComparison.OrdinalIgnoreCase))
                {
                    chPart = part.Substring(3);
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(chPart))
                return false;

            string[] ch = chPart.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (ch.Length < 4)
                return false;

            _ch1Roll = NormalizeChannel(int.Parse(ch[0]));
            _ch2Pitch = NormalizeChannel(int.Parse(ch[1]));
            _ch3Throttle = NormalizeChannel(int.Parse(ch[2]));
            _ch4Yaw = NormalizeChannel(int.Parse(ch[3]));

            _ch5Arm = ch.Length > 4 ? NormalizeChannel(int.Parse(ch[4])) : (ushort)1000;
            _ch6Mode = ch.Length > 5 ? NormalizeChannel(int.Parse(ch[5])) : (ushort)1000;
            _ch7Aux = ch.Length > 6 ? NormalizeChannel(int.Parse(ch[6])) : (ushort)1000;
            _ch8Aux = ch.Length > 7 ? NormalizeChannel(int.Parse(ch[7])) : (ushort)1000;

            Console.WriteLine($"PARSED TEXT: ch1={_ch1Roll} ch2={_ch2Pitch} ch3={_ch3Throttle} ch4={_ch4Yaw} ch5={_ch5Arm} ch6={_ch6Mode}");

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("TEXT parse exception: " + ex.Message);
            return false;
        }
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue)
    {
        if (!root.TryGetProperty(name, out JsonElement el))
            return defaultValue;

        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value))
            return value;

        if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), out int strValue))
            return strValue;

        return defaultValue;
    }

    private static bool TryParseCsvPacket(string text)
    {
        // Формат на всякий случай:
        // roll,pitch,throttle,yaw,ch5,ch6,ch7,ch8
        try
        {
            string[] p = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (p.Length < 4)
                return false;

            int roll = int.Parse(p[0]);
            int pitch = int.Parse(p[1]);
            int throttle = int.Parse(p[2]);
            int yaw = int.Parse(p[3]);

            int ch5 = p.Length > 4 ? int.Parse(p[4]) : 1000;
            int ch6 = p.Length > 5 ? int.Parse(p[5]) : 1000;
            int ch7 = p.Length > 6 ? int.Parse(p[6]) : 1000;
            int ch8 = p.Length > 7 ? int.Parse(p[7]) : 1000;

            _ch1Roll = AxisToPwmCenter(roll);
            _ch2Pitch = AxisToPwmCenter(pitch);
            _ch3Throttle = AxisToPwmThrottle(throttle);
            _ch4Yaw = AxisToPwmCenter(yaw);

            _ch5Arm = NormalizeChannel(ch5);
            _ch6Mode = NormalizeChannel(ch6);
            _ch7Aux = NormalizeChannel(ch7);
            _ch8Aux = NormalizeChannel(ch8);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ushort AxisToPwmCenter(int value)
    {
        // Если уже PWM 1000-2000
        if (value >= 900 && value <= 2100)
            return ClampPwm(value);

        // Если ось -32767..32767
        value = Math.Clamp(value, -32767, 32767);

        double normalized = value / 32767.0;
        int pwm = 1500 + (int)Math.Round(normalized * 500.0);

        return ClampPwm(pwm);
    }

    private static ushort AxisToPwmThrottle(int value)
    {
        // Если уже PWM 1000-2000
        if (value >= 900 && value <= 2100)
            return ClampPwm(value);

        // Если throttle приходит как -32767..32767
        value = Math.Clamp(value, -32767, 32767);

        double normalized = (value + 32767.0) / 65534.0;
        int pwm = 1000 + (int)Math.Round(normalized * 1000.0);

        return ClampPwm(pwm);
    }

    private static ushort NormalizeChannel(int value)
    {
        // Если уже PWM
        if (value >= 900 && value <= 2100)
            return ClampPwm(value);

        // Если 0/1
        if (value == 0)
            return 1000;

        if (value == 1)
            return 2000;

        // Если -32767..32767
        return AxisToPwmCenter(value);
    }

    private static ushort ClampPwm(int value)
    {
        return (ushort)Math.Clamp(value, 1000, 2000);
    }

    private static void SendHeartbeat(SerialPort serial)
    {
        byte[] payload =
        [
            0x00, 0x00, 0x00, 0x00, // custom_mode
            6,                      // MAV_TYPE_GCS
            8,                      // MAV_AUTOPILOT_INVALID
            0,                      // base_mode
            4,                      // MAV_STATE_ACTIVE
            3                       // mavlink_version
        ];

        SendMavlinkV1(serial, 0, payload);
    }

    private static void SendRcOverride(
        SerialPort serial,
        ushort ch1,
        ushort ch2,
        ushort ch3,
        ushort ch4,
        ushort ch5,
        ushort ch6,
        ushort ch7,
        ushort ch8)
    {
        byte[] payload = new byte[18];

        WriteUInt16(payload, 0, ch1);
        WriteUInt16(payload, 2, ch2);
        WriteUInt16(payload, 4, ch3);
        WriteUInt16(payload, 6, ch4);
        WriteUInt16(payload, 8, ch5);
        WriteUInt16(payload, 10, ch6);
        WriteUInt16(payload, 12, ch7);
        WriteUInt16(payload, 14, ch8);

        payload[16] = TargetSystemId;
        payload[17] = TargetComponentId;

        SendMavlinkV1(serial, 70, payload);
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void SendMavlinkV1(SerialPort serial, byte messageId, byte[] payload)
    {
        byte payloadLength = (byte)payload.Length;

        byte[] packet = new byte[6 + payloadLength + 2];

        packet[0] = 0xFE;
        packet[1] = payloadLength;
        packet[2] = _mavSeq++;
        packet[3] = SenderSystemId;
        packet[4] = SenderComponentId;
        packet[5] = messageId;

        Array.Copy(payload, 0, packet, 6, payloadLength);

        ushort crc = X25Crc(packet, 1, 5 + payloadLength);
        crc = CrcAccumulate(GetMavlinkCrcExtra(messageId), crc);

        packet[6 + payloadLength] = (byte)(crc & 0xFF);
        packet[7 + payloadLength] = (byte)((crc >> 8) & 0xFF);

        serial.Write(packet, 0, packet.Length);
    }

    private static byte GetMavlinkCrcExtra(byte messageId)
    {
        return messageId switch
        {
            0 => 50,    // HEARTBEAT
            70 => 124,  // RC_CHANNELS_OVERRIDE
            _ => 0
        };
    }

    private static ushort X25Crc(byte[] buffer, int offset, int length)
    {
        ushort crc = 0xFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc = CrcAccumulate(buffer[i], crc);
        }

        return crc;
    }

    private static ushort CrcAccumulate(byte data, ushort crc)
    {
        data ^= (byte)(crc & 0xFF);
        data ^= (byte)(data << 4);

        return (ushort)((crc >> 8) ^ (data << 8) ^ (data << 3) ^ (data >> 4));
    }
}

public class TelemetryState
{
    public bool IsArmed { get; set; }
    public string Mode { get; set; } = "UNKNOWN";
    public double BatteryVoltage { get; set; }
    public double BatteryCurrent { get; set; }
    public int GpsFix { get; set; }
    public int Satellites { get; set; }
    public double Altitude { get; set; }
    public double GroundSpeed { get; set; }
    public double Roll { get; set; }
    public double Pitch { get; set; }
    public double Yaw { get; set; }
    public DateTime LastMavlinkUtc { get; set; }
}

internal sealed class MavlinkV1Parser
{
    private readonly List<byte> _buffer = new();

    public void Push(byte[] bytes, int count, TelemetryState telemetry)
    {
        try
        {
            for (int i = 0; i < count; i++)
                _buffer.Add(bytes[i]);

            ParseAvailable(telemetry);
        }
        catch (Exception ex)
        {
            Console.WriteLine("MAVLink parse exception: " + ex.Message);
            _buffer.Clear();
        }
    }

    private void ParseAvailable(TelemetryState telemetry)
    {
        while (true)
        {
            int magicIndex = _buffer.IndexOf(0xFE);
            if (magicIndex < 0)
            {
                _buffer.Clear();
                return;
            }

            if (magicIndex > 0)
                _buffer.RemoveRange(0, magicIndex);

            if (_buffer.Count < 8)
                return;

            int payloadLength = _buffer[1];
            int packetLength = 6 + payloadLength + 2;

            if (_buffer.Count < packetLength)
                return;

            byte msgId = _buffer[5];
            byte[] payload = new byte[payloadLength];
            _buffer.CopyTo(6, payload, 0, payloadLength);
            _buffer.RemoveRange(0, packetLength);

            ApplyMessage(msgId, payload, telemetry);
        }
    }

    private static void ApplyMessage(byte msgId, byte[] payload, TelemetryState telemetry)
    {
        try
        {
            switch (msgId)
            {
                case 0:
                    ApplyHeartbeat(payload, telemetry);
                    break;
                case 1:
                    ApplySysStatus(payload, telemetry);
                    break;
                case 24:
                    ApplyGpsRawInt(payload, telemetry);
                    break;
                case 30:
                    ApplyAttitude(payload, telemetry);
                    break;
                case 33:
                    ApplyGlobalPositionInt(payload, telemetry);
                    break;
                case 74:
                    ApplyVfrHud(payload, telemetry);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"MAVLink msg {msgId} exception: {ex.Message}");
        }
    }

    private static void ApplyHeartbeat(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 9)
            return;

        uint customMode = ReadUInt32(payload, 0);
        byte baseMode = payload[6];

        telemetry.IsArmed = (baseMode & 128) != 0;
        telemetry.Mode = customMode.ToString(CultureInfo.InvariantCulture);
        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static void ApplySysStatus(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 18)
            return;

        ushort voltageBattery = ReadUInt16(payload, 14);
        short currentBattery = ReadInt16(payload, 16);

        if (voltageBattery != ushort.MaxValue)
            telemetry.BatteryVoltage = voltageBattery / 1000.0;

        if (currentBattery != -1)
            telemetry.BatteryCurrent = currentBattery / 100.0;

        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static void ApplyGpsRawInt(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 30)
            return;

        telemetry.GpsFix = payload[28];
        telemetry.Satellites = payload[29];
        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static void ApplyAttitude(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 16)
            return;

        telemetry.Roll = RadiansToDegrees(ReadSingle(payload, 4));
        telemetry.Pitch = RadiansToDegrees(ReadSingle(payload, 8));
        telemetry.Yaw = RadiansToDegrees(ReadSingle(payload, 12));
        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static void ApplyGlobalPositionInt(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 24)
            return;

        int relativeAlt = ReadInt32(payload, 20);
        telemetry.Altitude = relativeAlt / 1000.0;
        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static void ApplyVfrHud(byte[] payload, TelemetryState telemetry)
    {
        if (payload.Length < 20)
            return;

        telemetry.GroundSpeed = ReadSingle(payload, 4);
        telemetry.Altitude = ReadSingle(payload, 8);
        telemetry.LastMavlinkUtc = DateTime.UtcNow;
    }

    private static double RadiansToDegrees(float radians)
    {
        return radians * 180.0 / Math.PI;
    }

    private static ushort ReadUInt16(byte[] payload, int offset)
    {
        return (ushort)(payload[offset] | (payload[offset + 1] << 8));
    }

    private static short ReadInt16(byte[] payload, int offset)
    {
        return unchecked((short)ReadUInt16(payload, offset));
    }

    private static uint ReadUInt32(byte[] payload, int offset)
    {
        return (uint)(payload[offset] |
                      (payload[offset + 1] << 8) |
                      (payload[offset + 2] << 16) |
                      (payload[offset + 3] << 24));
    }

    private static int ReadInt32(byte[] payload, int offset)
    {
        return unchecked((int)ReadUInt32(payload, offset));
    }

    private static float ReadSingle(byte[] payload, int offset)
    {
        return BitConverter.ToSingle(payload, offset);
    }
}
