using Vortice.DirectInput;

namespace OperatorStation;

internal sealed class DirectInputJoystickReader : IDisposable
{
    private readonly IDirectInput8 _directInput = DInput.DirectInput8Create();
    private IDirectInputDevice8? _device;
    private Guid _deviceGuid;

    public IReadOnlyList<JoystickDevice> GetDevices()
    {
        return _directInput
            .GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly)
            .Select(CreateDeviceInfo)
            .ToList();
    }

    public void SelectDevice(JoystickDevice device, IntPtr windowHandle)
    {
        if (device.Source != JoystickDeviceSource.DirectInput)
            return;

        if (_deviceGuid == device.DirectInputGuid && _device != null)
            return;

        ReleaseDevice();
        _deviceGuid = device.DirectInputGuid;
        _device = _directInput.CreateDevice(device.DirectInputGuid);
        _device.SetDataFormat<RawJoystickState>();
        _device.SetCooperativeLevel(windowHandle, CooperativeLevel.Background | CooperativeLevel.NonExclusive);

        foreach (DeviceObjectInstance obj in _device.GetObjects(DeviceObjectTypeFlags.Axis))
            _device.GetObjectPropertiesById(obj.ObjectId).Range = new InputRange(0, 65535);

        _device.Acquire();
    }

    public JoystickSnapshot? Poll()
    {
        if (_device == null)
            return null;

        try
        {
            _device.Poll();
            JoystickState state = _device.GetCurrentJoystickState();
            int[] sliders = state.Sliders;
            int[] povs = state.PointOfViewControllers;

            return new JoystickSnapshot
            {
                Axes =
                [
                    state.X,
                    state.Y,
                    state.Z,
                    state.RotationX,
                    state.RotationY,
                    state.RotationZ,
                    sliders.Length > 0 ? sliders[0] : 32767,
                    sliders.Length > 1 ? sliders[1] : 32767
                ],
                Buttons = state.Buttons,
                Pov = povs.FirstOrDefault(-1)
            };
        }
        catch
        {
            try
            {
                _device.Acquire();
                _device.Poll();
                JoystickState state = _device.GetCurrentJoystickState();
                int[] sliders = state.Sliders;
                int[] povs = state.PointOfViewControllers;

                return new JoystickSnapshot
                {
                    Axes =
                    [
                        state.X,
                        state.Y,
                        state.Z,
                        state.RotationX,
                        state.RotationY,
                        state.RotationZ,
                        sliders.Length > 0 ? sliders[0] : 32767,
                        sliders.Length > 1 ? sliders[1] : 32767
                    ],
                    Buttons = state.Buttons,
                    Pov = povs.FirstOrDefault(-1)
                };
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        ReleaseDevice();
        _directInput.Dispose();
    }

    private static JoystickDevice CreateDeviceInfo(DeviceInstance instance)
    {
        return new JoystickDevice
        {
            Source = JoystickDeviceSource.DirectInput,
            DirectInputGuid = instance.InstanceGuid,
            Name = $"DI: {instance.ProductName}",
            AxisCount = 8,
            ButtonCount = 128
        };
    }

    private void ReleaseDevice()
    {
        if (_device == null)
            return;

        try { _device.Unacquire(); } catch { }
        _device.Dispose();
        _device = null;
        _deviceGuid = Guid.Empty;
    }
}
