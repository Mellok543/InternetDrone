using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace OperatorStation;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AxisState> _axes = new();
    private readonly ObservableCollection<ButtonState> _buttons = new();
    private readonly ObservableCollection<ChannelState> _channels = new();
    private readonly PovState _pov = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly UdpRelayClient _relayClient = new();
    private readonly DirectInputJoystickReader _directInputReader = new();
    private readonly OsdLayoutStore _osdLayoutStore = new();
    private readonly ObservableCollection<OsdElement> _osdElements = new();
    private readonly Dictionary<FrameworkElement, OsdElement> _osdVisualElements = new();
    private readonly TelemetryState _telemetry = new();

    private readonly DispatcherTimer _pollTimer = new();
    private readonly DispatcherTimer _sendTimer = new();
    private readonly DispatcherTimer _rxTimer = new();

    private JoystickDevice? _selectedJoystick;
    private bool _calibrating;
    private ComboBox? _captureTargetCombo;
    private string _captureTargetName = "";
    private bool[] _previousButtons = [];
    private string _previousPovText = "CENTER";
    private int[] _captureAxisBaseline = [];
    private int _seq;
    private bool _arm;
    private string _mode = "MANUAL";
    private DateTime _lastAck = DateTime.MinValue;
    private OsdLayout _osdLayout = OsdDefaults.CreateLayout();
    private OsdElement? _draggingOsdElement;
    private const double OsdCellWidth = 30.0;
    private const double OsdCellHeight = 30.0;

    public MainWindow()
    {
        InitializeComponent();

        foreach (string axis in OperatorConstants.AxisNames) _axes.Add(new AxisState { AxisName = axis });
        for (int i = 1; i <= 128; i++) _buttons.Add(new ButtonState { Button = i });
        for (int i = 1; i <= 16; i++) _channels.Add(new ChannelState { Name = $"CH{i}", Pwm = i == 3 ? 1000 : 1500 });

        AxesGrid.ItemsSource = _axes;
        ButtonsGrid.ItemsSource = _buttons;
        ChannelsGrid.ItemsSource = _channels;
        OsdElementsGrid.ItemsSource = _osdElements;
        JoystickCombo.SelectionChanged += JoystickCombo_SelectionChanged;
        _osdElements.CollectionChanged += OsdElements_CollectionChanged;
        _telemetry.PropertyChanged += (_, _) => RenderOsdPreview();

        SetupCombos();
        LoadOsdLayout(_osdLayoutStore.Load());
        RefreshJoysticks();
        LoadSettingsIfExists();

        _pollTimer.Interval = TimeSpan.FromMilliseconds(20);
        _pollTimer.Tick += (_, _) => PollJoystick();
        _pollTimer.Start();

        _sendTimer.Interval = TimeSpan.FromMilliseconds(50);
        _sendTimer.Tick += (_, _) => SendPacket();

        _rxTimer.Interval = TimeSpan.FromMilliseconds(100);
        _rxTimer.Tick += (_, _) => ReceiveRelayPackets();
        _rxTimer.Start();
    }

    private void SetupCombos()
    {
        foreach (ComboBox combo in new[] { RollAxisCombo, PitchAxisCombo, ThrottleAxisCombo, YawAxisCombo })
            combo.ItemsSource = OperatorConstants.AxisNames;

        RollAxisCombo.SelectedItem = "X";
        PitchAxisCombo.SelectedItem = "Y";
        ThrottleAxisCombo.SelectedItem = "Z";
        YawAxisCombo.SelectedItem = "R";

        var opts = new List<string> { OperatorConstants.Unassigned };
        opts.AddRange(Enumerable.Range(1, 128).Select(x => $"Button {x}"));
        opts.AddRange(new[] { "POV UP", "POV RIGHT", "POV DOWN", "POV LEFT", "POV CENTER" });
        opts.AddRange(OperatorConstants.AxisNames.SelectMany(axis => new[] { $"Axis {axis} HIGH", $"Axis {axis} LOW" }));
        foreach (ComboBox combo in new[] { ArmButtonCombo, LoiterButtonCombo, RtlButtonCombo, LandButtonCombo, ManualButtonCombo })
        {
            combo.ItemsSource = opts;
            combo.SelectedIndex = 0;
        }
    }

    private void RefreshJoysticks_Click(object sender, RoutedEventArgs e) => RefreshJoysticks();

    private void RefreshJoysticks()
    {
        var devices = new List<JoystickDevice>(_directInputReader.GetDevices());
        uint count = JoystickNative.joyGetNumDevs();
        for (uint i = 0; i < count; i++)
        {
            uint res = JoystickNative.joyGetDevCaps(i, out var caps, (uint)Marshal.SizeOf<JoystickNative.JOYCAPS>());
            if (res == 0)
            {
                devices.Add(new JoystickDevice
                {
                    Source = JoystickDeviceSource.WinMm,
                    Id = i,
                    Name = $"MM: {i}: {caps.szPname}",
                    AxisCount = (int)caps.wNumAxes,
                    ButtonCount = (int)caps.wNumButtons
                });
            }
        }

        JoystickCombo.ItemsSource = devices;
        if (devices.Count > 0)
        {
            JoystickCombo.SelectedIndex = 0;
            _selectedJoystick = devices[0];
            StatusText.Text = $"JOYSTICK: {_selectedJoystick.Name}";
        }
        else
        {
            JoystickCombo.SelectedIndex = -1;
            _selectedJoystick = null;
            StatusText.Text = "NO JOYSTICK";
        }
    }

    private void JoystickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedJoystick = JoystickCombo.SelectedItem as JoystickDevice;
        _previousButtons = [];
        _previousPovText = "CENTER";
    }

    private void EnsureAxisCount(int count)
    {
        int target = Math.Min(count, OperatorConstants.AxisNames.Length);
        while (_axes.Count < target)
        {
            string axis = OperatorConstants.AxisNames[_axes.Count];
            _axes.Add(new AxisState { AxisName = axis });
        }
    }

    private void EnsureButtonCount(int count)
    {
        int target = Math.Min(Math.Max(count, 32), 128);
        while (_buttons.Count < target)
            _buttons.Add(new ButtonState { Button = _buttons.Count + 1 });
    }

    private void PollJoystick()
    {
        if (_selectedJoystick == null) return;

        JoystickSnapshot? snapshot = _selectedJoystick.Source == JoystickDeviceSource.DirectInput
            ? PollDirectInputJoystick()
            : PollWinMmJoystick();

        if (snapshot == null)
            return;

        int[] raw = snapshot.Axes;

        EnsureAxisCount(raw.Length);
        EnsureButtonCount(snapshot.Buttons.Length);

        for (int i = 0; i < _axes.Count; i++)
        {
            int value = i < raw.Length ? raw[i] : 32767;
            _axes[i].Raw = value;
            if (_calibrating)
            {
                _axes[i].Min = Math.Min(_axes[i].Min, value);
                _axes[i].Max = Math.Max(_axes[i].Max, value);
            }
            _axes[i].Pwm = ControlMath.AxisToPwm(_axes[i]);
        }

        for (int i = 0; i < _buttons.Count; i++)
            _buttons[i].Pressed = i < snapshot.Buttons.Length && snapshot.Buttons[i];

        string povText = DecodePov(snapshot.Pov);
        _pov.Value = povText;
        PovText.Text = povText;

        HandleCapture(raw, snapshot.Buttons, povText);

        _previousButtons = snapshot.Buttons.ToArray();
        _previousPovText = povText;

        UpdateLogicalChannels();
        UpdateStickViews();
        UpdateStateViews();
    }

    private JoystickSnapshot? PollDirectInputJoystick()
    {
        if (_selectedJoystick == null)
            return null;

        var handle = new WindowInteropHelper(this).Handle;
        _directInputReader.SelectDevice(_selectedJoystick, handle);
        JoystickSnapshot? snapshot = _directInputReader.Poll();
        if (snapshot == null)
            StatusText.Text = "DIRECTINPUT ERR";

        return snapshot;
    }

    private JoystickSnapshot? PollWinMmJoystick()
    {
        if (_selectedJoystick == null)
            return null;

        var info = new JoystickNative.JOYINFOEX
        {
            dwSize = (uint)Marshal.SizeOf<JoystickNative.JOYINFOEX>(),
            dwFlags = JoystickNative.JOY_RETURNALL
        };

        uint res = JoystickNative.joyGetPosEx(_selectedJoystick.Id, ref info);
        if (res != 0)
        {
            StatusText.Text = $"JOYSTICK ERR {res}";
            return null;
        }

        var buttons = new bool[32];
        for (int i = 0; i < buttons.Length; i++)
            buttons[i] = (info.dwButtons & (1u << i)) != 0;

        return new JoystickSnapshot
        {
            Axes = [(int)info.dwXpos, (int)info.dwYpos, (int)info.dwZpos, (int)info.dwRpos, (int)info.dwUpos, (int)info.dwVpos, 32767, 32767],
            Buttons = buttons,
            Pov = unchecked((int)info.dwPOV)
        };
    }

    private void HandleCapture(IReadOnlyList<int> raw, IReadOnlyList<bool> currentButtons, string povText)
    {
        if (_captureTargetCombo == null)
            return;

        for (int i = 0; i < currentButtons.Count; i++)
        {
            bool wasPressed = i < _previousButtons.Length && _previousButtons[i];
            if (currentButtons[i] && !wasPressed)
            {
                int buttonNumber = i + 1;
                _captureTargetCombo.SelectedItem = $"Button {buttonNumber}";
                StatusText.Text = $"Назначено {_captureTargetName}: Button {buttonNumber}";
                CaptureStatusText.Text = "OFF";
                _captureTargetCombo = null;
                return;
            }
        }

        if (povText != "CENTER" && _previousPovText == "CENTER")
        {
            _captureTargetCombo.SelectedItem = $"POV {povText.Split('-')[0]}";
            StatusText.Text = $"Назначено {_captureTargetName}: POV {povText}";
            CaptureStatusText.Text = "OFF";
            _captureTargetCombo = null;
            return;
        }

        CaptureAxisSwitch(raw);
    }

    private void UpdateLogicalChannels()
    {
        _channels[0].Pwm = GetAxisPwm(RollAxisCombo.SelectedItem?.ToString() ?? "X");
        _channels[1].Pwm = GetAxisPwm(PitchAxisCombo.SelectedItem?.ToString() ?? "Y");
        _channels[2].Pwm = GetAxisPwm(ThrottleAxisCombo.SelectedItem?.ToString() ?? "Z");
        _channels[3].Pwm = GetAxisPwm(YawAxisCombo.SelectedItem?.ToString() ?? "R");

        _arm = IsAssignedButtonPressed(ArmButtonCombo);
        if (IsAssignedButtonPressed(LoiterButtonCombo)) _mode = "LOITER";
        else if (IsAssignedButtonPressed(RtlButtonCombo)) _mode = "RTL";
        else if (IsAssignedButtonPressed(LandButtonCombo)) _mode = "LAND";
        else if (IsAssignedButtonPressed(ManualButtonCombo)) _mode = "MANUAL";

        _channels[4].Pwm = _arm ? 2000 : 1000;
        _channels[5].Pwm = _mode switch
        {
            "LOITER" => 1600,
            "RTL" => 1800,
            "LAND" => 1400,
            _ => 1000
        };
    }

    private int GetAxisPwm(string name) => _axes.FirstOrDefault(a => a.AxisName == name)?.Pwm ?? 1500;

    private bool IsAssignedButtonPressed(ComboBox combo)
    {
        if (combo.SelectedItem is not string s || s == OperatorConstants.Unassigned)
            return false;

        if (s.StartsWith("Button ") && int.TryParse(s[7..], out int b))
            return b >= 1 && b <= _buttons.Count && _buttons[b - 1].Pressed;

        if (s.StartsWith("POV "))
        {
            string target = s[4..];
            return _pov.Value == target || _pov.Value.StartsWith(target + "-", StringComparison.Ordinal);
        }

        if (TryParseAxisSwitch(s, out string axisName, out bool high))
        {
            AxisState? axis = _axes.FirstOrDefault(a => a.AxisName == axisName);
            return axis != null && IsAxisSwitchActive(axis, high);
        }

        return false;
    }

    private static int ComboToButton(ComboBox combo)
    {
        if (combo.SelectedItem is not string s || s == OperatorConstants.Unassigned) return -1;
        return s.StartsWith("Button ") && int.TryParse(s[7..], out int b) ? b : -1;
    }

    private static void SetComboButton(ComboBox combo, int button) => combo.SelectedItem = button <= 0 ? OperatorConstants.Unassigned : $"Button {button}";

    private static bool TryParseAxisSwitch(string value, out string axisName, out bool high)
    {
        axisName = "";
        high = false;

        string[] parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "Axis")
            return false;

        axisName = parts[1];
        high = parts[2] == "HIGH";
        return OperatorConstants.AxisNames.Contains(axisName) && (high || parts[2] == "LOW");
    }

    private static bool IsAxisSwitchActive(AxisState axis, bool high)
    {
        int center = axis.Center;
        int span = Math.Max(axis.Max - axis.Min, 1);
        int threshold = Math.Max(span / 4, 6000);
        return high ? axis.Raw >= center + threshold : axis.Raw <= center - threshold;
    }

    private void CaptureAxisSwitch(IReadOnlyList<int> raw)
    {
        ComboBox? targetCombo = _captureTargetCombo;
        if (targetCombo == null || _captureAxisBaseline.Length != raw.Count)
            return;

        for (int i = 0; i < raw.Count && i < OperatorConstants.AxisNames.Length; i++)
        {
            int delta = raw[i] - _captureAxisBaseline[i];
            if (Math.Abs(delta) < 10000)
                continue;

            string axis = OperatorConstants.AxisNames[i];
            string direction = delta > 0 ? "HIGH" : "LOW";
            targetCombo.SelectedItem = $"Axis {axis} {direction}";
            StatusText.Text = $"Назначено {_captureTargetName}: Axis {axis} {direction}";
            CaptureStatusText.Text = "OFF";
            _captureTargetCombo = null;
            return;
        }
    }

    private void UpdateStickViews()
    {
        MoveDot(LeftStickDot, _channels[3].Pwm, _channels[2].Pwm);
        MoveDot(RightStickDot, _channels[0].Pwm, _channels[1].Pwm);

        static void MoveDot(System.Windows.Shapes.Ellipse dot, int xPwm, int yPwm)
        {
            double x = (xPwm - 1000) / 1000.0 * 260.0;
            double y = 260.0 - ((yPwm - 1000) / 1000.0 * 260.0);
            Canvas.SetLeft(dot, Math.Clamp(x, 0, 260) - 11);
            Canvas.SetTop(dot, Math.Clamp(y, 0, 260) - 11);
        }
    }

    private void UpdateStateViews()
    {
        ArmText.Text = _arm ? "ON" : "OFF";
        ArmPanel.Background = _arm ? new SolidColorBrush(Color.FromRgb(23, 74, 45)) : new SolidColorBrush(Color.FromRgb(58, 22, 32));
        ModeText.Text = _mode;
        SeqText.Text = $"SEQ {_seq}";
        if (_lastAck != DateTime.MinValue)
        {
            double age = (DateTime.UtcNow - _lastAck).TotalMilliseconds;
            PingText.Text = age < 1000 ? "ACK" : "-- ms";
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string host = VpsHostBox.Text.Trim();
            if (!int.TryParse(VpsPortBox.Text, out int port))
                throw new InvalidOperationException("Порт должен быть числом.");

            StartButton.IsEnabled = false;
            StatusText.Text = "CONNECTING";
            await _relayClient.ConnectAsync(host, port);
            _relayClient.SendHello(RoomBox.Text);
            _sendTimer.Start();

            StopButton.IsEnabled = true;
            LinkLamp.Fill = Brushes.LimeGreen;
            StatusText.Text = "ONLINE → VPS";
        }
        catch (Exception ex)
        {
            SetDisconnected("OFFLINE");
            MessageBox.Show($"Ошибка подключения: {ex.Message}");
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SetDisconnected("OFFLINE");
    }

    private void SendPacket()
    {
        if (!_relayClient.IsConnected) return;

        try
        {
            _seq++;
            int[] ch = _channels.Select(c => c.Pwm).ToArray();
            string inner = _relayClient.SendControlPacket(RoomBox.Text, _seq, ch, _arm, _mode);

            LastPacketText.Text = $"Packet: {inner}";
            SeqText.Text = $"SEQ {_seq}";
        }
        catch (Exception ex)
        {
            SetDisconnected("SEND ERROR");
            LastPacketText.Text = $"Packet error: {ex.Message}";
        }
    }

    private void ReceiveRelayPackets()
    {
        if (!_relayClient.IsConnected)
        {
            _telemetry.RefreshComputedProperties();
            RenderOsdPreview();
            return;
        }

        try
        {
            bool telemetryUpdated = false;
            foreach (string text in _relayClient.ReceivePending())
            {
                if (text.StartsWith("TO_OPERATOR;", StringComparison.Ordinal) && text.Contains(";TEL;", StringComparison.Ordinal))
                {
                    TelemetryState parsed = TelemetryParser.ParseTelemetryPacket(text);
                    Dispatcher.Invoke(() =>
                    {
                        _telemetry.CopyFrom(parsed);
                        telemetryUpdated = true;
                        OsdStatusText.Text = $"Telemetry: {_telemetry.Mode} {_telemetry.BatteryVoltage.ToString("0.0", CultureInfo.InvariantCulture)}V";
                    });
                    continue;
                }

                if (text.StartsWith("ACK") || text.StartsWith("FROM_DRONE"))
                {
                    _lastAck = DateTime.UtcNow;
                    PingText.Text = "ACK";
                }
            }

            _telemetry.RefreshComputedProperties();
            if (!telemetryUpdated)
                RenderOsdPreview();
        }
        catch (Exception ex)
        {
            SetDisconnected("RX ERROR");
            LastPacketText.Text = $"Receive error: {ex.Message}";
        }
    }

    private void SetDisconnected(string status)
    {
        _sendTimer.Stop();
        _relayClient.Disconnect();
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        LinkLamp.Fill = new SolidColorBrush(Color.FromRgb(86, 96, 112));
        StatusText.Text = status;
    }

    private void CalStart_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _axes) { a.Min = a.Raw; a.Max = a.Raw; }
        _calibrating = true;
        CalStartButton.IsEnabled = false;
        CalFinishButton.IsEnabled = true;
        StatusText.Text = "CALIBRATING";
    }

    private void CalFinish_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _axes)
        {
            a.Center = a.Raw;
            if (a.Max <= a.Min + 20) { a.Min = 0; a.Center = 32767; a.Max = 65535; }
        }
        _calibrating = false;
        CalStartButton.IsEnabled = true;
        CalFinishButton.IsEnabled = false;
        StatusText.Text = "CAL DONE";
    }

    private void CalReset_Click(object sender, RoutedEventArgs e)
    {
        foreach (var a in _axes) { a.Min = 0; a.Center = 32767; a.Max = 65535; a.Invert = false; }
    }

    private void CaptureArm_Click(object sender, RoutedEventArgs e) => StartButtonCapture(ArmButtonCombo, "ARM");
    private void CaptureLoiter_Click(object sender, RoutedEventArgs e) => StartButtonCapture(LoiterButtonCombo, "LOITER");
    private void CaptureRtl_Click(object sender, RoutedEventArgs e) => StartButtonCapture(RtlButtonCombo, "RTL");
    private void CaptureLand_Click(object sender, RoutedEventArgs e) => StartButtonCapture(LandButtonCombo, "LAND");
    private void CaptureManual_Click(object sender, RoutedEventArgs e) => StartButtonCapture(ManualButtonCombo, "MANUAL");

    private void StartButtonCapture(ComboBox targetCombo, string functionName)
    {
        _captureTargetCombo = targetCombo;
        _captureTargetName = functionName;
        CaptureStatusText.Text = functionName;
        StatusText.Text = $"Щёлкни тумблером для {functionName}";
        _previousButtons = _buttons.Select(b => b.Pressed).ToArray();
        _previousPovText = _pov.Value;
        _captureAxisBaseline = _axes.Select(a => a.Raw).ToArray();
    }

    private static string DecodePov(int pov)
    {
        if (pov < 0 || pov == 0xFFFF || pov == 65535)
            return "CENTER";

        return pov switch
        {
            0 => "UP",
            4500 => "UP-RIGHT",
            9000 => "RIGHT",
            13500 => "DOWN-RIGHT",
            18000 => "DOWN",
            22500 => "DOWN-LEFT",
            27000 => "LEFT",
            31500 => "UP-LEFT",
            _ => pov.ToString()
        };
    }

    private void LoadOsdLayout(OsdLayout layout)
    {
        _osdLayout = layout;
        _osdElements.Clear();

        foreach (OsdElement element in layout.Elements)
            _osdElements.Add(element);

        RenderOsdPreview();
    }

    private OsdLayout BuildCurrentOsdLayout()
    {
        var layout = new OsdLayout
        {
            Cols = 30,
            Rows = 16,
            Elements = _osdElements
                .Select(e => new OsdElement
                {
                    Id = e.Id,
                    Enabled = e.Enabled,
                    Row = Math.Clamp(e.Row, 0, 15),
                    Col = Math.Clamp(e.Col, 0, GetMaxOsdCol(e)),
                    Template = e.Template
                })
                .ToList()
        };

        _osdLayout = layout;
        return layout;
    }

    private void RenderOsdPreview()
    {
        if (OsdPreviewCanvas == null)
            return;

        OsdPreviewCanvas.Children.Clear();
        _osdVisualElements.Clear();

        for (int col = 0; col <= 30; col++)
        {
            var line = new Line
            {
                X1 = col * OsdCellWidth,
                X2 = col * OsdCellWidth,
                Y1 = 0,
                Y2 = 16 * OsdCellHeight,
                Stroke = (Brush)FindResource("Line"),
                StrokeThickness = col % 5 == 0 ? 1.2 : 0.6
            };
            OsdPreviewCanvas.Children.Add(line);
        }

        for (int row = 0; row <= 16; row++)
        {
            var line = new Line
            {
                X1 = 0,
                X2 = 30 * OsdCellWidth,
                Y1 = row * OsdCellHeight,
                Y2 = row * OsdCellHeight,
                Stroke = (Brush)FindResource("Line"),
                StrokeThickness = row % 4 == 0 ? 1.2 : 0.6
            };
            OsdPreviewCanvas.Children.Add(line);
        }

        foreach (OsdElement element in _osdElements.Where(e => e.Enabled))
            DrawOsdElement(element);
    }

    private void DrawOsdElement(OsdElement element)
    {
        string text = RenderTemplate(element.Template);
        int textCells = Math.Max(1, Math.Min(30, text.Length));
        int row = Math.Clamp(element.Row, 0, 15);
        int col = Math.Clamp(element.Col, 0, Math.Max(0, 30 - textCells));
        double width = Math.Max(OsdCellWidth, textCells * OsdCellWidth);

        var border = new Border
        {
            Tag = element,
            Width = width,
            Height = OsdCellHeight,
            Background = new SolidColorBrush(Color.FromArgb(190, 22, 36, 46)),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 0),
            Child = new TextBlock
            {
                Tag = element,
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("Text"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            }
        };

        Canvas.SetLeft(border, col * OsdCellWidth);
        Canvas.SetTop(border, row * OsdCellHeight);
        OsdPreviewCanvas.Children.Add(border);
        _osdVisualElements[border] = element;
    }

    private string RenderTemplate(string template)
    {
        string text = template ?? "";
        foreach ((string key, string value) in GetOsdValues())
            text = text.Replace("{" + key + "}", value, StringComparison.Ordinal);

        return text;
    }

    private IReadOnlyDictionary<string, string> GetOsdValues()
    {
        bool hasTelemetry = _telemetry.LastUpdateUtc != default;
        bool linkOnline = _telemetry.LinkOnline;

        string mode = hasTelemetry ? _telemetry.Mode : "UNKNOWN";
        string armed = hasTelemetry ? _telemetry.ArmedText : "DISARMED";
        string bat = hasTelemetry ? _telemetry.BatteryVoltage.ToString("0.0", CultureInfo.InvariantCulture) : "--.-";
        string batA = hasTelemetry ? _telemetry.BatteryCurrent.ToString("0.0", CultureInfo.InvariantCulture) : "-.-";
        string battery = hasTelemetry ? $"{bat}V {batA}A" : "--.-V";
        string gps = hasTelemetry ? $"{_telemetry.GpsFix} SAT {_telemetry.Satellites}" : "NO DATA";
        string alt = hasTelemetry ? _telemetry.Altitude.ToString("0.0", CultureInfo.InvariantCulture) : "--.-";
        string altitude = hasTelemetry ? $"{alt} m" : "--.- m";
        string spd = hasTelemetry ? _telemetry.GroundSpeed.ToString("0.0", CultureInfo.InvariantCulture) : "--.-";
        string speed = hasTelemetry ? $"{spd} m/s" : "--.- m/s";
        string roll = hasTelemetry ? _telemetry.Roll.ToString("0.0", CultureInfo.InvariantCulture) : "--.-";
        string pitch = hasTelemetry ? _telemetry.Pitch.ToString("0.0", CultureInfo.InvariantCulture) : "--.-";
        string yaw = hasTelemetry ? _telemetry.Yaw.ToString("0", CultureInfo.InvariantCulture) : "---";
        string attitude = hasTelemetry ? $"R:{roll} P:{pitch} Y:{yaw}" : "R:-- P:-- Y:---";

        return new Dictionary<string, string>
        {
            ["mode"] = mode,
            ["armed"] = armed,
            ["battery"] = battery,
            ["bat"] = bat,
            ["batV"] = bat,
            ["batA"] = batA,
            ["gps"] = gps,
            ["gpsFix"] = hasTelemetry ? _telemetry.GpsFix.ToString() : "0",
            ["sats"] = hasTelemetry ? _telemetry.Satellites.ToString() : "0",
            ["link"] = linkOnline ? "OK" : "LOST",
            ["linkText"] = linkOnline ? "LINK OK" : "LINK LOST",
            ["alt"] = alt,
            ["altitude"] = altitude,
            ["spd"] = spd,
            ["speed"] = speed,
            ["roll"] = roll,
            ["pitch"] = pitch,
            ["yaw"] = yaw,
            ["attitude"] = attitude,
            ["ping"] = _lastAck == DateTime.MinValue ? "--" : Math.Max(0, (int)(DateTime.UtcNow - _lastAck).TotalMilliseconds).ToString()
        };
    }

    private void OsdElements_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (OsdElement element in e.OldItems)
                element.PropertyChanged -= OsdElement_PropertyChanged;
        }

        if (e.NewItems != null)
        {
            foreach (OsdElement element in e.NewItems)
                element.PropertyChanged += OsdElement_PropertyChanged;
        }

        RenderOsdPreview();
    }

    private void OsdElement_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is OsdElement element &&
            (e.PropertyName == nameof(OsdElement.Row) ||
             e.PropertyName == nameof(OsdElement.Col) ||
             e.PropertyName == nameof(OsdElement.Template)))
        {
            element.Row = Math.Clamp(element.Row, 0, 15);
            element.Col = Math.Clamp(element.Col, 0, GetMaxOsdCol(element));
        }

        RenderOsdPreview();
    }

    private void OsdElementsGrid_CurrentCellChanged(object sender, EventArgs e)
    {
        OsdElementsGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        OsdElementsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        RenderOsdPreview();
    }

    private void AddOsdElement_Click(object sender, RoutedEventArgs e)
    {
        int index = _osdElements.Count + 1;
        _osdElements.Add(new OsdElement
        {
            Id = $"custom{index}",
            Enabled = true,
            Row = Math.Min(15, index % 16),
            Col = 0,
            Template = $"CUSTOM{index}:{{mode}}"
        });
        OsdStatusText.Text = "Element added";
    }

    private void DeleteOsdElement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: OsdElement element })
        {
            _osdElements.Remove(element);
            OsdStatusText.Text = "Element deleted";
        }
    }

    private void ResetDefaultOsd_Click(object sender, RoutedEventArgs e)
    {
        LoadOsdLayout(OsdDefaults.CreateLayout());
        OsdStatusText.Text = "Default layout restored";
    }

    private void SaveLocalOsd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _osdLayoutStore.Save(BuildCurrentOsdLayout());
            OsdStatusText.Text = $"Saved: {_osdLayoutStore.LayoutPath}";
        }
        catch (Exception ex)
        {
            OsdStatusText.Text = "Save failed";
            MessageBox.Show($"Ошибка сохранения OSD layout: {ex.Message}");
        }
    }

    private void LoadLocalOsd_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadOsdLayout(_osdLayoutStore.Load());
            OsdStatusText.Text = $"Loaded: {_osdLayoutStore.LayoutPath}";
        }
        catch (Exception ex)
        {
            OsdStatusText.Text = "Load failed";
            MessageBox.Show($"Ошибка загрузки OSD layout: {ex.Message}");
        }
    }

    private void SendOsdLayout_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string json = OsdLayoutStore.Serialize(BuildCurrentOsdLayout());
            _relayClient.SendDroneCommand(RoomBox.Text, $"OSD_LAYOUT;json={json}");
            OsdStatusText.Text = "OSD layout sent";
        }
        catch (Exception ex)
        {
            OsdStatusText.Text = "Send failed";
            MessageBox.Show($"Не удалось отправить OSD layout. Подключись к VPS и повтори.\n\n{ex.Message}");
        }
    }

    private void SendOsdTestValues_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string json = JsonSerializer.Serialize(OsdDefaults.TestValues, OsdLayoutStore.CompactJsonOptions);
            _relayClient.SendDroneCommand(RoomBox.Text, $"OSD_VALUES;json={json}");
            OsdStatusText.Text = "Test values sent";
        }
        catch (Exception ex)
        {
            OsdStatusText.Text = "Send failed";
            MessageBox.Show($"Не удалось отправить OSD values. Подключись к VPS и повтори.\n\n{ex.Message}");
        }
    }

    private void OsdPreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        OsdElement? element = FindOsdElementFromSource(e.OriginalSource as DependencyObject);
        if (element == null || !element.Enabled)
            return;

        _draggingOsdElement = element;
        OsdPreviewCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OsdPreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingOsdElement == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        Point point = e.GetPosition(OsdPreviewCanvas);
        MoveOsdElementToPoint(_draggingOsdElement, point);
        e.Handled = true;
    }

    private void OsdPreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingOsdElement == null)
            return;

        Point point = e.GetPosition(OsdPreviewCanvas);
        MoveOsdElementToPoint(_draggingOsdElement, point);
        _draggingOsdElement = null;
        OsdPreviewCanvas.ReleaseMouseCapture();
        OsdStatusText.Text = "Element moved";
        e.Handled = true;
    }

    private void MoveOsdElementToPoint(OsdElement element, Point point)
    {
        string text = RenderTemplate(element.Template);
        int textCells = Math.Max(1, Math.Min(30, text.Length));
        int row = Math.Clamp((int)Math.Floor(point.Y / OsdCellHeight), 0, 15);
        int col = Math.Clamp((int)Math.Floor(point.X / OsdCellWidth), 0, Math.Max(0, 30 - textCells));

        element.Row = row;
        element.Col = col;
    }

    private int GetMaxOsdCol(OsdElement element)
    {
        int textCells = Math.Max(1, Math.Min(30, RenderTemplate(element.Template).Length));
        return Math.Max(0, 30 - textCells);
    }

    private static OsdElement? FindOsdElementFromSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is FrameworkElement { Tag: OsdElement element })
                return element;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e) => SaveSettings();
    private void LoadSettings_Click(object sender, RoutedEventArgs e) => LoadSettingsIfExists(true);

    private void SaveSettings()
    {
        try
        {
            var s = new OperatorSettings
            {
                VpsHost = VpsHostBox.Text.Trim(),
                VpsPort = int.TryParse(VpsPortBox.Text, out int p) ? p : 50555,
                Room = RoomBox.Text.Trim(),
                RollAxis = RollAxisCombo.SelectedItem?.ToString() ?? "X",
                PitchAxis = PitchAxisCombo.SelectedItem?.ToString() ?? "Y",
                ThrottleAxis = ThrottleAxisCombo.SelectedItem?.ToString() ?? "Z",
                YawAxis = YawAxisCombo.SelectedItem?.ToString() ?? "R",
                ArmButton = ComboToButton(ArmButtonCombo),
                LoiterButton = ComboToButton(LoiterButtonCombo),
                RtlButton = ComboToButton(RtlButtonCombo),
                LandButton = ComboToButton(LandButtonCombo),
                ManualButton = ComboToButton(ManualButtonCombo),
                ArmSwitch = ArmButtonCombo.SelectedItem?.ToString() ?? OperatorConstants.Unassigned,
                LoiterSwitch = LoiterButtonCombo.SelectedItem?.ToString() ?? OperatorConstants.Unassigned,
                RtlSwitch = RtlButtonCombo.SelectedItem?.ToString() ?? OperatorConstants.Unassigned,
                LandSwitch = LandButtonCombo.SelectedItem?.ToString() ?? OperatorConstants.Unassigned,
                ManualSwitch = ManualButtonCombo.SelectedItem?.ToString() ?? OperatorConstants.Unassigned,
                Axes = _axes.Select(a => new AxisCalibration { AxisName = a.AxisName, Min = a.Min, Center = a.Center, Max = a.Max, Invert = a.Invert }).ToList()
            };

            _settingsStore.Save(s);
            StatusText.Text = "CONFIG SAVED";
        }
        catch (Exception ex)
        {
            StatusText.Text = "CONFIG SAVE ERROR";
            MessageBox.Show($"Ошибка сохранения настроек: {ex.Message}");
        }
    }

    private void LoadSettingsIfExists(bool showMessage = false)
    {
        OperatorSettings? s;
        try
        {
            s = _settingsStore.Load();
        }
        catch (Exception ex)
        {
            if (showMessage) MessageBox.Show($"Ошибка загрузки настроек: {ex.Message}");
            return;
        }

        if (s == null)
        {
            if (showMessage) MessageBox.Show("Настройки не найдены");
            return;
        }

        VpsHostBox.Text = s.VpsHost;
        VpsPortBox.Text = s.VpsPort.ToString();
        RoomBox.Text = s.Room;
        RollAxisCombo.SelectedItem = s.RollAxis;
        PitchAxisCombo.SelectedItem = s.PitchAxis;
        ThrottleAxisCombo.SelectedItem = s.ThrottleAxis;
        YawAxisCombo.SelectedItem = s.YawAxis;
        ArmButtonCombo.SelectedItem = s.ArmSwitch;
        LoiterButtonCombo.SelectedItem = s.LoiterSwitch;
        RtlButtonCombo.SelectedItem = s.RtlSwitch;
        LandButtonCombo.SelectedItem = s.LandSwitch;
        ManualButtonCombo.SelectedItem = s.ManualSwitch;

        foreach (var cal in s.Axes)
        {
            var a = _axes.FirstOrDefault(x => x.AxisName == cal.AxisName);
            if (a != null) { a.Min = cal.Min; a.Center = cal.Center; a.Max = cal.Max; a.Invert = cal.Invert; }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveSettings();
        _relayClient.Dispose();
        base.OnClosed(e);
    }
}
