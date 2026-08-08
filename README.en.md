[한국어](README.md) | [English](README.en.md) | [日本語](README.ja.md) | [简体中文](README.zh-Hans.md) | [繁體中文](README.zh-Hant.md)

# sts2_matchmaker

A mod that adds **matchmaking (auto-search/host), rehosting, and kick/ban** to Slay the Spire 2 multiplayer.

> This mod is registered as not affecting gameplay (`affects_gameplay: false` in `mod_manifest.json`), so **playing multiplayer with someone who doesn't have it installed works completely fine.** Joining directly via an invite link and other existing methods work exactly the same regardless of whether the mod is installed. The "Match" (auto-search) feature itself, however, only works between people who both have this mod installed.

## Usage

### Matching

Open the main menu → Multiplayer screen and press the new **"Match"** button to open the matching settings window.

- **Community Name**: Use this to only group up with a specific circle. Leave it blank to match with anyone.
- **Language**: Only match with users of a specific language. Selecting "Any" removes the filter.
- **Run Type**: Standard / Daily / Custom.
- **Ascension Level**: Your desired ascension. Selecting "Any" matches any room at or below the highest ascension you've cleared.
- **Player Count**: Target party size. "Any" is only selectable from the main matching popup (since you might end up hosting, this works independently of the actual room capacity).
- **Allow Hosting**: If unchecked, you'll only attempt to join, and keep waiting instead of becoming a host if no match is found.

Press the checkmark (✓) button in the bottom right to start matching. If a room matching your conditions is found, you auto-join it; if none is found and "Allow Hosting" is on, a room is opened automatically.

From an already-hosted lobby, you can also open the same settings window via the **"Match"** button next to your character card, to expose your remaining slots to search. The same screen has a **"Copy Link"** button that copies a link straight to your clipboard, letting someone join this room directly.

### Rehosting

Lets a multiplayer run that ended abnormally regroup automatically.

- **Host**: Pressing the (pre-existing) **"Host from Save"** button in the multiplayer menu automatically opens it up as searchable. No need to send invite links to participants separately.
- **Participant**: Press the **"Wait"** button in the matching settings window, and you'll be found and connected automatically the moment the host reopens the save. Only people who were actually in that run can join (anyone not in the save is automatically rejected).
- Once the original roster is fully back together, everyone is automatically set to ready, continuing right away with no extra waiting.

### Kick / Ban

Buttons appear next to each player card in the lobby.

- **Host**: "Kick" (just removes them) / "Ban" (removes them immediately and adds them to your ban list, so you'll never match with them again)
- **Guest**: "Register Ban" (can't remove them right now, but excludes them from your future matches)
- You can also ban past teammates from the run history (past run records) screen.
- If the host kicks you, the popup that appears includes an extra button to immediately ban that host.

You can check/clear your ban list from the **"Ban List"** tab in the matching settings window. This list is purely local to you - a lobby hosted by someone on your ban list simply never shows up when searching.

### Mod Info

The **"Mod Info"** tab in the matching settings window shows the list of currently installed mods. What's shown here isn't every installed mod, only the ones that **affect multiplayer matching (gameplay-relevant mods)**. Matching always requires this mod list to fully match the other side's - it's a mandatory condition that can't be turned off.

---

*This mod was developed via vibe coding using Claude Code.*
