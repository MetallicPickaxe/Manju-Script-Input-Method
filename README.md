# Manju Script Input Method

**ᠮᠠᠨᠵᡠ ᡥᡝᡵᡤᡝᠨ** (Manju Hergen, the Manchu alphabet)

Manju IME is an input method editor (IME) for Windows, designed for learners of Manchu: a program that lets you type a script your
keyboard has no keys for, here the Manchu script. You type the spelling of a Manchu word in Latin
letters, and a small window beside the text, the candidate window, shows the word in Manchu letters;
press **Space**, and the word goes into your document.

The candidate window shows one card for each form
the highlighted letter can take, each card the whole word with that form, so you see how a letter changes
with its place in the word, and a number key picks the form. Six input schemes let you type with a
spelling you already know, and whichever of them you type with, the display scheme can show the letters
typed in the spelling you learned, such as Möllendorff’s.

Manju IME enters only standard characters of Unicode, the standard that numbers every character of every
script: the Manchu letters and punctuation, the variation selectors that pick another form of a letter,
and the separators that set a suffix apart, all as Unicode defines them, and the Latin letters and
symbols you type on a standard keyboard. It uses no private-use characters and no codes that only one
font understands, so any font that follows Unicode draws the text. Each of the six input schemes, ways of
spelling Manchu in Latin letters, is typed with the Latin letters of a standard keyboard.

## Examples

The first three pictures below were taken in Windows 11, one frame for each key, on a web page written
top to bottom in Microsoft Edge, and the caption under each names the input scheme it was typed with. The
last one shows the candidate window in each of its eight themes.

### A word, key by key

![With the Möllendorff input scheme, hūwašabumbi is typed one key at a time on a page written top to bottom: h, u, a tap of Shift that turns the u into ū, w, a, s, a tap of Shift that turns the s into š, and abumbi. After each key the candidate window beside the text shows the word in Manchu letters, and Space enters it](docs/images/readme-word.gif)

