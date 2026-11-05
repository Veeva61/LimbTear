# LimbTear

Rip NPCs apart with your bare hands in **BRUTALISTICK VR**.

> Works great together with **GiantPlayer**: as a x2 giant with x10 strength you tear enemies apart with ease.

## Features
- **Tear off body parts:** grab an arm, leg, head or torso piece and pull. When the real physics force on its joint stays above the threshold, it rips off (whatever hangs from it comes along).
- **Keep tearing:** a torn-off leg can be ripped in half again, the pelvis can be torn from the upper body, and so on.
- **Rip one part in half:** hold the same part with **both hands** and pull them apart. It is cut in two by the game's own slicing system.
- **Gore:** the game's own bleeding spots that drip and pool, blood particles and flesh sounds; uses the game's dismember damage.

## Configuration (`UserData/MelonPreferences.cfg` -> `[LimbTear]`)
| Setting | Default | Description |
| --- | --- | --- |
| Enabled | true | Master switch |
| TearForce | 1800 | Joint force (N) needed to rip a part off. Lower = easier |
| HoldTime | 0.2 | Seconds the pull must stay above TearForce |
| DismemberDamage | 60 | Damage dealt on tear |
| AllowHead | true | Heads can be ripped off |
| TorsoCanTear | true | Chest, neck and pelvis can be torn |
| Gore | true | Blood and sound on tear |
| BleedSources | 3 | Bleeding spots per wound side |
| SplitWithTwoHands | true | Rip one part in half with two hands |
| SplitPull | 0.18 | How far (m) to pull the controllers apart |
| SplitHoldTime | 0.25 | Seconds to keep pulling |
| Verbose | true | Log your strongest pull (to tune TearForce) |

---

# LimbTear (RU)

Рви NPC голыми руками.

> Отлично сочетается с **GiantPlayer**: великаном с силой x10 рвать врагов намного проще.

- Хватай руку, ногу, голову или часть туловища и тяни: при достаточной силе часть отрывается.
- Оторванную ногу можно порвать ещё раз пополам, таз оторвать от верха и т. д.
- Схвати одну часть **двумя руками** и разведи руки: она разорвётся надвое.
- Кровь, лужи и звуки — родные эффекты игры.
- Силу отрыва настраивает `TearForce` (по умолчанию 1800 Н).

## Building

Requires the .NET 6 SDK and BRUTALISTICK VR with MelonLoader 0.7.3 installed through r2modman
(the game must have been started once so MelonLoader generates the Il2Cpp assemblies).

    dotnet build -c Release -p:GameProfile="<path to your r2modman profile>"

The default profile is `%APPDATA%\r2modmanPlus-local\BrutalistickVr\profiles\Default`.
The built dll is copied into the profile's `Mods` folder.
