<a id="readme-top"></a>

[![Contributors][contributors-shield]][contributors-url]
[![Forks][forks-shield]][forks-url]
[![Stargazers][stars-shield]][stars-url]
[![Issues][issues-shield]][issues-url]
[![MIT License][license-shield]][license-url]

<br />
<div align="center">
  <h3 align="center">Nomad Drive Map</h3>

  <p align="center">
    A BepInEx plugin for Nomad Drive: a minimap on the HUD and a fullscreen map, built from the game's own UI.
    <br />
    <a href="https://github.com/NunoD87/NomadDrive-Map#getting-started"><strong>Install guide »</strong></a>
    <br />
    <br />
    <a href="https://github.com/NunoD87/NomadDrive-Map/releases">Download</a>
    &middot;
    <a href="https://github.com/NunoD87/NomadDrive-Map/issues/new?labels=bug">Report Bug</a>
    &middot;
    <a href="https://github.com/NunoD87/NomadDrive-Map/issues/new?labels=enhancement">Request Feature</a>
  </p>
</div>

<details>
  <summary>Table of Contents</summary>
  <ol>
    <li><a href="#about-the-project">About The Project</a>
      <ul><li><a href="#built-with">Built With</a></li></ul>
    </li>
    <li><a href="#getting-started">Getting Started</a>
      <ul>
        <li><a href="#prerequisites">Prerequisites</a></li>
        <li><a href="#installation">Installation</a></li>
      </ul>
    </li>
    <li><a href="#usage">Usage</a></li>
    <li><a href="#how-it-works">How It Works</a></li>
    <li><a href="#known-limits">Known Limits</a></li>
    <li><a href="#roadmap">Roadmap</a></li>
    <li><a href="#contributing">Contributing</a></li>
    <li><a href="#license">License</a></li>
    <li><a href="#contact">Contact</a></li>
    <li><a href="#acknowledgments">Acknowledgments</a></li>
  </ol>
</details>

## About The Project

Nomad Drive has no map. This mod adds one, and it only needs to be installed by the player who wants it.

* A **minimap** on the HUD: your arrow, the places you have found, vehicles, other players and a trail of where you have been. Top-left by default; it can also sit above the health bar, in any corner, as a square or a wide GTA-style box.
* A **fullscreen map**. Press **M** to open it without pausing or taking the controls (a passenger can read it while the driver drives), or use the **Map** entry in the pause menu to pan and zoom with the mouse.
* **Ground pictures.** While you drive, the mod photographs the ground from above with the game's own camera, always with the same simulated sun and clean weather, and draws the pictures under the markers.
* **Only what you have seen.** Places and vehicles show up on the map when you see them (within a radius and with a clear line of sight), not before.
* **Your own vehicle models** are drawn on the map, rendered from above from the game's real models, and they turn as you drive.
* **Saved per world.** Places, trail and ground pictures are kept for each world, so they are there when you load your save again.
* It uses the game's own fonts, buttons and panel colours. No extra files, models or textures.

> **Status: v0.x. Tested in single player on the Early Access Playtest build. Multiplayer, the demo and gamepads are not tested.** Please report anything odd.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

### Built With

