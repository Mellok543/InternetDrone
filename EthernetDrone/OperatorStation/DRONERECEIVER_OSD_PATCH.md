# DroneReceiver OSD Patch

`DroneReceiver` is not present in this repository. Add the following handling before the normal control-packet handling in the UDP receive loop:

```csharp
if (packet.Contains(";OSD_LAYOUT;json="))
{
    var json = packet.Split(";OSD_LAYOUT;json=", 2)[1];
    File.WriteAllText("/home/mell/osd_layout.json", json);
    Console.WriteLine("OSD layout saved");
    return;
}

if (packet.Contains(";OSD_VALUES;json="))
{
    var json = packet.Split(";OSD_VALUES;json=", 2)[1];
    File.WriteAllText("/tmp/drone_osd_values.json", json);
    Console.WriteLine("OSD values saved");
    return;
}
```

The receiver file must include:

```csharp
using System.IO;
```

Install the OSD grid generator on Raspberry Pi:

```bash
sudo install -m 755 scripts/mell_osd_grid.py /usr/local/bin/mell_osd_grid.py
```

Run it manually for testing:

```bash
/usr/local/bin/mell_osd_grid.py
```

Expected output files:

```bash
cat /home/mell/osd_layout.json
cat /tmp/drone_osd_values.json
cat /tmp/drone_osd.txt
```
