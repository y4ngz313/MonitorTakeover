# Monitor Takeover

![About Y4NGZ](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/shared/about-y4ngz.png)

I am Y4NGZ, an up and coming rapper out of NYC/Detroit. All of my mods are
inspired by the music I write. If you are interested in knowing more about me,
check out my SoundCloud: https://soundcloud.com/y4ngz

## Generative AI usage

The entire code base of this mod was created with generative AI. Assets are
free, licensed, paid for, or generated with AI tools. If you do not wish to play
with AI-generated content, do not install this mod.

## What Monitor Takeover does

Monitor Takeover adds a quota progression to Lethal Company. After each fulfilled quota the
ship monitors play a takeover broadcast with that quota's dialogue, then list the quota's
rewards. Rewards can unlock contracts, ship upgrades, constellations, moons, suits and store
items, or grant credits, ship fuel and Y4NGZ Upgrades tokens. Progression and rewards run on
the host and persist with the save. Out of the box no rewards are set, and every quota shows
the default Y4NGZ takeover.

## Requirements

Your mod manager installs these automatically:

- **BepInExPack**
- **DawnLib**
- **Y4NGZCore**

## The Takeover

When the ship reaches orbit after a fulfilled quota, the lights dim, the HUD hides, and
every ship monitor switches to the takeover feed for a configurable number of seconds.
The feed shows that quota's typed dialogue over the built-in Y4NGZ video, or over media
you supply.

![Monitor takeover](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/monitortakeover/takeover.gif)

## Media

Drop a PNG, JPG, or MP4 into `BepInEx/config/Y4NGZCompany/MonitorTakeovers` and name it in
a quota's section, or paste a full YouTube link instead. One file or link can cover every
quota, or a pool of files and links can be spread across the monitors so each screen plays
something different. Every player needs an identical copy of a local file; a player without
one sees the built-in video. YouTube links download in the background on each player's
machine, so the first takeover after adding one may still show the built-in video. After
that, the download is cached.

For audio you can keep the built-in mix (Charlie Brown mumbles and a siren at the start), let
the video's own audio play, or supply your own sound files or a soundtrack link. When the
takeover ends, the large ship monitors list everything that quota unlocked and granted.

![Quota rewards on the ship monitors](https://raw.githubusercontent.com/y4ngz313/Y4NGZMedia/main/monitortakeover/quota-rewards.png)

## Configuration

The configuration file is `BepInEx/config/com.y4ngz.company.monitortakeover.cfg`. Quotas 1
through 9 each have their own `Quota N` section with that quota's dialogue, media, audio and
rewards.

## Compatibility

Independent installation: Monitor Takeover needs only Y4NGZCore and the libraries listed under
Requirements. The other Y4NGZ packages are optional companions - install any combination
and each one lights up its extra behavior.

The required Core package is `Y4NGZ313-Y4NGZCore-1.0.10` or newer.

| Mod | What it adds |
| --- | --- |
| **GeneralImprovements** | The takeover and per-monitor media cover the Better Monitors wall as well. |
| **Contracted** | Contracts as quota milestone unlocks, listed on the ship monitors after the takeover. |
| **Y4NGZ Upgrades** | Upgrade tokens as quota milestone rewards. |
| **OpenBodyCams** | The body cam screen shows the takeover too, then goes back to the body cam. |

## GitHub

Source code: https://github.com/y4ngz313/MonitorTakeover

## Bugs

Report bugs at https://github.com/y4ngz313/MonitorTakeover/issues.
