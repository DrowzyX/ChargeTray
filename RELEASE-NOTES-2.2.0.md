# ChargeTray 2.2.0 Preview — Device Intelligence + Protocol Lab + Fluent UI

## New
- Multi-source Battery Engine: native Windows/Bluetooth/HID readings and opt-in local Vendor Bridge with source freshness checks. Source and time are displayed on cards.
- Vendor Bridge v1: local JSON integration contract. This does not automatically read proprietary OEM applications. Real per-vendor adapters are still required.
- Protocol Lab: before/after snapshots, comparisons of percentage, connection, charging and previously collected HID metadata, local export. No new unknown HID commands.
- UI Polish: improved light theme, six real colorful preset chips in both themes; existing RGB and Island preserved.
- Existing AULA, Razer, Bluetooth and Xenta readers unchanged.

## Vendor Bridge
Trusted local adapter writes to %LOCALAPPDATA%\ChargeTray\vendor-bridge.json
Enable Settings → Разрешить локальный Vendor Bridge.
JSON example:
~~~json
{
  "Version": 1,
  "Readings": [
    {
      "DeviceKey": "community:A8A5:2255",
      "Percent": 76,
      "ObservedUtc": "2026-11-24T17:00:00Z",
      "Source": "Vendor Bridge",
      "Charging": false,
      "Confidence": 95
    }
  ]
}
~~~
Replace timestamp with actual UTC time and value with actual reading. The connected DeviceKey must match. Stale data (over 2 minutes), invalid levels and unknown devices are ignored. No automatic vendor app scraping is included.

## Protocol Lab
Captures only existing device readings and sanitized HID metadata. Use Before → change connection/charging → Refresh → After → Compare/Export JSON. No packet sniffer, kernel driver, or arbitrary HID write commands. Review exported JSON before sharing as redaction is best-effort.

## Windows
Windows 10/11 x64 self-contained ZIP. Close existing app before extracting and running. All RGB and personalized preferences remain.

This is a preview pending Windows UI and real-device testing.