*`hūwašabumbi` (to raise, to bring up), typed with the Möllendorff input scheme: a tap of **Shift** after
`u` and after `s` gives `ū` and `š`, as
[Tap Shift in the middle of a word](docs/usage.md#tap-shift-in-the-middle-of-a-word) describes.*

### An aphorism, then suffixes

![A page written top to bottom, with the Möllendorff input scheme. A tap of Shift switches to Latin input, and two lines are typed: Hippocrates: ars longa, vita brevis. and Hippocrates: art is long, life is short. On the third line, Hippocrates is typed in Latin letters, a tap of Shift switches back to Manchu input, and i and gisun follow in Manchu letters, with the colon from Shift and the semicolon key; the fourth line is muten golmin, jalgan foholon. with the Manchu comma and full stop. Space enters each Manchu word. Then, on a new page, manju-i, bi-de, boo-ci, bithe-be, gurun-i, and niyalma-de, a line each, with the hyphen key before each suffix](docs/images/readme-aphorism-suffixes.gif)

*An aphorism of Hippocrates in its Latin form (`ars longa, vita brevis`), in English, and in Manchu, our
translation; then suffixes after the `-` key. Typed with the Möllendorff input scheme: see
[Switch between Latin and Manchu](docs/usage.md#switch-between-latin-and-manchu),
[Punctuation](docs/usage.md#punctuation), and [Suffixes](docs/usage.md#suffixes) for the keys.*

### Letters drawn as one glyph, then syllables

![A page written top to bottom, with the Möllendorff input scheme: b followed by each vowel, then p; k, g, and h followed by e, i, and u, and, with a tap of Shift for the apostrophe, by a and o; then ongko, angga, bingha, denglu, dongmo, fangnai, abla, abma, alla, comlimbi, and amma. In each, two letters are drawn as one glyph, and Space enters it. Then, on a new page, the seven syllables typed with the Abkai input scheme, three in the top row and four in the bottom row](docs/images/readme-joined-syllables.gif)

*Every pair of letters that Noto Sans Mongolian, the font shipped with Manju IME, draws as one glyph, a
single shape, typed with the Möllendorff input scheme; then the seven syllables of the
[Syllables](docs/romanisation.md#syllables) table, typed with the Abkai input scheme.
[Letters drawn as one glyph](docs/romanisation.md#letters-drawn-as-one-glyph) shows the pairs more slowly,
and [Pick a form with the number keys](docs/usage.md#pick-a-form-with-the-number-keys) shows a pair drawn
apart.*

### The eight themes

![The candidate window after typing manju with the Möllendorff input scheme, in each of its eight themes: Windows 11, So Young, Solarized Dark, Court, High Contrast, Bean Green, Google, and Android](docs/images/themes.png)

*The candidate window in its eight [themes](docs/usage.md#themes), sets of colours that the settings file
picks, after typing `manju` with the Möllendorff input scheme.*

[Using Manju IME](docs/usage.md) has more pictures, with every key the IME uses.

## Requirements

| Windows | From version | Status |
|---|---|---|
| Windows 11 | 21H2 | Supported |
| Windows 10 | 22H2 | Uncertain (not tested) |

- A 64-bit Intel or AMD processor.
- Manju IME works only in programs that support Unicode text.
- A font for the Manchu script installed in Windows, such as [Noto Sans Mongolian](https://github.com/notofonts/mongolian).
- Installing it needs administrator rights, because it registers Manju IME for the whole computer. On a
  computer your organisation manages, ask your IT staff.
  [What Manju IME does to a computer](docs/deployment.md#what-manju-ime-does-to-a-computer) lists every
  change it makes.

## Documentation

| Document | For | What it covers |
|---|---|---|
| [Installing Manju IME](docs/installation.md) | Users | Setup, page by page, and the uninstaller |
| [Using Manju IME](docs/usage.md) | Users | How typing works, the candidate window, every key, the input schemes, and the themes |
| [Input Schemes](docs/romanisation.md) | Users | The six input schemes compared letter by letter and syllable by syllable, the spellings typed with Shift, the letters each scheme does not use, the letters drawn as one glyph, and where each scheme comes from |
| [Deploying Manju IME](docs/deployment.md) | IT staff | Unattended deployment with scripts for PowerShell, the command shell of Windows, and what Manju IME does to a computer |

## Install

Download the zip file of the latest release from the
[Releases](https://github.com/MetallicPickaxe/Manju-Script-Input-Method/releases) page and unzip it
into a folder you will keep. In the `Installer` folder, run `Install Manju IME.exe`, the setup program.
[Installing Manju IME](docs/installation.md) shows every page of Setup.

[Deploying Manju IME](docs/deployment.md) covers unattended deployment with the PowerShell scripts.

![Setup page by page: the first page, the language page with its list open, the folder page, Setup while it installs Manju IME, and the last page, which says Manju IME is installed](docs/images/setup.gif)

## Use

Press **Win+Space** and pick Manju IME. Type a Manchu word in Latin letters, and press **Space** to
enter it. [Using Manju IME](docs/usage.md) explains the candidate window and every key.

## Uninstall

In the `Installer` folder, run `Uninstall Manju IME.exe`. The files stay in the folder.
[Installing Manju IME](docs/installation.md#uninstall) shows the uninstaller.

## Licence

- The code of Manju IME is licensed under the MIT License: see [LICENSE](LICENSE), which also contains
  the third-party notices.
- The documentation, this README and the text and pictures in `docs`, and the Refined Romanisation
  (draft) input scheme are licensed under the Creative Commons Attribution 4.0 International License
  (CC BY 4.0): see [docs/LICENSE](docs/LICENSE).

## Acknowledgements

- [HarfBuzz](https://github.com/harfbuzz/harfbuzz), the text shaping engine: Manju IME joins Manchu
  letters with a C# port of HarfBuzz code.
- [Noto Sans Mongolian](https://github.com/notofonts/mongolian): the font of the candidate window.
- [Solarized](https://github.com/altercation/solarized), the colour scheme by Ethan Schoonover: the
  colours of the Solarized Dark and So Young themes.
- Weasel, the Windows front end of the [RIME](https://rime.im/) input method engine: Manju IME drew some
  inspiration from it.
- The makers of the five published input schemes: see [Input Schemes](docs/romanisation.md#acknowledgements).
- [Claude Code](https://www.anthropic.com/claude-code) by Anthropic, the AI coding agent that wrote the code
  and these documents to the author’s design and architecture, and [Codex](https://openai.com/codex/) by
  OpenAI, which reviewed the releases.