* [![BepInEx][BepInEx-badge]][BepInEx-url]
* [![Harmony][Harmony-badge]][Harmony-url]
* [![C#][CSharp-badge]][CSharp-url]
* [![GitHub Actions][Actions-badge]][Actions-url]

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Getting Started

### Prerequisites

* Nomad Drive (the Early Access Playtest build the mod was written against).
* **BepInExPack_NomadDrive** from Nexus Mods: <https://www.nexusmods.com/nomaddrive/mods/7>
  (BepInEx set up for this game; the game's own core libraries are stripped, so plain BepInEx does not start).

### Installation

1. Extract **BepInExPack_NomadDrive** into the game folder, next to the game `.exe`.
2. Start the game once from Steam, then quit, so BepInEx creates its folders.
3. Download `NomadDriveMap-<version>.zip` from [Releases][releases-url] and extract it into the same game folder.
   You should get `BepInEx/plugins/NomadDriveMap/NomadDriveMap.dll`.
4. Start the game from Steam.

To uninstall, delete that folder. The mod's saved data is in `BepInEx/NomadDriveMap/` (delete it too if you want the space back).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Usage

* **Minimap:** shows automatically in the world. **F7** hides/shows it.
* **Fullscreen map:** **M** opens it as an overlay and closes it again. The game keeps running and you keep the controls. **+** and **-** zoom.
* **Pause menu:** click **Map** (right below Settings) for the interactive map: drag to pan, scroll or **+** / **-** to zoom, **Recenter** and **Close** buttons.
* The first few minutes in a world are when the mod fills in the ground pictures around you; expect a brief moment of work every few seconds.

**Config** (`BepInEx/config/com.nunod87.nomaddrive.map.cfg`, created on first run; the mod does not reload it while the game runs, so restart after editing):

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | `Enabled` | `true` | Switch the mod off without uninstalling |
| Minimap | `Show` | `true` | Show the minimap |
| Minimap | `Layout` | `FixedCorner` | `FixedCorner` or `AboveHealthBar` |
| Minimap | `Corner` | `TopLeft` | Corner for `FixedCorner` (and the fallback) |
| Minimap | `Shape` | `Square` | `Square`, or `Wide` (as wide as the health bar, 1.6 : 1) |
| Minimap | `Size` | `300` | Size of the square, in pixels at 1080p |
| Minimap | `ZoomRadiusMetres` | `70` | Metres from you to the top/bottom edge |
| Minimap | `Opacity` | `0.85` | Background opacity |
| Minimap | `RotateWithView` | `false` | Rotate the minimap with where you look |
| Minimap | `ToggleKey` | `F7` | Show/hide the minimap |
| Map | `FullMapKey` | `M` | Open/close the fullscreen map |
| Map | `FullMapOpacity` | `0.92` | How solid the key-opened map is |
| Map | `PanelColor` | `353644` | Panel colour (hex); the default matches the health bar |
| Map | `ShowTrail` | `true` | Draw your trail |
| Map | `ShowPlaceNames` | `true` | Names next to places |
| Map | `ShowVehicleModels` | `true` | Vehicle models instead of a plain marker |
| Discovery | `OnlyShowSeen` | `true` | Only show places and vehicles you have seen |
| Discovery | `RevealRadiusMetres` | `150` | How far you can see something to put it on the map |
| GroundPictures | `Enabled` | `true` | Take ground pictures |
| GroundPictures | `PixelsPerMetre` | `6` | Sharpness of the pictures (bigger files, longer pause per picture) |
| GroundPictures | `SecondsBetweenPictures` | `4` | Minimum time between pictures outside the minimap area |
| GroundPictures | `MaxSizeMB` | `150` | Disk limit per world; the oldest pictures go first |
| GroundPictures | `BrightnessOffsetEV` | `0.5` | Brighter (+) or darker (-) for new pictures |

**Saved data** (`BepInEx/NomadDriveMap/`): `world_<seed>.json` (found places with name, type and position, plus the trail), `tiles/<seed>/` (the ground pictures, JPEG) and `sun.json` (the daytime sun the mod learned).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## How It Works

Found by reading the game's assemblies:

* The map is a separate overlay canvas made of the game's own UI types (images, TextMeshPro text, the pause menu button copied for **Map**). Markers are pooled images, so it costs next to nothing per frame.
* The world re-centres as you move (floating origin). Everything the mod remembers is stored in the game's true-world coordinates, so the trail and places stay in the right place.
* Places are the game's `PoiDefinition` objects. A place counts as seen when it is inside the reveal radius and a physics line from the camera is not blocked by something else (a blocker right next to it means you are looking at the place itself).
* The ground pictures are a hidden orthographic camera looking straight down, one 128 m tile at a time. For that one render the mod switches to its own look: a temporary high-priority effects volume (no vignette, bloom, fog, clouds, ambient occlusion...), a fixed sun angle, colour and strength, a fixed sky, fixed exposure, no weather (the game's wetness, rain and snow values are zeroed and put back right after). Everything is restored in the same frame, so your own view never changes.
* Vehicle models are rendered once per vehicle type, from above, against black and against white, and the transparency is worked out from the difference.
* The pause menu entry is a copy of the whole Settings entry, inserted after it, so the list lays it out.

Game types are reached through reflection, so this repository contains no game files.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Known Limits

* **Power lines are thin.** From above they can look faint or broken.
* **Tone steps at tile borders.** Neighbouring ground pictures are taken separately and can differ a little in tone.
* **Ground pictures show every structure that is loaded**, so a building can appear in the picture before its place is found on the map.
* **A small pause for each ground picture** (roughly 40 ms, about every 4 s while the map fills in). The first picture of a session takes a moment longer.
* **Fullscreen map from the pause menu needs a mouse.** The **M** overlay works without one.
* **Only explored ground shows.** The road is long and straight, so the map is a long thin strip.
* Roads that lead to a place you have not found are drawn like any other road.
* Multiplayer, the demo build and gamepads are untested. Other players are always shown on your map.
* Playtest and release builds can differ. A game update can break the mod; it logs an error if it cannot find the expected types.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Roadmap

- [x] Minimap and fullscreen map in the game's own UI
- [x] Places found by sight, saved per world
- [x] Trail, vehicle models, other players
- [x] Ground pictures with fixed light and clean weather
- [ ] Rotate the fullscreen map so the road runs left to right
- [ ] "Fit the whole route" view
- [ ] Route bar with places and biomes along the road
- [ ] Reload the config while the game runs
- [ ] Smoother borders between ground pictures
- [ ] Multiplayer, demo and gamepad checks

See the [open issues](https://github.com/NunoD87/NomadDrive-Map/issues) for a full list of proposed features (and known issues).

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Contributing

Contributions are what make the open source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'feat: add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

Build locally with `dotnet build src/NomadDriveMap/NomadDriveMap.csproj -c Release`.
CI builds every push, and pushing a tag like `v0.1.1` publishes a GitHub Release with the zip.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## License

Distributed under the MIT License. See `LICENSE` for more information.

Not affiliated with Indie Devil. Use at your own risk, and check the playtest terms before modding.

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Contact

Nuno Daniel - [@NunoD87](https://github.com/NunoD87)

Project Link: [https://github.com/NunoD87/NomadDrive-Map](https://github.com/NunoD87/NomadDrive-Map)

<p align="right">(<a href="#readme-top">back to top</a>)</p>

## Acknowledgments

* [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX)
* [BepInExPack_NomadDrive](https://www.nexusmods.com/nomaddrive/mods/7) for the game-ready BepInEx setup
* [NomadDrive-Wardrobe](https://github.com/NunoD87/NomadDrive-Wardrobe), whose pause menu button and panel technique this mod reuses
* Indie Devil, for making Nomad Drive

<p align="right">(<a href="#readme-top">back to top</a>)</p>

<!-- MARKDOWN LINKS & IMAGES -->
[contributors-shield]: https://img.shields.io/github/contributors/NunoD87/NomadDrive-Map.svg?style=for-the-badge
[contributors-url]: https://github.com/NunoD87/NomadDrive-Map/graphs/contributors
[forks-shield]: https://img.shields.io/github/forks/NunoD87/NomadDrive-Map.svg?style=for-the-badge
[forks-url]: https://github.com/NunoD87/NomadDrive-Map/network/members
[stars-shield]: https://img.shields.io/github/stars/NunoD87/NomadDrive-Map.svg?style=for-the-badge
[stars-url]: https://github.com/NunoD87/NomadDrive-Map/stargazers
[issues-shield]: https://img.shields.io/github/issues/NunoD87/NomadDrive-Map.svg?style=for-the-badge
[issues-url]: https://github.com/NunoD87/NomadDrive-Map/issues
[license-shield]: https://img.shields.io/github/license/NunoD87/NomadDrive-Map.svg?style=for-the-badge
[license-url]: https://github.com/NunoD87/NomadDrive-Map/blob/main/LICENSE
[releases-url]: https://github.com/NunoD87/NomadDrive-Map/releases
[BepInEx-badge]: https://img.shields.io/badge/BepInEx-5.4.23.5-1f6feb?style=for-the-badge
[BepInEx-url]: https://github.com/BepInEx/BepInEx
[Harmony-badge]: https://img.shields.io/badge/HarmonyX-patches-7b2cbf?style=for-the-badge
[Harmony-url]: https://github.com/BepInEx/HarmonyX
[CSharp-badge]: https://img.shields.io/badge/C%23-net472-239120?style=for-the-badge&logo=csharp&logoColor=white
[CSharp-url]: https://learn.microsoft.com/dotnet/csharp/
[Actions-badge]: https://img.shields.io/badge/GitHub_Actions-CI-2088FF?style=for-the-badge&logo=githubactions&logoColor=white
[Actions-url]: https://github.com/NunoD87/NomadDrive-Map/actions
