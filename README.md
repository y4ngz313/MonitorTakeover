# Monitor Takeover

Plugin source for **Monitor Takeover**, a quota progression mod for Lethal Company:
each fulfilled profit quota takes over the ship monitors with a configurable video,
audio, and dialogue broadcast, then grants that quota's rewards (contract, ship upgrade,
constellation, suit, and store unlocks, or credits, ship fuel, and Y4NGZUpgrades tokens).

Download and play it from Thunderstore:
[Y4NGZ313/MonitorTakeover](https://thunderstore.io/c/lethal-company/p/Y4NGZ313/MonitorTakeover/).

## What is in this repository

The C# plugin source only. The bundled takeover video, audio clips, and compiled Unity
asset bundle the plugin loads at runtime are not published here — they ship inside the
Thunderstore package. The project also references `Y4NGZCore`, a shared support library
that is not public. This tree shows how the mod works; it does not build into a playable
mod on its own.

## Runtime dependencies

BepInEx 5.4.2305, DawnLib, and Y4NGZCore (listed as Thunderstore dependencies).

## Bugs and feedback

Open an issue with the quota number, whether the problem occurred for the host or a
client, what the media and audio settings were, and the relevant `BepInEx/LogOutput.log`
excerpt.

## Licensing

No open-source license is granted; this source is published for reference.
