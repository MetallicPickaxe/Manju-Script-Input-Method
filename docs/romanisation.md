# Input Schemes

An input scheme is a way of spelling Manchu in Latin letters. Manju IME, an input method editor (IME)
for typing the Manchu script, supports six input schemes: five published ones, and one of its own. In
the Latin order of their names:

- **Abkai**, the transliteration published on the website abkai.net.
- **BabelPad**, the Manchu input of the BabelPad text editor.
- **Hu**, the transliteration of the Manchu dictionary edited by Hu Zengyi.
- **Möllendorff**, the transliteration of Paul Georg von Möllendorff’s Manchu grammar of 1892. It is the default.
- **Norman**, the transliteration of Jerry Norman’s Manchu dictionary of 2013.
- **Refined Romanisation (draft)**, Manju IME’s own scheme.

[Using Manju IME](usage.md#input-and-display-schemes) says how to pick a scheme. The tables below show
how each scheme spells each letter, and [Where the schemes come from](#where-the-schemes-come-from)
gives the source of each.

## Letters

Unicode, the standard that numbers every character of every script, gives each Manchu letter a number
of its own, its code point, written U+ and four hexadecimal digits. A standard keyboard has no key for
`ū`, `š`, or `ž`, and its `'` key separates two letters: `n'g` gives ᠨ and ᡤ, where `ng` gives the one
letter ᠩ. [Spellings typed with Shift](#spellings-typed-with-shift) shows how to type the spellings with
a mark or an apostrophe, and [Using Manju IME](usage.md#the-apostrophe-key) describes the `'` key.

| Letter | Code points | Abkai | BabelPad | Hu | Möllendorff | Norman | Refined Romanisation (draft) |
|---|---|---|---|---|---|---|---|
| ᠠ | U+1820 | `a` | `a` | `a` | `a` | `a` | `a` |
| ᡝ | U+185D | `e` | `e` | `e` | `e` | `e` | `e` |
| ᡳ | U+1873 | `i` | `i` | `i` | `i` | `i` | `i` |
| ᠣ | U+1823 | `o` | `o` | `o` | `o` | `o` | `o` |
| ᡠ | U+1860 | `u` | `u` | `u` | `u` | `u` | `u` |
| ᡡ | U+1861 | `v` | `uu` | `uu` | `ū` | `ū` | `v` |
| ᠨ | U+1828 | `n` | `n` | `n` | `n` | `n` | `n` |
| ᡴ | U+1874 | `k` | `k` | `k` | `k` | `k` | `k` |
| ᡤ | U+1864 | `g` | `g` | `g` | `g` | `g` | `g` |
| ᡥ | U+1865 | `h` | `h` | `h` | `h` | `h` | `h` |
| ᠪ | U+182A | `b` | `b` | `b` | `b` | `b` | `b` |
| ᡦ | U+1866 | `p` | `p` | `p` | `p` | `p` | `p` |
| ᠰ | U+1830 | `s` | `s` | `s` | `s` | `s` | `s` |
| ᡧ | U+1867 | `x` | `x` | `sh` | `š` | `š` | `sh` |
| ᡨ | U+1868 | `t` | `t` | `t` | `t` | `t` | `t` |
| ᡩ | U+1869 | `d` | `d` | `d` | `d` | `d` | `d` |
| ᠯ | U+182F | `l` | `l` | `l` | `l` | `l` | `l` |
| ᠮ | U+182E | `m` | `m` | `m` | `m` | `m` | `m` |
| ᠴ | U+1834 | `q` | `c` | `ch` | `c` | `c` | `ch`, `tsh` |
| ᠵ | U+1835 | `j` | `j` | `zh` | `j` | `j` | `j`, `dzh` |
| ᠶ | U+1836 | `y` | `y` | `y` | `y` | `y` | `y` |
| ᡵ | U+1875 | `r` | `r` | `r` | `r` | `r` | `r` |
| ᡶ | U+1876 | `f` | `f` | `f` | `f` | `f` | `f` |
| ᠸ | U+1838 | `w` | `w` | `w` | `w` | `w` | `w` |
| ᠩ | U+1829 | `ng` | `ng` | `ng` | `ng` | `ng` | `ng` |
| ᠺ | U+183A | `k'` | `kh` | `kk` | `k'` | `k'` | `k'` |
| ᡬ | U+186C | `g'` | `gh` | `gg` | `g'` | `g'` | `g'` |
| ᡭ | U+186D | `h'` | `hh` | `hh` | `h'` | `h'` | `h'` |
| ᡮ | U+186E | `c` | `ts` | `c` | `ts'` | `ts` | `ts` |
| ᡯ | U+186F | `z` | `dz` | `z` | `dz` | `dz` | `dz` |
| ᡰ | U+1870 | `r'` | `z` | `rr` | `ž` | `ž` | `zh`, `rh`, `r'` |

## Syllables

In these syllables a letter takes a form of its own. The last code point of ᡯᡳ᠌, U+180C, is free variation
selector 2, which selects another form of the letter before it. An empty cell means that the source of
the scheme writes the syllable with its consonant alone, with no vowel letter after it: ᡯᡳ᠌ as `dz` in
BabelPad, Möllendorff, and Norman and as `z` in Hu, and ᡮᡟ as `ts` in Norman. A reader tells the syllable
from the single letter by its place in the word. The key tables of Manju IME 1.0.0 do not look at the place of
a spelling, so in these schemes the spelling types the single letter; to type the syllable, switch to
Abkai or Refined Romanisation (draft). ᡧᡳ and ᡰᡳ are the letters ᡧ and ᡰ followed by ᡳ, and every scheme
types them as those two letters.

| Syllable | Code points | Abkai | BabelPad | Hu | Möllendorff | Norman | Refined Romanisation (draft) |
|---|---|---|---|---|---|---|---|
| ᡮᡟ | U+186E U+185F | `cy'` | `tsy` | `cy` | `ts` |  | `tsy` |
| ᠰᡟ | U+1830 U+185F | `sy'` | `sy` | `sy` | `sy` | `sy` | `sy` |
| ᡱᡳ | U+1871 U+1873 | `qy'` | `chi` | `chy` | `c'y` | `cy` | `chy`, `tshy` |
| ᡷᡳ | U+1877 U+1873 | `jy'` | `zhi` | `zhy` | `jy` | `jy` | `jy`, `dzhy` |
| ᡯᡳ᠌ | U+186F U+1873 U+180C | `zy'` |  |  |  |  | `dzy` |
| ᡧᡳ | U+1867 U+1873 | `xi` | `xi` | `shi` | `ši` | `ši` | `shy` |
| ᡰᡳ | U+1870 U+1873 | `r'i` | `zi` | `rri` | `ži` | `ži` | `zhy`, `rhy`, `r'y` |

The picture below shows the seven syllables typed with the Abkai input scheme, in two rows. The table under it
has them in the same places, each with its Möllendorff spelling and, in brackets, the Abkai spelling
typed.

![A page written top to bottom in two rows, with the Abkai input scheme: three syllables in the top row and four in the bottom row, a line each, in the places of the table below. Each is typed letter by letter, with a tap of Shift for the apostrophe, and entered with Space](images/romanisation-syllables.gif)

| `dz` (`zy'`) | `ts` (`cy'`) | `sy` (`sy'`) |  |
|---|---|---|---|
| `jy` (`jy'`) | `c'y` (`qy'`) | `ši` (`xi`) | `ži` (`r'i`) |

The picture uses Abkai because Möllendorff writes the syllable ᡯᡳ᠌ as `dz`, the same as the letter ᡯ,
and a reader tells the two apart by their place in the word, which Manju IME 1.0.0 does not look at.

## Spellings typed with Shift

These spellings are typed with a tap of **Shift** in the middle of the word: type the letters up to the
mark or the apostrophe, then tap **Shift**.
[Using Manju IME](usage.md#tap-shift-in-the-middle-of-a-word) describes the tap. Where Abkai writes ʻ, as
in its syllable rʻi, Manju IME uses the plain apostrophe, as in `r'i`, a substitute that the Abkai page
allows.

In BabelPad and Hu, letter keys alone type every spelling.

| Scheme | Spelling | Manchu | How to type it |
|---|---|---|---|
| Abkai | `k'` | ᠺ | `k`, then tap **Shift** |
| Abkai | `g'` | ᡬ | `g`, then tap **Shift** |
| Abkai | `h'` | ᡭ | `h`, then tap **Shift** |
| Abkai | `cy'` | ᡮᡟ | `c`, `y`, then tap **Shift** |
| Abkai | `zy'` | ᡯᡳ᠌ | `z`, `y`, then tap **Shift** |
| Abkai | `r'` | ᡰ | `r`, then tap **Shift** |
| Abkai | `sy'` | ᠰᡟ | `s`, `y`, then tap **Shift** |
| Abkai | `qy'` | ᡱᡳ | `q`, `y`, then tap **Shift** |
| Abkai | `jy'` | ᡷᡳ | `j`, `y`, then tap **Shift** |
| Möllendorff | `ū` | ᡡ | `u`, then tap **Shift** |
| Möllendorff | `š` | ᡧ | `s`, then tap **Shift** |
| Möllendorff | `k'` | ᠺ | `k`, then tap **Shift** |
| Möllendorff | `g'` | ᡬ | `g`, then tap **Shift** |
| Möllendorff | `h'` | ᡭ | `h`, then tap **Shift** |
| Möllendorff | `ts'` | ᡮ | `t`, `s`, then tap **Shift** |
| Möllendorff | `ž` | ᡰ | `z`, then tap **Shift** |
| Möllendorff | `c'y` | ᡱᡳ | `c`, tap **Shift**, then `y` |
| Norman | `ū` | ᡡ | `u`, then tap **Shift** |
| Norman | `š` | ᡧ | `s`, then tap **Shift** |
| Norman | `k'` | ᠺ | `k`, then tap **Shift** |
| Norman | `g'` | ᡬ | `g`, then tap **Shift** |
| Norman | `h'` | ᡭ | `h`, then tap **Shift** |
| Norman | `ž` | ᡰ | `z`, then tap **Shift** |
| Refined Romanisation (draft) | `r'` | ᡰ | `r`, then tap **Shift** |
| Refined Romanisation (draft) | `r'y` | ᡰᡳ | `r`, tap **Shift**, then `y` |
| Refined Romanisation (draft) | `k'` | ᠺ | `k`, then tap **Shift** |
| Refined Romanisation (draft) | `g'` | ᡬ | `g`, then tap **Shift** |
| Refined Romanisation (draft) | `h'` | ᡭ | `h`, then tap **Shift** |

## Letters a scheme does not use

No scheme uses every letter of the keyboard. When you are not in the middle of a word, a letter that the
scheme does not use goes into the document as itself, a Latin letter. In the middle of a word, it joins
the letters typed and goes into the document as that Latin letter when the word does.

| Scheme | Letters it does not use |
|---|---|
| Abkai | none |
| BabelPad | `q`, `v` |
| Hu | `j`, `q`, `v`, `x` |
| Möllendorff | `q`, `v`, `x` |
| Norman | `q`, `v`, `x` |
| Refined Romanisation (draft) | `q`, `x` |

## Letters drawn as one glyph

The picture below shows every pair of letters that Noto Sans Mongolian, the font shipped with Manju IME,
draws as one glyph, a single shape for the two letters, typed with the Möllendorff input scheme.

![A page written top to bottom, with the Möllendorff input scheme: b followed by each vowel, then p; k, g, and h followed by e, i, and u, and, with a tap of Shift for the apostrophe, by a and o; then ongko, angga, bingha, denglu, dongmo, fangnai, abla, abma, alla, comlimbi, and amma. In each, two letters are drawn as one glyph, and Space enters it. From ongko on, Up takes the highlight back to the second letter of the pair, whose first card shows the pair joined](images/romanisation-joined.gif)

## Where the schemes come from

The sections below give the source of each scheme, in the Latin order of their names.

### Abkai

Abkai (ᠠᠪᡴᠠᡳ) is the transliteration published on abkai.net, a website that also calls itself Taiqing (太清) and
offers Manchu fonts and keyboards. It writes Manchu with the 26 basic Latin letters, the apostrophe, and
the hyphen alone.

### BabelPad

BabelPad is a free Unicode text editor for Windows by Andrew West, published on the BabelStone website.
For Manchu, its romanised input uses a modified Möllendorff transliteration.

### Hu

The Hu scheme is the transliteration of *Xin Man-Han da cidian*, the Manchu dictionary edited by 胡增益
(Hu Zengyi). The scheme has no English name; Manju IME names it after the editor. The dictionary was
first published in 1994, and its second edition, revised and enlarged, in Beijing in 2020.

### Möllendorff

This transliteration is named after Paul Georg von Möllendorff, and it is the one his *A Manchu
Grammar, with Analysed Texts*, printed in Shanghai in 1892, uses. According to the Wikipedia article
[Transliterations of Manchu](https://en.wikipedia.org/wiki/Transliterations_of_Manchu), most recent
Western publications on Manchu use it, in the form that Norman’s dictionary gives it. It is Manju IME’s
default input scheme.

### Norman

Jerry Norman’s *A Comprehensive Manchu-English Dictionary*, published in 2013, revises his *A Concise
Manchu-English Lexicon* of 1978, which already uses this transliteration. The preface of the 2013
dictionary says that it employs the Möllendorff system of romanisation. Its spellings differ from those
of the 1892 grammar in three places: ᡮ is `ts` where the grammar has `ts'`, ᡱᡳ is `cy` where the grammar
has `c'y`, and ᡮᡟ, which the grammar spells `ts`, has no spelling of its own. The Wikipedia article
[Transliterations of Manchu](https://en.wikipedia.org/wiki/Transliterations_of_Manchu) calls this form
the Norman system.

### Refined Romanisation (draft)

Refined Romanisation (draft) is Manju IME’s own scheme. It uses plain Latin letters and
the apostrophe, and some letters have more than one spelling, such as `j` and `dzh`. It is a draft:
later versions of Manju IME may change it.

## References

- Hu Zengyi, ed. *Xin Man-Han da cidian*. 2nd ed. Beijing: The Commercial Press, 2020. ISBN
  978-7-100-17460-2. [Record in CiNii Books](https://ci.nii.ac.jp/ncid/BC04876916).
- Hu Zengyi, ed. *Xin Man-Han da cidian*. Ürümqi: Xinjiang People’s Publishing House, 1994. ISBN 7-228-02404-4.
  [Record in CiNii Books](https://ci.nii.ac.jp/ncid/BN14969613).
- Möllendorff, Paul Georg von. *A Manchu Grammar, with Analysed Texts*. Shanghai: American Presbyterian
  Mission Press, 1892. [Scan at the Internet Archive](https://archive.org/details/cu31924023341112).
- Norman, Jerry. *A Concise Manchu-English Lexicon*. Seattle: University of Washington Press, 1978.
  ISBN 0-295-95574-0. [Record in CiNii Books](https://ci.nii.ac.jp/ncid/BA00463680).
- Norman, Jerry, with Keith Dede and David Prager Branner. *A Comprehensive Manchu-English
  Dictionary*. Cambridge, Massachusetts: Harvard University Asia Center, 2013. ISBN 978-0-674-07213-8.
  The preface names the Möllendorff system on page xii.
- abkai.net: [Manchu transliteration](https://abkai.net/zh/manchu/manchu-transliteration/),
  [About us](https://abkai.net/en/home/about-us/), and [Copyright](https://abkai.net/en/home/copyright/).
- BabelStone: [BabelPad](https://www.babelstone.co.uk/Software/BabelPad.html) and
  [BabelPad Features](https://www.babelstone.co.uk/Software/BabelPad_Features.html).
- [Transliterations of Manchu](https://en.wikipedia.org/wiki/Transliterations_of_Manchu), Wikipedia.

## Acknowledgements

Manju IME thanks the makers of the five published schemes, and their sources:

- Paul Georg von Möllendorff, for the transliteration of his grammar. The scan of the Cornell University
  Library notes no known copyright restrictions in the United States on the use of the text.
- Jerry Norman, for the lexicon of 1978 and the dictionary of 2013, and Keith Dede and David Prager
  Branner, who assisted with the dictionary.
- Hu Zengyi and the editors of *Xin Man-Han da cidian*.
- abkai.net, which publishes the Abkai transliteration. Manju IME wrote its own key table and display
  table for Abkai, following the spelling rules of the transliteration. The fonts and the keyboard
  software of abkai.net have terms of their own, for personal, non-commercial use only, and are not part
  of Manju IME.
- Andrew West, for BabelPad, which is free to download and use for personal or commercial purposes.

## Licence

This document and the Refined Romanisation (draft) scheme are licensed under the Creative Commons
Attribution 4.0 International License (CC BY 4.0): see [LICENSE](LICENSE). The five published schemes
belong to their makers, named above.
