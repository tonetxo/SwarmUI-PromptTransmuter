# Prompt Transmuter for SwarmUI

A [SwarmUI](https://github.com/mcmonkeyprojects/SwarmUI) extension that mutates prompts into variants, deterministically from a seed. It runs entirely in the SwarmUI server (no ComfyUI nodes, models, or network access needed) and works with any backend.

## Modes

- **words**: replaces random words with cinematic vocabulary (camera, lighting, mood terms).
- **internal**: replaces random words with other words from the same prompt.
- **synonyms**: replaces words that have a known synonym (only those words are counted).

**Strength** is the probability (0-100%) that each word is changed. The same prompt, mode, strength and seed always give the same variants, including across server restarts.

SwarmUI prompt syntax is never touched: `<...>` tags (`<segment:...>`, `<region:...>`, `<base>`, `<lora:...>`, ...) and internal markers (embeddings, trigger phrases) are kept verbatim, and line breaks are preserved.

## Usage

- **Tool**: in the Generate tab, open **Tools → Prompt Transmuter**. Pick a mode, strength, variant count (1-16) and seed, press **Transmute**, then **Send first variant to prompt** or **Copy**. Requires the `[Prompt Transmuter] Use Prompt Transmuter` permission (granted to normal users by default).
- **Automatic**: enable the **Prompt Transmuter Auto** parameter group (Advanced Options must be shown). While enabled, the prompt is transmuted before every generation with the selected mode/strength/seed (`-1` = random seed each time). The saved metadata contains the already transmuted prompt; the mode is not reused when reusing parameters, so the prompt is not transmuted twice.

## Installation

Clone or copy this folder into `SwarmUI/src/Extensions/SwarmUI-PromptTransmuter/` and restart SwarmUI (extensions are compiled at launch). To update, pull the repository and restart; to uninstall, delete the folder and restart.

## License

MIT, by Tonetxo. See [LICENSE](LICENSE).
