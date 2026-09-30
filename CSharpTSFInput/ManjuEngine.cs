using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CSharpTSFInput.Messaging;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput
{
    /// <summary>
    /// ManjuEngine: the core input-processing engine.
    /// Responsible for: 1. keymap lookup 2. dictionary queries 3. font resources 4. asynchronous shaping.
    /// </summary>
    public class ManjuEngine
    {
        // Dictionary_Mappings holds the primary (default) transliteration:
        //   Latin key → first/primary codepoint string.
        // Dictionary_Alternatives maps Latin key → additional codepoint choices (beyond the primary).
        // When a keymap entry is a scalar, primary = that value, alternatives = empty.
        // When a keymap entry is a list, primary = list[0], alternatives = list[1..].
        // Purpose: same-Latin-key may correspond to multiple Unicode codepoints (e.g. Mongolian-shared vs
        // Manchu-specific letters that differ semantically but share the Latin transliteration). Alternative
        // codepoints surface as additional candidate cards alongside FVS variants (same flat hierarchy).
        private Dictionary<String, String> Dictionary_Mappings = new Dictionary<String, String>();
        private Dictionary<String, List<String>> Dictionary_Alternatives = new Dictionary<String, List<String>>();
        // Optional codepoint → human-readable meaning lookup for metadata row display.
        // Populated from the keymap's optional "meanings" section. Example:
        //   "meanings": maps each codepoint to its name, e.g. "Mongolian A", "Manchu I (dotted)"
        // If a codepoint has no entry, metadata falls back to "U+XXXX" hex form.
        // Used by GetLastLetterMeaning() to build "<type> · <meaning>" style semantic names so users
        // can distinguish same-glyph-different-codepoint candidates by reading the text row.
        private Dictionary<String, String> Dictionary_Meanings = new Dictionary<String, String>();
        // The engine does no word lookup of its own: that belongs to the optional dictionary module.
        // The legacy dict-match lookup sites get an empty list from the static no-op helper below.
        private static List<string> LegacyDictSearchEmptyShim(string snapshot) => new List<string>();

        /// <summary>How much of a candidate card's body one glyph takes, in design units; the card is
        /// sized from the sum. A rotated glyph (a Manchu letter or mark) takes its vertical advance. An
        /// upright glyph (a keycap symbol such as ; or &lt;, or a Latin letter with no Manchu counterpart)
        /// has no vertical advance at all, and the card body stacks it by its horizontal box plus the
        /// ink-edge gap (ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters), so that is what it takes
        /// here. Counted as 0, a word of symbols would get a card too short for it, and the card body
        /// would break the word into a second column in place of scrolling it.</summary>
        public static float CardColumnDesignUnits(ManjuShaperCore.FinalGlyph g) =>
            g.YAdvance != 0 ? Math.Abs(g.YAdvance) : Math.Abs(g.HAdvance) + ManjuDirectWriteRenderer.InkEdgeGapDesignUnits;

        private StringBuilder Builder_Composing = new StringBuilder();
        private Byte[]? Buffer_FontData = null;
        private String? _fontFilePath = null; // font path, for file-based loading

        // Process-Global Channel & Worker
        // Static: Shared across all TextService instances in this process.
        private static readonly Channel<ImeKeyInput> _inputChannel;
        private static readonly CancellationTokenSource _globalShutdownCts = new CancellationTokenSource();
        private static readonly Task _globalWorkerTask;
        
        /// <summary>
        /// A frame has been published.
        ///
        /// **CONTRACT : A SUBSCRIBER IS NEVER ENTERED
        /// FOR A COMPOSITION THAT HAS ALREADY BEEN CANCELLED.** Delivery is not best-effort and
        /// validation is not the subscriber's duty.
        ///
        /// How it is achieved, since it cannot be done by holding a lock across foreign code (a lock held
        /// there lets a throwing subscriber kill the single static worker, and a handler waiting on
        /// another thread's <see cref="Clear"/> deadlock it): dispatch is
        /// handed to <see cref="FrameDeliveryDispatcher"/>, which the host points at the thread that owns
        /// composition lifetime — the same thread that calls <see cref="Clear"/>. Once dispatch and
        /// cancellation run on one thread they cannot interleave, so the epoch re-check performed
        /// immediately before each invocation is exact rather than approximate. Nothing blocks waiting on
        /// a subscriber.
        ///
        /// SCOPE OF THE GUARANTEE: it holds
        /// for every publisher that labels its <see cref="DeliveryOrigin"/> honestly. The engine does not
        /// verify thread identity, so a caller that claims <c>DeliveryOrigin.Caller</c> while actually
        /// publishing from a thread another can cancel underneath would defeat it. Every in-tree
        /// publisher is the shared worker, which passes <c>DeliveryOrigin.Worker</c>, so the
        /// guarantee holds for the shipped product as built.
        ///
        /// Within that scope the guarantee is unconditional, and the price is stated
        /// plainly: when
        /// delivery cannot be marshalled onto the owning thread it is RETAINED or DROPPED, never run on
        /// the publishing thread. The opposite, accepting "no dispatcher yet, hand-off threw, HWND
        /// zero, PostMessage failed" as limits, means the worker enters the subscriber on each of
        /// them, which is exactly the interleaving this contract forbids. Concretely:
        ///   * hand-off throws       → DROPPED immediately, no retry. The dispatcher returns void, so an
        ///                             exception cannot say whether it had already accepted; retrying
        ///                             could deliver one frame's notification twice. Delivery is
        ///                             AT-MOST-ONCE, so "outcome unknown" is treated as delivered-or-lost
        ///                             and the frame is abandoned.
        ///   * A same-thread publisher has no cancellation window and still runs.
        ///   * error state, HWND set → RETAINED and a WM_APP_DELIVER is posted; WndProc drains ahead of
        ///                             the error branch, so it really is woken.
        ///   * HWND zero             → DROPPED. Retaining work nothing can ever wake is a leak, not a
        ///                             retention.
        ///   * PostMessage failed    → one retry; still failing, THIS work item is removed by identity
        ///                             and dropped, because nothing guarantees a later pump that
        ///                             would drain it.
        /// A dropped delivery costs one repaint and is superseded by the next frame.
        ///
        /// The one inline path is selected by the caller-supplied
        /// <see cref="DeliveryOrigin"/>, NOT by inspecting the current thread. <c>DeliveryOrigin.Caller</c>
        /// declares "I am publishing synchronously on my own thread and no other thread can cancel this
        /// composition underneath me"; the engine takes that at its word. The only in-tree publisher that
        /// can violate it is the shared worker, and it explicitly passes <c>DeliveryOrigin.Worker</c>.
        ///
        /// A subscriber that validates anyway subscribes to <see cref="OnFrameReadyFrame"/> and checks
        /// the frame it is handed with <c>IsFrameCurrent</c>.
        /// </summary>
        public event Action? OnFrameReady;

        /// <summary>
        /// Same signal, but CARRIES the frame object.
        /// Raised just before <see cref="OnFrameReady"/>, only for frames that passed the
        /// epoch publish check.
        ///
        /// **The same contract and the same limits apply verbatim: a subscriber is never entered for a
        /// cancelled composition while a dispatcher is installed.** Both events are public API, so
        /// both state it.
        /// </summary>
        public event Action<Messaging.RenderFrame>? OnFrameReadyFrame;


        static ManjuEngine()
        {
            // Initialize Process-Global Channel
            _inputChannel = Channel.CreateBounded<ImeKeyInput>(new BoundedChannelOptions(10) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true, // Only one static worker
                SingleWriter = false // Multiple engine instances can write
            });

            // Start Global Worker
            _globalWorkerTask = Task.Run(() => ProcessInputLoop(_globalShutdownCts.Token));
        }

        public ManjuEngine()
        {
            LoadConfiguration();   // must precede LoadDefaultFont — font path is read from config.
            LoadMappings();
            LoadDisplayScheme();   // display.scheme — independent of the input scheme.
            LoadDefaultFont();
            // Create the optional dictionary module via the registry
            // factory.
            // The core names NO concrete dictionary type — only the interface + registry.
            DictionaryModule = CSharpTSFInput.Abstractions.DictionaryModuleRegistry.Factory?.Invoke(this);
        }

        // Configuration store. Loaded once at engine ctor; used by LoadDefaultFont for the
        // font path (and exposed for downstream code via GetConfigStore).
        private readonly CSharpTSFInput.Configuration.ConfigurationStore _config = new();

        /// <summary>
        /// The optional dictionary/knowledge-window module, or null when the
        /// KnowledgeModule/ directory is not compiled in. ALL core interaction with the dictionary
        /// feature goes through this <see cref="CSharpTSFInput.Abstractions.IDictionaryModule"/> —
        /// the core references no concrete knowledge-window type.
        /// </summary>
        public CSharpTSFInput.Abstractions.IDictionaryModule? DictionaryModule { get; }

        private void LoadConfiguration()
        {
            try
            {
                string dllPath = Globals.GetModulePath();
                string? dllDir = Path.GetDirectoryName(dllPath);
                if (dllDir == null) return;
                LoadConfiguration(Path.Combine(dllDir, "Resource", "Configuration"));
            }
            catch (Exception)
            {
            }
        }

        /// <summary> Reads configuration.yaml in <paramref name="configDir"/>, remembers where it is and when
        /// it was written (for PollConfiguration), and takes the values read once per load.</summary>
        internal void LoadConfiguration(string configDir)
        {
            _config.Load(configDir);
            _configPath = Path.Combine(configDir, "configuration.yaml");
            _configWriteTimeUtc = ConfigWriteTime();
            _configCheckedAtMs = ConfigurationNowMs();
            // Single source of truth for the suffix-connector codepoint. Initialized
            // from yaml (default false = MVS), flippable at runtime from the settings menu.
            _useNnbspConnector = _config.GetBool("input.suffix_connector_narrow_no_break_space", false);
            // g/h/k description grouping mode (wording of the display only — never touches input).
            _ghkGrouping = _config.GetString("appearance.ghk_grouping", "loanword");
            // Apostrophe DISPLAY glyph in Preview/Candidate. Internal char stays the
            // plain ASCII ' (typing + parsing unchanged); only the shown glyph differs. Preference goes to
            // the IPA-formal modifier letter apostrophe (default). See ApostropheDisplayChar.
            _apostropheDisplay = _config.GetString("appearance.apostrophe_display", "modifier");
            // The separator mark (the ' key, ZWNJ) has its own display glyph, told apart from the transliteration apostrophe at a glance (default: the middle dot ·).
            _separatorDisplay = _config.GetString("appearance.separator_display", "middledot");
        }


        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SETTINGS FILE IS READ AGAIN WHEN IT CHANGES.
        //
        // PollConfiguration runs on every key event, on the calling thread: no file watcher and no
        // thread of its own. TextService calls it from OnTestKeyDown, and from OnKeyDown for a key the
        // test gate did not see, which is every key of a host that never calls OnTestKeyDown (Edge). It looks at configuration.yaml's last write time at most every
        // ConfigurationPollMs. A changed file is read and checked (ValidateConfiguration). A file that
        // cannot be read is tried again at the next look; a file that fails the check waits for the
        // next change; either way the settings in force stay as they are. Otherwise every key whose
        // value the file changed takes the file's value, replacing whatever the settings menu chose
        // for it this session, and a key the file did not change keeps the menu's choice. The menu
        // never writes the file.
        //
        // Applied at once: the keys in ReloadedKeys. Read at the next activation: font.path,
        // font.size, knowledge_window.enabled; install.language_identifier is read only by setup and registration.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        public const int ConfigurationPollMs = 500;

        private static readonly string[] ReloadedKeys =
        {
            "keymap.scheme", "display.scheme", "dictionary.active", "input.suffix_connector_narrow_no_break_space",
            "appearance.ghk_grouping", "appearance.apostrophe_display", "appearance.separator_display",
            "appearance.show_detailed_unicode_information", "appearance.show_theme_name", "appearance.theme", "display.cursive",
            "knowledge_window.visible_by_default",
        };

        private string _configPath = "";
        private DateTime _configWriteTimeUtc;
        private long _configCheckedAtMs;


        /// <summary> The time PollConfiguration measures its interval with, in milliseconds.</summary>
        private long ConfigurationNowMs() =>
            Environment.TickCount64;

        /// <summary> Raised on the polling thread after a read changed at least one key, with the keys
        /// that changed. The presenter and the dictionary module follow their own keys through it.</summary>
        public event Action<IReadOnlyList<string>>? ConfigurationChanged;

        private DateTime ConfigWriteTime()
        {
            try { return File.Exists(_configPath) ? File.GetLastWriteTimeUtc(_configPath) : default; }
            catch { return default; }
        }

        /// <summary> Reads the settings file again when it was written since it was last read and the
        /// last look was ConfigurationPollMs or more ago. Returns the keys whose values changed: empty
        /// when nothing was read, nothing changed, or the new file was not taken.</summary>
        public IReadOnlyList<string> PollConfiguration()
        {
            long now = ConfigurationNowMs();
            if (string.IsNullOrEmpty(_configPath) || now - _configCheckedAtMs < ConfigurationPollMs)
                return Array.Empty<string>();
            _configCheckedAtMs = now;
            DateTime written = ConfigWriteTime();
            if (written == _configWriteTimeUtc) return Array.Empty<string>();

            var fresh = CSharpTSFInput.Configuration.ConfigurationStore.TryRead(_configPath, out string? error);
            if (fresh == null)
            {
                return Array.Empty<string>();
            }
            _configWriteTimeUtc = written;
            string? rejected = ValidateConfiguration(fresh);
            if (rejected != null)
            {
                return Array.Empty<string>();
            }

            var changed = new List<string>();
            foreach (string key in ReloadedKeys)
                if (!string.Equals(_config.GetRaw(key), fresh.GetRaw(key), StringComparison.Ordinal))
                    changed.Add(key);
            string schemeBefore = ActiveInputScheme;
            _config.ReplaceWith(fresh);
            if (changed.Count == 0) return changed;
            ApplyChangedConfiguration(changed, schemeBefore);
            ConfigurationChanged?.Invoke(changed);
            return changed;
        }

        /// <summary> Why the file cannot be taken, or null when it can. It cannot when it names none of
        /// the settings (a file caught half written reads that way), or when a setting holds a value the
        /// input method has no meaning for.</summary>
        private static string? ValidateConfiguration(CSharpTSFInput.Configuration.ConfigurationStore store)
        {
            bool any = false;
            foreach (string key in ReloadedKeys) if (store.GetRaw(key) != null) { any = true; break; }
            if (!any) return "it names none of the settings";
            string[] booleans = { "true", "false", "yes", "no", "on", "off", "1", "0" };
            string? OneOf(string key, IEnumerable<string> allowed, StringComparison comparison)
            {
                string? v = store.GetRaw(key)?.Trim();
                if (v == null) return null;
                foreach (string a in allowed) if (string.Equals(a, v, comparison)) return null;
                return $"{key}: '{v}' is not one of {string.Join(", ", allowed)}";
            }
            return OneOf("keymap.scheme", InputSchemes, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("display.scheme", DisplaySchemes, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("appearance.separator_display", SeparatorShapes.Select(s => s.name), StringComparison.Ordinal)
                ?? OneOf("appearance.apostrophe_display", ApostropheShapes.Select(s => s.name), StringComparison.Ordinal)
                ?? OneOf("appearance.ghk_grouping", new[] { "loanword", "phonology" }, StringComparison.Ordinal)
                ?? OneOf("input.suffix_connector_narrow_no_break_space", booleans, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("appearance.show_detailed_unicode_information", booleans, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("appearance.show_theme_name", booleans, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("appearance.theme", ThemeKeys, StringComparison.Ordinal)
                ?? OneOf("display.cursive", booleans, StringComparison.OrdinalIgnoreCase)
                ?? OneOf("knowledge_window.visible_by_default", booleans, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary> Gives every changed key the file's value, replacing the settings menu's choice for it.
        /// The input scheme changes at once when nothing is being composed; during a composition the word
        /// keeps <paramref name="schemeBefore"/> and the file's scheme applies when it ends, the same
        /// deferral the settings menu uses.</summary>
        private void ApplyChangedConfiguration(IReadOnlyList<string> changed, string schemeBefore)
        {
            foreach (string key in changed)
            {
                switch (key)
                {
                    case "keymap.scheme":
                        if (Builder_Composing.Length == 0)
                        {
                            _schemeOverride = null;
                            _pendingSchemeSwitch = null;
                            LoadMappings();
                        }
                        else
                        {
                            _schemeOverride = schemeBefore;
                            _pendingSchemeSwitch = _config.GetString("keymap.scheme", "refined");
                        }
                        break;
                    case "display.scheme":
                        _displaySchemeOverride = null;
                        LoadDisplayScheme();
                        break;
                    case "dictionary.active":
                        _dictionaryOverride = null;
                        if (DictionaryModule != null) DictionaryModule.ReloadDictionary();
                        break;
                    case "input.suffix_connector_narrow_no_break_space":
                        _useNnbspConnector = _config.GetBool(key, false);
                        break;
                    case "appearance.ghk_grouping":
                        _ghkGrouping = _config.GetString(key, "loanword");
                        break;
                    case "appearance.apostrophe_display":
                        _apostropheDisplay = _config.GetString(key, "modifier");
                        break;
                    case "appearance.separator_display":
                        _separatorDisplay = _config.GetString(key, "middledot");
                        break;
                    case "appearance.theme":
                        ApplyConfiguredTheme();
                        break;
                }
            }
        }

        // Suffix connector = ONE logic shared by Shift+Space AND the '-' hyphen key
        // (both target MVS by default; NNBSP only when set in yaml or toggled at runtime from the settings menu).
        // MVS U+180E = default (word/suffix break, plain forms);
        // NNBSP U+202F = suffix-form connector (genitive-i takes the suffix form). '--' is the
        // toggle-independent explicit MVS escape (feminine word-internal tail ᠨ᠎ᠠ).
        private bool _useNnbspConnector;
        public char SuffixConnectorChar => _useNnbspConnector ? (char)0x202F : (char)0x180E;
        public static bool IsSuffixConnectorChar(char c) => c == (char)0x202F || c == (char)0x180E;
        public void ToggleSuffixConnector()
        {
            _useNnbspConnector = !_useNnbspConnector;
            // Two states, so the position is 1/2 or 2/2 — the same "which one am I on"
            // the six-way axes need, and it costs nothing to say it here too.
            EmitSwitchToast(ToastAxisConnectorCodepoint, SettingsSummaryEntries()[ToastAxisConnectorCodepoint].Value,
                            _useNnbspConnector ? 2 : 1, 2, deferred: false);
        }

        // Apostrophe DISPLAY glyph (Preview/Candidate only). Keep the plain
        // ASCII ' internally for typing + parsing convenience, but SHOW a clearer mark. The settings menu cycles the
        // styles, and config `appearance.apostrophe_display` names the start. The settings file documents three:
        //   "modifier"    (default) - ʼ U+02BC MODIFIER LETTER APOSTROPHE (IPA-formal; the preferred one)
        //   "turned"                - ʻ U+02BB MODIFIER LETTER TURNED COMMA
        //   "raw"                   - ' U+0027 plain apostrophe (as typed)
        // Only the displayed text uses this; composed codepoints + tokenizer are untouched.
        // More shapes than these three are offered: apostrophe-like symbols are hard
        // to tell apart, so a wider palette lets the clearest be picked. The settings menu cycles this list; the config value
        // `appearance.apostrophe_display` names the start. All are single glyphs that read as an apostrophe/
        // prime but with visibly different weight/curl/height so the escape mark stands out from a plain '.
        private static readonly (string name, char ch)[] ApostropheShapes = new[]
        {
            ("modifier",  (char)0x02BC),  // ʼ MODIFIER LETTER APOSTROPHE (IPA — default)
            ("turned",    (char)0x02BB),  // ʻ MODIFIER LETTER TURNED COMMA
            ("vertical",  (char)0x02C8),  // ˈ MODIFIER LETTER VERTICAL LINE (IPA primary stress)
            ("prime",     (char)0x02B9),  // ʹ MODIFIER LETTER PRIME
            ("curly",     (char)0x2019),  // ’ RIGHT SINGLE QUOTATION MARK
            ("reversed",  (char)0x02BD),  // ʽ MODIFIER LETTER REVERSED COMMA
            ("saltillo",  (char)0xA78C),  // ꞌ LATIN SMALL LETTER SALTILLO
            ("raw",       (char)0x0027),  // ' plain ASCII apostrophe
        };
        private string _apostropheDisplay = "modifier";
        public char ApostropheDisplayChar
        {
            get
            {
                foreach (var s in ApostropheShapes) if (s.name == _apostropheDisplay) return s.ch;
                return (char)0x02BC;
            }
        }
        public void CycleApostropheDisplay()
        {
            int i = 0;
            for (int k = 0; k < ApostropheShapes.Length; k++) if (ApostropheShapes[k].name == _apostropheDisplay) { i = k; break; }
            int nextIndex = (i + 1) % ApostropheShapes.Length;
            _apostropheDisplay = ApostropheShapes[nextIndex].name;
            EmitSwitchToast(ToastAxisApostropheShape, SettingsSummaryEntries()[ToastAxisApostropheShape].Value,
                            nextIndex + 1, ApostropheShapes.Length, deferred: false);
        }

        // The separator is drawn as its own symbol so the two differ at a glance. The ' key SEPARATOR (ZWNJ) must render as a
        // VISIBLY DIFFERENT glyph from the right-Shift transliteration apostrophe (which stays the IPA ʼ, tight against the letter).
        // Default = middle dot · (round, mid-height) vs the adapter's ʼ (apostrophe-like, high) → different at a glance.
        // Distinct palette so they never overlap in shape.
        private static readonly (string name, char ch)[] SeparatorShapes = new[]
        {
            ("middledot",  (char)0x00B7),  // · MIDDLE DOT (default)
            ("bullet",     (char)0x2022),  // • BULLET
            ("hyphenationpoint", (char)0x2027),  // ‧ HYPHENATION POINT — the first family lacks it, so DirectWrite's
                                                 //   own fallback draws it. It draws; it is merely pale.
            ("ratio",      (char)0x2236),  // ∶ RATIO
            // U+2E31 WORD SEPARATOR MIDDLE DOT is not offered, and not as a matter of taste: none of
            // the three families the Preview actually asks for has a glyph for it. A shape nothing chosen
            // can draw is not an option.
            ("verticalbar",(char)0x00A6),  // ¦ BROKEN BAR
        };
        private string _separatorDisplay = "middledot";
        public string SeparatorDisplayStyle => _separatorDisplay;
        public char SeparatorDisplayChar
        {
            get
            {
                foreach (var s in SeparatorShapes) if (s.name == _separatorDisplay) return s.ch;
                return (char)0x00B7;
            }
        }
        // THE CONNECTOR'S DISPLAY ROW GETS ITS OWN CYCLEABLE SHAPE.
        //
        // The suffix connector has a display row of its own, and its shape must differ from every
        // separator shape. Vertical strokes are the shape family that survives a 90-degree column: they
        // sit in the row and point along it. An arc such as U+2040 CHARACTER TIE, drawn across the top of
        // a vertically stacked column, reads as a join between the wrong pair of rows, so the connector
        // never carries that half-arc, not even as an alternative.
        //
        // FONT COVERAGE IS MEASURABLE, not a matter of taste. Preview text is drawn with
        // GetOrCreateSystemTextFormat, whose family list is Segoe UI → Microsoft YaHei → Arial. A shape
        // the first family lacks is drawn by DirectWrite's font fallback, which is Windows picking some
        // other font on the spot, with a weight and a baseline this project does not control. U+2040
        // and U+23B8 are in the worst position: none of the three families has either at all.
        //
        // ⇒ a shape that no family in the list has is NEVER offered.
        // A register of named exceptions is not carried, because the next shape that nothing can draw
        // would then get in on a one-line reason.
        //
        // THIS IS DISPLAY ONLY. The connector's CODEPOINTS (MVS U+180E / NNBSP U+202F) and its KEYS
        // (Shift+Space, '-', the settings menu's connector-codepoint row, '--' escape) are untouched here. Nothing
        // in this table reaches the committed text.
        //
        // INVARIANT: no shape here may equal any SeparatorShapes entry. The two rows sit in the same
        // column and mean opposite things — one breaks a word, one joins a suffix to it — so a reader who
        // cannot tell them apart cannot tell why a word came out the way it did. The invariant covers
        // EVERY separator shape, not just the current one.
        private static readonly (string name, char ch)[] ConnectorShapes =
        {
            ("verticalline",  (char)0x007C),  // | VERTICAL LINE, the default
            // U+23B8 LEFT VERTICAL BOX LINE is not offered: no family in the Preview's list has a glyph
            // for it.
            ("divides",       (char)0x2223),  // ∣ DIVIDES — the FIRST family lacks it, so DirectWrite's own
                                              //   fallback draws it. It draws; it is merely pale.
            // Shapes Segoe UI itself carries, so the Preview draws them with the same
            // weight and baseline as the text around them.
            ("doubleline",    (char)0x2016),  // ‖ DOUBLE VERTICAL LINE — two strokes read as "two pieces
                                              //   clipped together", and one stroke versus two is a
                                              //   difference that survives a small vertical row
            ("lateralclick",  (char)0x01C1),  // ǁ LATIN LETTER LATERAL CLICK — the same two strokes, but a
                                              //   LETTER: it sits on the Latin baseline and takes the Latin
                                              //   weight, so it sits in a romanized row instead of on it
            // U+2E17 DOUBLE OBLIQUE HYPHEN is not offered, and NOT for font coverage: Segoe UI carries
            // it. Two slanted strokes read as a seam on paper, but at this size, in a narrow vertical row,
            // it renders as a single speck sitting on the baseline and reads as a grain of dirt, not as a
            // join.
        };
        // DIRECTION IS LOAD-BEARING HERE, and it is why an obvious candidate is absent. The Preview is a
        // VERTICAL column: a stroke that runs ALONG the column reads as "not finished yet", and a stroke
        // that cuts ACROSS it reads as "broken here". So `=` and `―`, good symbols in linguistics — Leipzig
        // glossing marks a clitic boundary with `=` — point the wrong way in this column. Not admitted.
        private string _connectorDisplay = "verticalline";
        public string ConnectorDisplayStyle => _connectorDisplay;
        public char ConnectorDisplayChar
        {
            get
            {
                foreach (var s in ConnectorShapes) if (s.name == _connectorDisplay) return s.ch;
                return (char)0x007C;
            }
        }


        public void CycleConnectorDisplay()
        {
            int i = 0;
            for (int k = 0; k < ConnectorShapes.Length; k++) if (ConnectorShapes[k].name == _connectorDisplay) { i = k; break; }
            int nextIndex = (i + 1) % ConnectorShapes.Length;
            _connectorDisplay = ConnectorShapes[nextIndex].name;
            EmitSwitchToast(ToastAxisConnectorShape, SettingsSummaryEntries()[ToastAxisConnectorShape].Value,
                            nextIndex + 1, ConnectorShapes.Length, deferred: false);
        }

        public void CycleSeparatorDisplay()
        {
            int i = 0;
            for (int k = 0; k < SeparatorShapes.Length; k++) if (SeparatorShapes[k].name == _separatorDisplay) { i = k; break; }
            int nextIndex = (i + 1) % SeparatorShapes.Length;
            _separatorDisplay = SeparatorShapes[nextIndex].name;
            // The VALUE is read back through SettingsSources, so the toast and the
            // one-screen summary format this axis identically — no second formatting rule.
            EmitSwitchToast(ToastAxisSeparatorShape, SettingsSummaryEntries()[ToastAxisSeparatorShape].Value,
                            nextIndex + 1, SeparatorShapes.Length, deferred: false);
        }


        /// <summary> Access to the loaded ConfigurationStore for downstream consumers
        /// (incl. the dictionary module, which reads its own knowledge_window.* keys here).</summary>
        public CSharpTSFInput.Configuration.ConfigurationStore GetConfigStore() => _config;

        // Producer: Enqueue input without blocking
        public void EnqueueInput(int keyCode, int modifiers, string snapshot)
        {
            // Only what is edited moves — only positions whose Latin
            // character actually changed in the new snapshot get cleared. Append (snapshot grows) keeps
            // all picks; backspace prunes only out-of-range; mid-edit prunes only the edited position.
            // Pure focus-change events (EnqueueFocusChange) do NOT call this.
            PruneStaleSelections(snapshot);
            var input = new ImeKeyInput(keyCode, modifiers, this, snapshot);
            _inputChannel.Writer.TryWrite(input);
        }

        // Frontend API for the presenter to signal a per-position focus change.
        // Synthesizes an ImeKeyInput with sentinel KeyCode=0 (no-key) but carries the new FocusUnitIndex
        // and the current snapshot. Worker treats it like any other input: re-runs GetCandidatesFromSnapshot
        // with the new focus → re-shapes → emits a fresh RenderFrame. Without this hop, focus state would
        // stay UI-local and candidate column would never refresh on ←/→ navigation.
        // Does NOT clear locked variants — user is mid-selection.
        public void EnqueueFocusChange(string snapshot, int focusUnitIndex)
        {
            var input = new ImeKeyInput(0, 0, this, snapshot, focusUnitIndex);
            _inputChannel.Writer.TryWrite(input);
        }

        // Per-position selected + highlight state, kept apart so "user touched" never counts as
        // "user picked".
        //
        // _selectedPositions: which char positions the user has done EXPLICIT pick on (numeric 0-9).
        //                     ←/→ navigation does NOT mark selected — only navigates highlight.
        // _positionHighlightIndex: per-position currently-highlighted candidate index. Persists across
        //                          outer focus moves so user can return to a position and see prior pick.
        //                          0 = primary by default (when no explicit interaction).
        // _selectedPositionOriginChar: the Latin character at the position when the pick was made.
        //                              Used for content-aware prune (only what is edited moves) —
        //                              on input change, only positions whose Latin char actually changed
        //                              get cleared; others keep their pick.
        //
        // Pruning policy (called from EnqueueInput): for each selected position i,
        //   - if i >= snapshot.Length → drop (position no longer exists)
        //   - if snapshot[i] != _selectedPositionOriginChar[i] → drop (this specific position edited)
        //   - else → keep (position content stable)
        // Append (snapshot grows) keeps all picks; backspace prunes only out-of-range; mid-edit prunes
        // only the edited position; full reset to empty buffer prunes everything (range check covers it).
        private readonly HashSet<int> _selectedPositions = new HashSet<int>();
        private readonly Dictionary<int, int> _positionHighlightIndex = new Dictionary<int, int>();
        private readonly Dictionary<int, char> _selectedPositionOriginChar = new Dictionary<int, char>();

        // Per-position chosen-slice + chosen-glyph captured AT PICK TIME.
        // Recomputing "what did the user pick" after the fact, via alts[hl-1] /
        // localCandidates[hl], makes the WRONG assumption that hl is a linear index into alts.
        // hl is actually the post-collapse UI index, after the same-as-bare collapse drops some FVS
        // variants; the engine never replicates that collapse, so hl misaligns with alts for any
        // input where FVS variants exist at a position, and an FVS variant the user picked is not
        // respected.
        //
        // So the presenter, on pick (ExplicitPickAtFocus), reads CandidateMatrix.Cells[pos][hl]
        // — which holds the EXACT slice and shaped glyph the user saw — and passes both into
        // MarkPositionSelected. Engine stores them verbatim. All later "what did the user pick"
        // queries (ResolveCurrentCodepointAtPosition, ComputeFocusCodepointsForPosition, Mongolian
        // preview column) prefer the stored values; the hl-based reconstruction runs only
        // as a fallback when stored is missing (legacy 2-arg/3-arg MarkPositionSelected callers).
        private readonly Dictionary<int, string> _positionChosenCodepointSlice = new Dictionary<int, string>();
        private readonly Dictionary<int, ManjuShaperCore.FinalGlyph> _positionChosenGlyph = new Dictionary<int, ManjuShaperCore.FinalGlyph>();

        // Primary mark-pick API: presenter calls this with the chosen slice + glyph
        // pulled directly from CandidateMatrix.Cells[pos][hl]. The chosen slice is the codepoint(s)
        // for the focus position only (the matrix cell's CodepointString minus its prefix), and the
        // chosen glyph is the LAST glyph of the cell's shaped sequence (visually corresponds to that
        // position).
        public void MarkPositionSelected(int charPosition, int highlightIndex, char originChar,
                                         string chosenCodepointSlice, ManjuShaperCore.FinalGlyph chosenGlyph)
        {
            if (charPosition < 0 || highlightIndex < 0) return;
            _selectedPositions.Add(charPosition);
            _positionHighlightIndex[charPosition] = highlightIndex;
            _selectedPositionOriginChar[charPosition] = originChar;
            if (!string.IsNullOrEmpty(chosenCodepointSlice))
            {
                _positionChosenCodepointSlice[charPosition] = chosenCodepointSlice;
            }
            _positionChosenGlyph[charPosition] = chosenGlyph;
        }

        // Legacy 3-arg overload — chosen slice/glyph unknown. Falls back to
        // alts[hl-1] reconstruction at read time, which is unsafe in the presence of FVS variants
        // or collapse. Used by callsites that have no chosen slice/glyph.
        public void MarkPositionSelected(int charPosition, int highlightIndex, char originChar)
        {
            if (charPosition < 0 || highlightIndex < 0) return;
            _selectedPositions.Add(charPosition);
            _positionHighlightIndex[charPosition] = highlightIndex;
            _selectedPositionOriginChar[charPosition] = originChar;
        }

        // Backward-compat overload (origin char defaults to '\0' which won't match real input;
        // treat as "always-stale" so this overload behaves like full clear on input change).
        // Prefer the 5-arg overload above. Use this one when content-aware prune is not needed.
        public void MarkPositionSelected(int charPosition, int highlightIndex)
        {
            MarkPositionSelected(charPosition, highlightIndex, '\0');
        }

        // Public read API — presenter / external code asks "did the user pick a specific
        // codepoint slice at position i?" Returns null if no slice was captured (legacy callsite or
        // never picked). Callers fall back to displaying primary in that case.
        public string? GetChosenSliceAt(int charPosition)
        {
            return _positionChosenCodepointSlice.TryGetValue(charPosition, out var slice) ? slice : null;
        }

        // Public read API — presenter asks "what glyph did the user actually pick at
        // position i?" Returns the captured FinalGlyph for the chosen variant. Mongolian preview
        // column uses this in place of shapedFirst[i] for selected positions. Returns null on miss.
        public ManjuShaperCore.FinalGlyph? GetChosenGlyphAt(int charPosition)
        {
            return _positionChosenGlyph.TryGetValue(charPosition, out var glyph) ? glyph : null;
        }

        // Update only the highlight index (←/→ navigation; not yet a pick).
        public void SetPositionHighlight(int charPosition, int highlightIndex)
        {
            if (charPosition < 0 || highlightIndex < 0) return;
            _positionHighlightIndex[charPosition] = highlightIndex;
            // Note: does NOT add to _selectedPositions — ←/→ is navigation, not pick.
        }

        public bool IsPositionSelected(int charPosition)
        {
            return _selectedPositions.Contains(charPosition);
        }

        // Returns 0 (primary) when no explicit highlight set yet.
        public int GetPositionHighlight(int charPosition)
        {
            return _positionHighlightIndex.TryGetValue(charPosition, out int idx) ? idx : 0;
        }

        public IReadOnlyCollection<int> GetSelectedPositionsSnapshot() => _selectedPositions;

        public IReadOnlyDictionary<int, int> GetPositionHighlightSnapshot() => _positionHighlightIndex;

        public void ClearAllSelectionState()
        {
            if (_selectedPositions.Count == 0 && _positionHighlightIndex.Count == 0 && _selectedPositionOriginChar.Count == 0
                && _positionChosenCodepointSlice.Count == 0 && _positionChosenGlyph.Count == 0) return;
            _selectedPositions.Clear();
            _positionHighlightIndex.Clear();
            _selectedPositionOriginChar.Clear();
            // Also clear chosen slice/glyph caches.
            _positionChosenCodepointSlice.Clear();
            _positionChosenGlyph.Clear();
        }

        // Drop only positions whose origin Latin character changed in the
        // new snapshot, OR positions that no longer exist (i >= snapshot.Length). Keep all others.
        // Called from EnqueueInput. Clearing all selection state on every input would be over-aggressive.
        // The rule: only what is edited moves, everything else is kept.
        public void PruneStaleSelections(string snapshot)
        {
            if (_selectedPositions.Count == 0) return;
            // Positions are UNIT indices (the model BuildCommitString /
            // ComputePrefixWithLocksOrSelections use). Validate against the new snapshot's
            // UNITS — not raw chars — so digraph words (ng/zh/tsh…) prune correctly: a position is
            // stale when the unit no longer exists or its origin (first char of the unit) changed.
            var units = ParseSnapshotUnits(snapshot ?? string.Empty);
            var toRemove = new List<int>();
            foreach (int pos in _selectedPositions)
            {
                if (pos < 0 || pos >= units.Count)
                {
                    toRemove.Add(pos);
                    continue;
                }
                // Content-aware: check if the unit's origin char is what was there at pick time.
                if (_selectedPositionOriginChar.TryGetValue(pos, out char origin))
                {
                    if (snapshot![units[pos].snapshotStart] != origin)
                    {
                        toRemove.Add(pos);
                    }
                }
                else
                {
                    // No origin recorded (legacy 2-arg MarkPositionSelected) — treat as stale to be safe.
                    toRemove.Add(pos);
                }
            }
            if (toRemove.Count == 0) return;
            foreach (int pos in toRemove)
            {
                _selectedPositions.Remove(pos);
                _positionHighlightIndex.Remove(pos);
                _selectedPositionOriginChar.Remove(pos);
                // Also prune chosen slice/glyph for the dropped position.
                _positionChosenCodepointSlice.Remove(pos);
                _positionChosenGlyph.Remove(pos);
            }
        }

        // Last focus the WORKER actually resolved a frame with = the position the
        // candidate window's content is currently showing. Written by the worker loop each frame,
        // read by the Backspace-unpick path so the rebuilt candidates STAY on the user's current
        // focus instead of jumping to the un-picked position: when the focus bar holds, the candidate
        // window's content must hold the same element too; an un-picked selection clears state only, it does not take the display context.
        private volatile int _lastWorkerFocusResolved = -1;
        public int LastWorkerFocusResolved
        {
            get => _lastWorkerFocusResolved;
            set => _lastWorkerFocusResolved = value;
        }

        // THE RACE: _lastWorkerFocusResolved is written by the WORKER after it finishes a frame, but the
        // user moves focus SYNCHRONOUSLY on the UI thread (MoveFocus / JumpFocus / auto-advance). Pressing
        // a separator, punctuation or Shift immediately after an arrow key therefore reads a focus value
        // that is one or more frames behind, and the mark lands on the wrong unit. The bounded channel is
        // DropOldest, which is safe for the messages themselves (each carries a full snapshot, so the
        // newest supersedes) but does nothing for this read-side staleness.
        //
        // So the presenter publishes the focus it just moved to, synchronously, before any key can
        // be processed. Insertion reads that authoritative value and only falls back to the worker's
        // resolved focus when the UI has not established one (-1 = no focus / sentinel).
        private volatile int _uiFocusAuthoritative = -1;

        /// <summary> The focus the USER is on right now, published synchronously by the
        /// presenter on every focus change. -1 = none established (fall back to the worker's value).</summary>
        public int UiFocusAuthoritative
        {
            get => _uiFocusAuthoritative;
            set => _uiFocusAuthoritative = value;
        }

        /// <summary> The focus every position-targeting insertion must use: the user's
        /// synchronous focus when one exists, otherwise the worker's last resolved focus.</summary>
        private int EffectiveFocus()
        {
            int ui = _uiFocusAuthoritative;
            return ui >= 0 ? ui : _lastWorkerFocusResolved;
        }

        // Last snapshot the worker built a frame for. Used to classify a sentinel-focus
        // key input as CONTINUATION (append/tail-erase → keep last focus, kills the per-key 0/k frame
        // alternation = fast-typing flicker) vs mid-edit/fresh (→ focus 0, matching presenter ResetFocus).
        // Worker-thread single-writer.
        internal string _lastWorkerSnapshot = string.Empty;

        // Two-phase Backspace support: pop the LAST explicit pick — the highest-index
        // (back→front by sequence position) — removing it from ALL per-position maps. Returns the popped
        // position, or -1 if no picks remain. Does NOT touch the raw input buffer; the Backspace handler
        // deletes a raw char only after this returns -1 (the rule: undo every selection before
        // deleting original input).
        public int UnpickHighestSelectedPosition()
        {
            if (_selectedPositions.Count == 0) return -1;
            int pos = -1;
            foreach (int p in _selectedPositions) if (p > pos) pos = p;
            _selectedPositions.Remove(pos);
            _positionHighlightIndex.Remove(pos);
            _selectedPositionOriginChar.Remove(pos);
            _positionChosenCodepointSlice.Remove(pos);
            _positionChosenGlyph.Remove(pos);
            return pos;
        }

        // Strip INTERNAL input-layer markers from an as-typed (Enter)
        // raw-Latin emission so they NEVER reach the host. An adapter unit like "k'" spans the U+0027
        // marker, so emitting the raw substring on the as-typed path would leak the internal marker as a
        // literal.
        // Gap separators are already safe (skipped by ParseSnapshotUnits, not in any unit span); this covers
        // the marker that sits INSIDE a token's span. ZWNJ stripped defensively too.
        private static string StripInputMarkers(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            if (s.IndexOf('\'') < 0 && s.IndexOf(SeparatorMarker) < 0) return s;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (c != '\'' && c != SeparatorMarker) sb.Append(c);
            return sb.ToString();
        }

        // Build the commit string for upstream (TSF SetText / EndComposition).
        // As in other IMEs: selected positions render as the chosen
        // Mongolian codepoint sequence; UNSELECTED positions commit as the raw Latin character.
        // Result is a mixed Mongolian + Latin string the host application receives directly.
        //
        // Algorithm: parse units (mirroring GetTranslationBasesFromSnapshot's parsing), iterate each
        // unit:
        //   - if _selectedPositions contains unit index → use the highlighted candidate's codepoint(s)
        //     for that unit (compute via prefix-with-locks logic)
        //   - else → append the original Latin substring for that unit
        //
        // This is called at TSF commit time from KeyHandlerEditSession.
        public string BuildCommitString(string snapshot, bool unselectedAsPrimary = false)
        {
            if (string.IsNullOrEmpty(snapshot)) return string.Empty;

            // Use shared O(n) parser and iterative prefix accumulation. Walks units
            // 0..N-1 once; for each selected position computes focus codepoints from the current
            // accumulated prefix (no recursion into GetCandidatesFromSnapshot).
            var units = ParseSnapshotUnits(snapshot);
            if (units.Count == 0) return string.Empty;

            // The connector is skipped by ParseSnapshotUnits (it is not a position), so the
            // unit walk below cannot emit it. Collect it from the gaps instead: everything the parser
            // steps over between one unit's end and the next unit's start. Only the connector is re-emitted:
            // ZWNJ and the apostrophe are invisible in the commit.
            string GapConnectors(int from, int to)
            {
                var sb = new StringBuilder();
                for (int gi = from; gi < to && gi < snapshot.Length; gi++)
                    if (IsSuffixConnectorChar(snapshot[gi])) sb.Append(snapshot[gi]);
                return sb.ToString();
            }

            var output = new StringBuilder();
            // Accumulated prefix of already-committed Mongolian/Latin output. For unselected
            // positions this stays in Latin form (raw); for selected positions we feed only the
            // pure-Mongolian-prefix view into ComputeFocusCodepointsForPosition. Both views are
            // built in the same loop.
            var mongolianPrefix = new StringBuilder();
            for (int i = 0; i < units.Count; i++)
            {
                var unit = units[i];
                string emitted;     // host-facing output for this unit
                string prefixPart;  // pure-Mongolian contribution to the shaping prefix (no joiners)
                if (_selectedPositions.Contains(i))
                {
                    int hl = GetPositionHighlight(i);
                    string? focusCodepoints = ComputeFocusCodepointsForPosition(
                        units, i, mongolianPrefix.ToString(), hl, snapshot);
                    emitted = focusCodepoints ?? unit.primary; // fallback to primary on miss
                    prefixPart = emitted;
                }
                else
                {
                    // Unselected-position output depends on the call path:
                    //   COMMIT (unselectedAsPrimary=true): emit the PRIMARY (default Mongolian) so
                    //     space/enter with no explicit pick commits the whole default set (the first candidate), like a
                    //     mature IME defaulting to the top candidate,
                    //     NOT the raw typed Latin.
                    //   LIVE / preview (default false): keep the raw Latin echo so the
                    //     inline composition still shows what the user typed as they type.
                    // mongolianPrefix always uses primary so a LATER selected position builds on the
                    // correct pure-Mongolian prefix either way.
                    emitted = unselectedAsPrimary
                        ? unit.primary
                        : StripInputMarkers(snapshot.Substring(unit.snapshotStart, unit.snapshotLen));
                    prefixPart = unit.primary;
                }
                // Commit CONTIGUOUS codepoints, NO inter-letter joiner.
                // Inserting U+200D ZWJ between adjacent Mongolian units to "hold" the cursive
                // joining on commit is unnecessary and harmful: the host splits script runs, so a contiguous
                // Mongolian run already joins correctly even with a trailing raw-Latin unit, AND the
                // ZWJ actively CORRUPTS the host's medial forms and emits a stray glyph. Committing
                // contiguous reproduces the candidate window's forms EXACTLY.
                output.Append(emitted);
                mongolianPrefix.Append(prefixPart);

                // Re-emit the connector that lives in the gap AFTER this unit. It goes into
                // the shaping prefix too: MVS is what makes the preceding letter take its final form, so
                // a later position computing its candidates from the prefix must see it. This reproduces
                // exactly what the connector would contribute as a unit of its own.
                int cgFrom = unit.snapshotStart + unit.snapshotLen;
                int cgTo = (i + 1 < units.Count) ? units[i + 1].snapshotStart : snapshot.Length;
                string conn = GapConnectors(cgFrom, cgTo);
                if (i == 0 && unit.snapshotStart > 0) conn = GapConnectors(0, unit.snapshotStart) + conn;
                if (conn.Length > 0)
                {
                    output.Append(conn); mongolianPrefix.Append(conn);
                }
            }
            // The positional-form switch is applied HERE, at the one place the host-facing
            // string leaves this method, so the forcing cannot be half-applied along some other path.
            // It is a no-op unless positional forcing has been turned on.
            return ApplyPositionalForcing(output.ToString());
        }

        // Compute prefix iteratively in O(n): a single forward pass, accumulating prefix as we go.
        // Each position's chosen codepoint is computed from the CURRENT prefix + that position's local
        // candidate list, with no recursion. A recursive form (GetCandidatesFromSnapshot calls
        // GetTranslationBasesFromSnapshot, which calls this method again) would be T(n)=2*T(n-1) → O(2^n)
        // in the number of selected positions.
        // Sentinel resolution is shared with the presenter: focusUnitIndex < 0 OR >= units.Count
        // resolves through ResolveFocusUnitSentinel. THAT MEANS THE LAST POSITION, not
        // the first. What matters is that both sides pick the SAME end: a band on row 1 while the
        // candidate window shows last-character variants comes from the two sides picking DIFFERENT
        // ends. See the pairing warning on ResolveFocusUnitSentinel.
        private string ComputePrefixWithLocksOrSelections(string snapshot, int focusUnitIndex)
        {
            if (string.IsNullOrEmpty(snapshot)) return string.Empty;
            var units = ParseSnapshotUnits(snapshot);
            if (units.Count == 0) return string.Empty;
            int focus = ResolveFocusUnitSentinel(focusUnitIndex, units.Count);
            var prefix = new StringBuilder();
            for (int i = 0; i < focus; i++)
            {
                if (_selectedPositions.Contains(i))
                {
                    int hl = GetPositionHighlight(i);
                    // Build candidates at position i WITHOUT recursion: use the prefix
                    // we have built so far (positions 0..i-1) as the literal prefix; no inner call
                    // back into ComputePrefixWithLocksOrSelections or GetCandidatesFromSnapshot.
                    string? focusCodepoints = ComputeFocusCodepointsForPosition(units, i, prefix.ToString(), hl, snapshot);
                    if (focusCodepoints != null)
                    {
                        prefix.Append(focusCodepoints);
                        continue;
                    }
                }
                prefix.Append(units[i].primary);
            }
            return prefix.ToString();
        }

        // Sentinel resolution for engine-side focus-unit-index. Mirrors
        // CandidateListUIPresenter.ResolveFocusIndex.
        //
        // BOTH RESOLVE TO THE TAIL: the default selection focus is the item just typed.
        //
        // THE PAIRING IS THE POINT, not the value. The failure mode is presenter at 0 and
        // engine at the tail, giving a candidate window that shows last-character variants while the band
        // is on the first row. A change that moves one side must move BOTH.
        private static int ResolveFocusUnitSentinel(int focusUnitIndex, int unitCount)
        {
            if (unitCount <= 0) return 0;
            if (focusUnitIndex < 0 || focusUnitIndex >= unitCount) return unitCount - 1;
            return focusUnitIndex;
        }

        // Double-apostrophe escape detector. A letter at idx immediately followed by TWO
        // apostrophes ("X''") is a forced syllable separation that OVERRIDES the apostrophe-token X' — e.g.
        // r''h -> r + h (so arhat = ar''hat), while r'/rh keep meaning ᡰ. Shared by both tokenizers below.
        private static bool IsDoubleApostropheEscape(string s, int idx)
            => idx + 2 < s.Length && s[idx] != '\'' && s[idx + 1] == '\'' && s[idx + 2] == '\'';

        // Apostrophe roles use TWO distinct internal codepoints so
        // the ' key never accidentally forms an adapter (a k' is hidden in the underlying layers):
        //   · `'` KEY, 1st tap  = SEPARATOR — appends ZWNJ U+200C (zero-width NON-JOINER; standard, non-PUA,
        //                         semantics = "don't join"). Breaks the greedy span (n'g→n+g) and disappears,
        //                         but NEVER completes an adapter token ("k"+ZWNJ ≠ the scheme key "k'").
        //   · `'` KEY, 2nd tap  = FORCE-COMMIT (like Enter) + a trailing literal ' — key-layer, so the tokenizer
        //                         never sees "''" while typing.
        //   · RIGHT SHIFT       = specialize/adapter/escape — appends U+0027 (k'→ᠺ, double→r''h escape via the
        //                         existing tokenizer). This is the "underlying '" that the scheme's k'/g'/h'/r'
        //                         keys match. refined.yaml needs no entry for the ZWNJ separator.
        // Tokenizer: U+0027 keeps its full behavior (adapter / separator / double-escape); ZWNJ is a pure
        // separator (skip, break greedy, no adapter). Both fold into the display as the apostrophe glyph.
        public const char SeparatorMarker = (char)0x200C;   // ZWNJ zero-width non-joiner = the ' key's separator

        // Right-Shift escape action: if the buffer ends in an escapable letter, append the U+0027
        // escape marker so the tokenizer specializes it (k→ᠺ, r→ža, or breaks a digraph). Returns true if
        // it inserted (so the caller refreshes). No-op when there is no escapable previous letter:
        // if the previous letter has no such capability, the right Shift does nothing.
        public bool ApplyShiftEscape()
        {
            if (Builder_Composing.Length == 0) return false;

            // Two rules:
            //   (1) the transliteration mark follows the focus: target the FOCUSED unit, not the tail.
            //   (2) it is checked for validity in context: validate at TOKEN level, not raw-character level, so
            //       the `h` inside CH / ZH / SH is NOT mistaken for the standalone adapter-capable `h`.
            // Both fall out of reading the focused UNIT's key and checking `<key>'` against the live scheme.
            //
            // Why token level is automatically correct for the digraph case: the tokenizer already groups
            // "sh" as ONE unit whose key is "sh". The scheme has no `sh'` entry, so it is rejected — a digraph
            // can never inherit its tail letter's adapter. Conversely a standalone `h` unit has key "h" and
            // `h'` IS a scheme key, so it is accepted. No hard-coded G/H/K/R whitelist is needed: the scheme
            // file stays the single source of truth (transliteration-free invariant).
            //
            // Stacking is likewise handled for free: an already-specialized unit has key "k'", and `k''`
            // is not a scheme key, so a second tap is a no-op instead of producing a dangling marker.
            string focusKey = GetFocusUnitKey();
            if (string.IsNullOrEmpty(focusKey)) return false;
            string lk = focusKey.ToLowerInvariant();

            // The apostrophe also completes letters that begin at an earlier unit, as in Abkai `cy'`,
            // whose c and y stay two units until the apostrophe joins them; and it goes in where a longer key
            // needs it before its last letters are typed, as in Möllendorff `c'y`. ApostropheStemAtFocus
            // finds those letters.
            string? stem = ApostropheStemAtFocus();
            if (stem == null)
            {
                // With no such letters, a letter the scheme also spells with a diacritic takes that
                // spelling, as `u`, `s` and `z` take `ū`, `š` and `ž` in Möllendorff and Norman. That also
                // returns true.
                string? marked = MarkedSpellingOf(lk);
                if (marked != null)
                {
                    ReplaceFocusUnit(marked);
                    return true;
                }
                return false;
            }

            InsertAtFocus("'");
            return true;
        }

        // The key of the scheme that is this letter with diacritics, found by canonical
        // decomposition (NFD): `ū` for `u` in Möllendorff. Null when the scheme has none, and when it has more
        // than one, since one tap cannot choose between them.
        private string? MarkedSpellingOf(string letter)
        {
            if (letter.Length != 1 || !char.IsLetter(letter[0])) return null;
            string? found = null;
            foreach (string key in Dictionary_Mappings.Keys)
            {
                string d = CanonicalDecomposition(key);
                if (d.Length < 2 || d[0] != letter[0]) continue;
                bool marksOnly = true;
                for (int i = 1; i < d.Length; i++)
                    if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(d[i]) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    {
                        marksOnly = false;
                        break;
                    }
                if (!marksOnly) continue;
                if (found != null) return null;
                found = key;
            }
            return found;
        }

        // The canonical decomposition (NFD) of a scheme key, by Windows' NormalizeString. Not
        // string.Normalize: the DLL is built with InvariantGlobalization, where that returns every non-ASCII
        // string as it is, so no key with a diacritic would show its base letter. An ASCII key is its own
        // decomposition. A key Windows cannot decompose is kept as it is, so it matches no base letter.
        internal static string CanonicalDecomposition(string key)
        {
            bool ascii = true;
            foreach (char ch in key)
                if (ch > 0x7F) { ascii = false; break; }
            if (ascii) return key;
            unsafe
            {
                fixed (char* source = key)
                {
                    int size = NativeMethods.NormalizeString(NativeMethods.NormalizationD, source, key.Length, null, 0);
                    for (int attempt = 0; attempt < 4 && size > 0; attempt++)
                    {
                        var buffer = new char[size];
                        fixed (char* destination = buffer)
                        {
                            int written = NativeMethods.NormalizeString(NativeMethods.NormalizationD, source, key.Length, destination, buffer.Length);
                            if (written > 0) return new string(buffer, 0, written);
                            if (System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != NativeMethods.ERROR_INSUFFICIENT_BUFFER) break;
                            size = -written;
                        }
                    }
                }
            }
            return key;
        }

        // The focused unit's letters give way to text; the focus stays on the unit.
        private void ReplaceFocusUnit(string text)
        {
            var units = ParseSnapshotUnits(Builder_Composing.ToString());
            if (units.Count == 0) return;
            int focus = EffectiveFocus();
            if (focus < 0 || focus >= units.Count) focus = units.Count - 1;
            Builder_Composing.Remove(units[focus].snapshotStart, units[focus].snapshotLen);
            Builder_Composing.Insert(units[focus].snapshotStart, text);
        }

        // The letters an apostrophe at the focus would complete, with the apostrophe: the text from the
        // start of the focused unit, or of a unit before it, to the focus, when that text and an apostrophe are
        // a key of the scheme or the start of one. The longest comes first. The units must follow one another
        // with nothing between them, since a separator, an apostrophe or a connector between two units keeps
        // them apart. Starting at a unit keeps the rule at token level: the h of sh is never taken alone.
        // Null when there are no such letters. The scheme file is the only list.
        private string? ApostropheStemAtFocus()
        {
            string raw = Builder_Composing.ToString();
            var units = ParseSnapshotUnits(raw);
            if (units.Count == 0) return null;
            int focus = EffectiveFocus();
            if (focus < 0 || focus >= units.Count) focus = units.Count - 1;
            int end = units[focus].snapshotStart + units[focus].snapshotLen;
            // An apostrophe already right after the focus, put there by an earlier tap for a longer key
            // (c' before the y of c'y): a second tap adds none, so taps do not stack.
            if (end < raw.Length && raw[end] == '\'') return null;
            int first = focus;
            while (first > 0 && units[first - 1].snapshotStart + units[first - 1].snapshotLen == units[first].snapshotStart)
                first--;
            for (int u = first; u <= focus; u++)
            {
                string stem = raw.Substring(units[u].snapshotStart, end - units[u].snapshotStart).ToLowerInvariant() + "'";
                foreach (string key in Dictionary_Mappings.Keys)
                    if (key.StartsWith(stem, StringComparison.Ordinal)) return stem;
            }
            return null;
        }

        // Parse snapshot into units (digraph-aware). Single-pass O(n) parser
        // shared by all prefix/commit-string code paths.
        private List<(string key, string primary, int snapshotStart, int snapshotLen)> ParseSnapshotUnits(string snapshot)
        {
            var units = new List<(string key, string primary, int snapshotStart, int snapshotLen)>();
            int idx = 0;
            while (idx < snapshot.Length)
            {
                // The ' KEY's SEPARATOR (ZWNJ U+200C): break the greedy span, no glyph, disappear.
                // Can NEVER form an adapter ("k"+ZWNJ isn't a scheme key) → the ' key is a pure separator.
                if (snapshot[idx] == SeparatorMarker) { idx++; continue; }
                // U+0027 (from RIGHT SHIFT): breaks the greedy span when standalone, OR completes an
                // adapter token (k'→ᠺ) via the greedy match below. As in Rime, a 2nd consecutive ' key press is
                // intercepted at the KEY layer (force-commit), so the tokenizer never sees ' key "''" live; the
                // double-escape r''h comes from right Shift's U+0027.
                if (snapshot[idx] == '\'') { idx++; continue; }
                // THE SUFFIX CONNECTOR (MVS U+180E / NNBSP U+202F) IS NOT A UNIT EITHER.
                // It follows the separator model: it occupies a display row
                // of its own, it is not a position, and the arrow keys cannot land on it. Falling
                // through to the greedy match makes it a unit, so it takes a focus stop, shifts
                // every index behind it, and shows up in the grid as the literal label "<MVS>".
                // Skipping it here removes it from BuildCommitString's iteration, so BuildCommitString
                // re-emits it from the GAP between units. It is a real character the host must receive
                // (it selects the preceding letter's final form); dropping it would be a silent data loss,
                // which is the one thing this skip is conditional on.
                if (IsSuffixConnectorChar(snapshot[idx])) { idx++; continue; }
                string key; int advance;
                // Double-apostrophe escape: letter + '' -> take letter alone (the '' skip as
                // separators next), overriding the apostrophe-token (r'/k'/g'/h'). So ᡰ stays typeable as rh/r'
                // while arhat is typed ar''hat. Checked BEFORE the greedy match so "r'" can't grab the '.
                if (IsDoubleApostropheEscape(snapshot, idx))
                { key = snapshot[idx].ToString(); advance = 1; }
                // Greedy longest-match: 4-char ("dzhy") -> 3-char ("tsy") -> 2-char ("ng","k'") -> 1-char.
                else if (idx + 3 < snapshot.Length && Dictionary_Mappings.ContainsKey(snapshot.Substring(idx, 4)))
                { key = snapshot.Substring(idx, 4); advance = 4; }
                else if (idx + 2 < snapshot.Length && Dictionary_Mappings.ContainsKey(snapshot.Substring(idx, 3)))
                { key = snapshot.Substring(idx, 3); advance = 3; }
                else if (idx + 1 < snapshot.Length && Dictionary_Mappings.ContainsKey(snapshot.Substring(idx, 2)))
                { key = snapshot.Substring(idx, 2); advance = 2; }
                else
                { key = snapshot[idx].ToString(); advance = 1; }
                string primary = Dictionary_Mappings.ContainsKey(key) ? Dictionary_Mappings[key] : key;
                units.Add((key, primary, idx, advance));
                idx += advance;
            }
            return units;
        }

        // Compute the focus codepoint(s) at position i given an already-built
        // prefix (positions 0..i-1) and a chosen highlight index. Returns the focus-codepoint(s) only
        // (NOT including the prefix). Returns null on miss (caller falls back to primary).
        //
        // Mirrors GetCandidatesFromSnapshot's candidate-construction order EXCEPT it operates on a
        // pre-built prefix and is non-recursive. The candidate list shape at position i is:
        //   index 0..D-1: dictionary matches (whole-snapshot search, prefix-agnostic)
        //   index D..D+B-1: bases = [prefix + primary, prefix + alt_1, prefix + alt_2, ...]
        //   then per-base FVS variants interleaved after their base (per GetCandidatesFromSnapshot loop)
        //
        // same-as-bare collapse happens in CompositionProcessorEngine AFTER this layer, so the
        // hl index user sees is the post-collapse index. To map back, we walk the pre-collapse list
        // in the same order. If post-collapse hl > pre-collapse list count, return null (caller
        // falls back to primary).
        //
        // NOTE: this method does NOT attempt to replicate collapse; the recorded hl was
        // captured against the post-collapse candidate list, so for hl > base+FVS count we accept the
        // miss. Full post-collapse index reconciliation is not done here.
        private string? ComputeFocusCodepointsForPosition(
            List<(string key, string primary, int snapshotStart, int snapshotLen)> units,
            int i,
            string prefix,
            int hl,
            string snapshot)
        {
            if (i < 0 || i >= units.Count) return null;
            if (hl < 0) return null;

            // If a chosen slice was captured at pick time (5-arg MarkPositionSelected),
            // use it directly. This avoids the hl-as-linear-index reconstruction below, which
            // misfires under FVS variants and collapse.
            if (_positionChosenCodepointSlice.TryGetValue(i, out var storedSlice) && !string.IsNullOrEmpty(storedSlice))
            {
                return storedSlice;
            }

            // Build candidate list at position i with the given prefix, in the same order as
            // GetCandidatesFromSnapshot would produce. We index into it by hl. Position-specific
            // candidates only (no dictMatches here because dictMatches doesn't have a prefix-shape).
            var localCandidates = new List<string>();
            // Dict matches first (must mirror GetCandidatesFromSnapshot which prepends them).
            // But dict matches are NOT prefix-shaped — they're full-snapshot results. So the
            // user's hl may point to a dict match. Replicate that ordering.
            List<string> dictMatches = LegacyDictSearchEmptyShim(snapshot);
            foreach (var dm in dictMatches) localCandidates.Add(dm);

            string focusKey = units[i].key;
            string focusPrimary = units[i].primary;

            // Bases at position i with the provided prefix (NO recursion).
            var basesLocal = new List<string>();
            string basePrimary = prefix + focusPrimary;
            basesLocal.Add(basePrimary);
            if (Dictionary_Alternatives.TryGetValue(focusKey, out var alts))
            {
                foreach (var alt in alts)
                {
                    string combo = prefix + alt;
                    if (!basesLocal.Contains(combo)) basesLocal.Add(combo);
                }
            }
            foreach (string baseStr in basesLocal)
            {
                if (!localCandidates.Contains(baseStr)) localCandidates.Add(baseStr);
                Char lastChar = baseStr.Last();
                if (lastChar >= 'ᠠ' && lastChar <= '᢯')
                {
                    String[] fvs = { "᠋", "᠌", "᠍" };
                    foreach (var v in fvs)
                    {
                        string combo = baseStr + v;
                        if (!localCandidates.Contains(combo)) localCandidates.Add(combo);
                    }
                }
            }

            if (hl >= localCandidates.Count) return null;
            string chosen = localCandidates[hl];
            if (!chosen.StartsWith(prefix)) return null;
            return chosen.Substring(prefix.Length);
        }

        // Consumer: Static Worker Loop
        /// <summary> Where a delivery came. This is passed EXPLICITLY by the
        /// caller; nothing is inferred from the current thread.
        ///
        /// A <c>[ThreadStatic]</c> bool set once at the top of
        /// <see cref="ProcessInputLoop"/> would not work: the loop
        /// is <c>async</c>, so every continuation after an <c>await</c> may resume on a different pool
        /// thread whose slot is false — the fail-closed check would be skipped and the worker would enter
        /// subscribers inline — while the original thread keeps a stale true. An enum on the call is not
        /// inference and cannot drift across an await.</summary>
        internal enum DeliveryOrigin
        {
            /// <summary>Published by the shared background worker. Its continuation may be on any pool
            /// thread, and a Clear() from the TSF thread can interleave — so it must never enter a
            /// subscriber without being marshalled first.</summary>
            Worker,
            /// <summary>Published synchronously by a direct caller doing the work on its own thread.
            /// No cross-thread cancellation window exists.</summary>
            Caller,
        }

        private static async Task ProcessInputLoop(CancellationToken ct)
        {
            try
            {
                // Reusable lists (per worker thread)
                List<List<ManjuShaperCore.FinalGlyph>> shapedResults = new();
                List<float> lengths = new();

                await foreach (var input in _inputChannel.Reader.ReadAllAsync(ct))
                {
                    {

                    if (!input.SourceEngine.TryGetTarget(out var engine))
                    {
                        continue;
                    }

                    // Drop inputs of a FINISHED composition before they can do
                    // anything observable. Without this, an old-word input still in the channel (or
                    // being computed) writes engine.LastWorkerFocusResolved/_lastWorkerSnapshot back
                    // AFTER Clear() has reset them (so the next word's InsertAtFocus lands at the dead
                    // word's focus), and would also publish a ghost frame of the dead word.
                    if (input.Epoch != engine.CurrentCompositionEpoch)
                    {
                        continue;
                    }

                    // 1. Process Logic (Update Buffer)
                    // (Buffer updated synchronously in AddInput on UI thread)
                    
                    // 2. Get Candidates (From Snapshot) — matrix path
                    // Worker consumes payload snapshot only.
                    // Focus channel passes input.FocusUnitIndex.
                    // Build per-position candidate matrix once (HarfBuzz shapes full
                    // input N×M times in BuildCandidateMatrix). Worker then takes the focus-row
                    // candidates + their PRE-SHAPED glyphs directly from the matrix, with no
                    // per-candidate FullShape call. This is what gives candidate cards the correct
                    // initial/medial/final joining form per position.
                    string snapshotNow = input.CompositionSnapshot ?? string.Empty;
                    CandidateMatrix matrix = engine.BuildCandidateMatrix(snapshotNow);
                    int focusResolved;
                    if (input.FocusUnitIndex >= 0 && input.FocusUnitIndex < matrix.RowCount)
                    {
                        focusResolved = input.FocusUnitIndex;               // explicit focus-change
                    }
                    else
                    {
                        // Sentinel focus (plain key input). A SENTINEL INPUT IS THE USER TYPING, SO THE
                        // ACTIVE ITEM IS THE ONE THEY JUST TYPED: the tail, not the first position and not
                        // the position the previous frame happened to resolve to.
                        //
                        // This is the same dual-state rule the Preview grid uses
                        // (CandidateListUIPresenter :6462, scrollTarget = focus >= 0 ? focusSlot :
                        // slotCount - 1), and this side is read off that one. Selection focus is
                        // untouched: an EXPLICIT focus change still takes the branch above, so arrow keys
                        // and clicks decide where selection is, and this decides only what the candidate
                        // column is built for.
                        //
                        // WHY IT IS HERE AND NOT IN THE PRESENTER. The presenter can already ask for a
                        // rebuild at the tail (it does, on every append), but the keystroke's OWN frame is
                        // built with the value below, so resolving it in the presenter always costs a SECOND
                        // frame for the same raw state, which paints one state twice. Deferring that
                        // paint does not help: the duplicate is a real second frame, not a
                        // second invalidation. Resolving it here means the keystroke's own frame already
                        // carries the right focus, the presenter's request becomes a no-op the layout
                        // check drops, and one state is drawn once.
                        //
                        // A sentinel resolved to 0 while the user's focus is at k>0 would publish TWO
                        // different frames for the SAME keystroke: frame A (input, focus 0), then frame B
                        // (presenter's EnqueueFocusChange, focus k). That 0/k content alternation shows as
                        // candidate flicker during fast typing. Both frames resolve to the tail, so they are
                        // identical and the second is suppressed.
                        // ONE KEYSTROKE, ONE FRAME.
                        //
                        // The tail is the right default, but it is only a DEFAULT. When the user has
                        // established a selection focus, that focus is the authority, and the presenter
                        // knows it: it publishes UiFocusAuthoritative synchronously on every focus
                        // change, precisely so a later reader does not have to wait a frame for it.
                        //
                        // Without this read, a keystroke produces TWO frames: this one built for the tail,
                        // then a second one after the presenter notices the mismatch and asks for a rebuild
                        // at the user's focus. The user sees the candidate column flick to the newest item
                        // and snap back, which is one operation showing two contradictory pictures. The
                        // second frame is not needed: the authority was already published before the key
                        // was processed, so the first frame can simply be right.
                        //
                        // This does NOT decide which element the default candidate follows: the default with no selection
                        // focus is the tail. It only stops the two rules from
                        // taking turns WITHIN one keystroke.
                        int uiFocus = engine.UiFocusAuthoritative;
                        int tail = matrix.RowCount - 1;
                        focusResolved = (uiFocus >= 0 && uiFocus < matrix.RowCount)
                            ? uiFocus
                            : (tail >= 0 ? tail : 0);  // empty matrix → first position
                    }
                    // LINEARIZED write-back: the epoch check and the side effects
                    // (focus/snapshot write-back + dictionary update) are ONE atomic block under
                    // _epochGate, closing the "pass the check → Clear interleaves → stale effects"
                    // window.
                    bool staleAtWriteback = false;
                    lock (engine._epochGate)
                    {
                        if (input.Epoch != engine.CurrentCompositionEpoch)
                        {
                            staleAtWriteback = true;
                        }
                        else
                        {
                            // Remember the focus this frame is built for = what the candidate
                            // window will show (Backspace-unpick keeps content on the same element).
                            engine.LastWorkerFocusResolved = focusResolved;
                            engine._lastWorkerSnapshot = snapshotNow;   // continuation baseline
                        }
                    }
                    // The dictionary callback is a REPLACEABLE MODULE — foreign code.
                    // Keeping it inside _epochGate with a try/catch is not enough, because try/catch only
                    // catches exceptions; it cannot break a mutual wait. A module callback that blocks on
                    // another thread which is calling Clear() (and therefore waiting for this very lock)
                    // freezes the single static worker. It runs OUTSIDE the
                    // lock, checked before and compensated after:
                    //   * re-check the epoch before calling, so a finished composition does not repopulate;
                    //   * re-check after, and if Clear() landed DURING the callback, drive the module's
                    //     own composition-end so its window cannot keep the resurrected entries.
                    if (!staleAtWriteback && input.Epoch == engine.CurrentCompositionEpoch)
                    {
                        try { engine.DictionaryModule?.OnSnapshotChanged(input.CompositionSnapshot ?? string.Empty); }
                        catch (Exception) { }

                        if (input.Epoch != engine.CurrentCompositionEpoch)
                        {
                            // Use the WORKER-thread compensation entry point. Calling
                            // OnCompositionEnd() from here violates its documented UI-thread contract
                            // (this code runs on the worker thread), and a legitimate
                            // replacement module is allowed to do HWND work in OnCompositionEnd.
                            try { engine.DictionaryModule?.RequestCompositionEndFromWorker(); }
                            catch (Exception) { }
                        }
                    }
                    if (staleAtWriteback)
                    {
                        continue;
                    }

                    List<string> candidates = matrix.GetCandidateStringsAt(focusResolved);
                    List<SemanticCandidateItem> semanticItems = matrix.GetSemanticItemsAt(focusResolved);
                    List<List<ManjuShaperCore.FinalGlyph>> matrixShapedAtFocus = matrix.GetShapedGlyphsAt(focusResolved);
                    // Per-cell position-slice list — what each
                    // candidate contributes AT THE FOCUS POSITION specifically. Shipped through
                    // the frame so presenter never reverse-engineers slice from candidate string.
                    List<string> slicesAtFocus = matrix.GetSlicesAt(focusResolved);

                    // 3. Perform Heavy Shaping (CPU Bound)
                    var fontData = engine.GetFontData();
                    if (candidates.Count > 0 && fontData != null)
                    {
                        shapedResults.Clear();
                        lengths.Clear();

                        // Backend must use the same font size as frontend rendering.
                        // Lengths computed at any other size would not match what the candidate window draws.
                        float fontSize = 32.0f;  // Must match CandidateListUIPresenter.FontSize
                        ushort upem = 1000;

                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        // Matrix produced pre-shaped glyphs in full-input context. Skip
                        // per-candidate FullShape and consume the matrix's shaped cells directly.
                        // This is also how the cards display correct initial/medial/final form:
                        // the glyphs in matrix[focus] came from shaping the FULL hypothetical input,
                        // not a prefix-substring.
                        unsafe
                        {
                            fixed (byte* pFont = fontData)
                            {
                                var upemReader = new ManjuFontReader(pFont, fontData.Length);
                                upem = upemReader.GetUnitsPerEm();
                            }
                        }
                        float scaleForLen = fontSize / (float)upem;
                        for (int i = 0; i < candidates.Count; i++)
                        {
                            var shaped = (i < matrixShapedAtFocus.Count) ? matrixShapedAtFocus[i] : new List<ManjuShaperCore.FinalGlyph>();
                            shapedResults.Add(shaped);
                            float len = 0;
                            foreach (var g in shaped) len += CardColumnDesignUnits(g) * scaleForLen;
                            lengths.Add(len);
                        }
                        sw.Stop();

                        // After shaping completes, identify which FvsVariant candidates produced glyph
                        // sequences identical to the bare (BaseTransliteration) candidate. Relabel those
                        // variants' semantic name from "variant" to "same as base" (FVS has no visible effect on this
                        // letter + position combination).
                        int bareIdx = -1;
                        for (int si = 0; si < semanticItems.Count; si++)
                        {
                            if (semanticItems[si].Kind == CandidateKind.BaseTransliteration) { bareIdx = si; break; }
                        }
                        if (bareIdx >= 0 && bareIdx < shapedResults.Count)
                        {
                            var bareShape = shapedResults[bareIdx];
                            // Mark an FvsVariant redundant if its shaped glyph sequence
                            // equals the bare OR an EARLIER-KEPT variant. Checking bare-equality alone would let
                            // letters whose FVS1/2/3 all map to the SAME single variant glyph (e.g. k'/g'/h'
                            // INIT/MEDI: all one glyph, distinct from bare) surface 3 visually-identical candidates.
                            // Variant-vs-variant dedup keeps the FIRST distinct variant and drops the rest (marked
                            // LABEL_SAME_AS_BARE so the existing collapse below removes them).
                            for (int si = 0; si < semanticItems.Count && si < shapedResults.Count; si++)
                            {
                                if (si == bareIdx) continue;
                                if (semanticItems[si].Kind != CandidateKind.FvsVariant) continue;
                                var variantShape = shapedResults[si];
                                bool redundant = GlyphSequencesEqual(bareShape, variantShape);
                                for (int pj = 0; pj < si && !redundant; pj++)
                                {
                                    if (pj == bareIdx) continue;
                                    if (semanticItems[pj].Kind != CandidateKind.FvsVariant) continue;
                                    if (semanticItems[pj].SemanticName == SemanticCandidateItem.LABEL_SAME_AS_BARE) continue; // already-dropped duplicate
                                    if (pj < shapedResults.Count && GlyphSequencesEqual(shapedResults[pj], variantShape)) redundant = true;
                                }
                                if (redundant)
                                {
                                    semanticItems[si] = semanticItems[si].WithSemanticName(SemanticCandidateItem.LABEL_SAME_AS_BARE);
                                }
                            }
                        }

                        // Dynamic collapse: remove FvsVariant candidates whose
                        // semantic name is "same as base" (their shaped glyph sequence equals bare). These candidates
                        // are visually indistinguishable from bare and selecting them gives no user-value.
                        // Preserves parallel-array alignment across candidates / shapedResults / lengths / semanticItems.
                        {
                            int write = 0;
                            for (int read = 0; read < semanticItems.Count; read++)
                            {
                                bool isDuplicate = semanticItems[read].Kind == CandidateKind.FvsVariant
                                                 && semanticItems[read].SemanticName == SemanticCandidateItem.LABEL_SAME_AS_BARE;
                                if (isDuplicate) continue;
                                if (write != read)
                                {
                                    semanticItems[write] = semanticItems[read];
                                    if (read < candidates.Count) candidates[write] = candidates[read];
                                    if (read < shapedResults.Count) shapedResults[write] = shapedResults[read];
                                    if (read < lengths.Count) lengths[write] = lengths[read];
                                    // Keep slicesAtFocus in sync with
                                    // the collapse. Without this, slice[hl] would point to a stale
                                    // pre-collapse candidate once same-as-bare entries are dropped.
                                    if (read < slicesAtFocus.Count) slicesAtFocus[write] = slicesAtFocus[read];
                                }
                                write++;
                            }
                            int removed = semanticItems.Count - write;
                            if (removed > 0)
                            {
                                semanticItems.RemoveRange(write, semanticItems.Count - write);
                                int cTrim = Math.Min(write, candidates.Count);
                                if (cTrim < candidates.Count) candidates.RemoveRange(cTrim, candidates.Count - cTrim);
                                int sTrim = Math.Min(write, shapedResults.Count);
                                if (sTrim < shapedResults.Count) shapedResults.RemoveRange(sTrim, shapedResults.Count - sTrim);
                                int lTrim = Math.Min(write, lengths.Count);
                                if (lTrim < lengths.Count) lengths.RemoveRange(lTrim, lengths.Count - lTrim);
                                // Trim slicesAtFocus to same length.
                                int slTrim = Math.Min(write, slicesAtFocus.Count);
                                if (slTrim < slicesAtFocus.Count) slicesAtFocus.RemoveRange(slTrim, slicesAtFocus.Count - slTrim);
                            }
                        }

                        // 4. Notify UI (Deep Copy results)
                        var finalShaped = new List<List<ManjuShaperCore.FinalGlyph>>(shapedResults);
                        var finalLengths = new List<float>(lengths);
                        var finalCandidates = new List<string>(candidates);
                        // Copy semantic items for frame commit
                        var finalSemanticItems = new List<SemanticCandidateItem>(semanticItems);

                        // Pass input.CompositionSnapshot as RawInput for backend-sourced
                        // Latin echo. This is the same buffer the keymap lookups consume, so RawInput
                        // and ShapedResults are atomically source-consistent (single-frame swap invariant).
                        // Also ship per-candidate slice list so presenter can
                        // pass the exact slice into MarkPositionSelected without reverse-engineering it.
                        var finalSlicesAtFocus = new List<string>(slicesAtFocus);
                        var frame = new RenderFrame(finalShaped, finalLengths, finalCandidates, finalSemanticItems, upem, input.CompositionSnapshot ?? string.Empty, finalSlicesAtFocus, focusResolved, input.Epoch, engine.EngineId);


                        // LINEARIZED publish: check + UpdateFrame are one atomic block
                        // under _epochGate, so a Clear() cannot slip between the check and the
                        // publish. Consumers additionally reject by
                        // frame.SourceEpoch, so even the "published just before Clear" ordering cannot
                        // leak a dead-word frame into the NEXT composition.
                        // The NOTIFICATION is inside the lock too. Leaving
                        // OnFrameReadyFrame outside it lets a Clear() land between publish and
                        // delivery, and a subscriber then receives a dead-epoch frame. Publishing and
                        // announcing are one atomic step: whoever wins the
                        // lock, a subscriber can only ever be handed a frame of the CURRENT generation.
                        // Safe to invoke under the lock: every subscriber does cheap work only
                        // (PostMessage / set an event), the dictionary callback already runs under this
                        // same gate above, and the lock is re-entrant so a handler calling back into
                        // Clear() cannot self-deadlock.
                        bool staleAtPublish = false;
                        lock (engine._epochGate)
                        {
                            // ONLY the publish decision + the GlobalState swap happen here. No foreign
                            // code runs under this lock.
                            if (input.Epoch != engine.CurrentCompositionEpoch) staleAtPublish = true;
                            else GlobalState.UpdateFrame(frame);
                        }
                        if (staleAtPublish)
                        {
                            continue;
                        }
                        engine.DeliverFrame(frame, input.Epoch, DeliveryOrigin.Worker);


                        // The legacy parameterless signal is raised by DeliverFrame,
                        // after the frame-carrying one and under the same per-subscriber guards.
                    }
                    else
                    {
                        // Empty-frame path also stamps RawInput for echo consistency.
                        // Empty frames also carry the resolved focus + epoch; a default of -1
                        // would weaken identity checks on the empty path.
                        var frame = new RenderFrame(new(), new(), new(), new(), 1000, input.CompositionSnapshot ?? string.Empty, null, focusResolved, input.Epoch, engine.EngineId);
                        bool staleEmpty = false;
                        lock (engine._epochGate)   // publish decision only — no foreign code under the lock
                        {
                            if (input.Epoch != engine.CurrentCompositionEpoch) staleEmpty = true;
                            else GlobalState.UpdateFrame(frame);
                        }
                        if (staleEmpty)
                        {
                            continue;
                        }
                        engine.DeliverFrame(frame, input.Epoch, DeliveryOrigin.Worker);
                    }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
            }
        }

        public void Dispose()
        {
            // Instance disposal does not stop the global worker
        }

        // RUNTIME INPUT-SCHEME SWITCHING, so every scheme can be reached while typing, without editing
        // the yaml and restarting the IME.
        //
        // `_schemeOverride` starts null, so startup resolves the scheme from `keymap.scheme`. The
        // override only exists once the user cycles the scheme from the settings menu, and it is per-engine, not written back
        // to the file.
        private static readonly string[] InputSchemes =
            { "refined", "mollendorff", "norman", "hu", "abkai", "babelpad" };
        private string? _schemeOverride;

        /// <summary> The scheme in force right now: the runtime override if the user has cycled,
        /// otherwise whatever `keymap.scheme` says.</summary>
        public string ActiveInputScheme =>
            _schemeOverride ?? _config.GetString("keymap.scheme", "refined");


        /// <summary> Advance to the next input scheme and reload the token→codepoint table.
        ///
        /// EFFECT ON A COMPOSITION IN PROGRESS. The raw buffer holds
        /// LATIN as typed and units are parsed from it on demand, so reloading the table re-reads the
        /// existing buffer through the new scheme.</summary>
        // A SWITCH MID-COMPOSITION TAKES EFFECT AT THE NEXT COMPOSITION, NOT IMMEDIATELY.
        //
        // Applying the new table
        // to the buffer already on screen re-reads LATIN THAT IS ALREADY THERE, and that goes wrong in
        // two ways:
        //   refined → hu, buffer "k'ata":  183A 1820 1868 1820 → 1874 1820 1868 1820
        //     the loan velar k' silently becomes a plain k, because ' is not part of a token in hu.
        //   hu → refined, buffer "guusa":  4 units → 5 units, 1864 1861 … → 1864 1860 1860 …
        //     ū splits into u+u, so every unit index after the first moves.
        // Selection state is keyed BY UNIT INDEX, so the second case shifts the ground
        // under any pick the user had already made. That is a reason to stop instead of working
        // around it, so the switch does not touch a live composition at all.
        //
        // Deferring is not a compromise here — it is also what a user would expect: the letters already on
        // screen were typed in the old romanization and go on meaning what they meant.
        private string? _pendingSchemeSwitch;


        // THE DISPLAY-SCHEME AXIS: display.scheme. Seven values: "input" (mirror what was typed) plus the
        // six romanizations. Independent of the input scheme: you can type in hu and read it back in Möllendorff.
        private static readonly string[] DisplaySchemes =
            { "input", "refined", "mollendorff", "norman", "hu", "abkai", "babelpad" };
        private string? _displaySchemeOverride;

        public string ActiveDisplayScheme =>
            _displaySchemeOverride ?? _config.GetString("display.scheme", "input");


        /// <summary> Advance display.scheme. Safe to apply immediately, unlike the input scheme: this
        /// changes how the composed CODEPOINTS are rendered back as Latin, and the codepoints do not move.
        /// Nothing is re-parsed, so no unit index can shift and no pick can be stranded.</summary>
        public void CycleDisplayScheme()
        {
            string current = ActiveDisplayScheme;
            int i = 0;
            for (int k = 0; k < DisplaySchemes.Length; k++)
                if (string.Equals(DisplaySchemes[k], current, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            int nextIndex = (i + 1) % DisplaySchemes.Length;
            _displaySchemeOverride = DisplaySchemes[nextIndex];
            LoadDisplayScheme();
            // Applies immediately — display is a re-read of finished text, not of the buffer.
            EmitSwitchToast(ToastAxisDisplayScheme, _displaySchemeOverride, nextIndex + 1, DisplaySchemes.Length, deferred: false);
        }

        // THE AXIS IS READ OFF THE DISK.
        //
        // The axis is the set of subdirectories of Resource/Dictionary that hold BOTH files the loader
        // needs (DictionaryManifestFile and DictionaryLemmaFile, below). A directory missing either is
        // skipped, so the dictionary cycle never lands on a dictionary that cannot load. Installing a dictionary adds
        // it to the axis; removing one takes it away.
        //
        // ORDER = ORDINAL SORT OF THE DIRECTORY NAME, and the reason matters because that order IS the
        // dictionary cycle:
        //   · the directory name is the only key that exists before the manifest is opened, so the order
        //     cannot depend on load success or on file contents;
        //   · Ordinal, not culture-aware: the order does not depend on the locale, so a machine with a
        //     Turkish or Swedish locale cycles in the same order as any other;
        //   · it is stable under adding a dictionary: existing names keep their relative order.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        internal const string DictionaryManifestFile = "dictionary.yaml";
        internal const string DictionaryLemmaFile = "manchu_lemmas.jsonl";

        /// <summary>
        /// What <c>dictionary.active</c> falls back to when the settings file names no dictionary.
        ///
        /// Still a NAME, not an empty string.
        /// </summary>
        public const string DefaultDictionaryName = "default";


        internal static string DictionaryRootDirectory()
        {
            try
            {
                string? dllDir = System.IO.Path.GetDirectoryName(Globals.GetModulePath());
                return dllDir == null ? string.Empty : System.IO.Path.Combine(dllDir, "Resource", "Dictionary");
            }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Every installed dictionary, in cycle order. Not cached: the scan is a handful
        /// of directory probes, and a cache would need an invalidation story for a dictionary installed
        /// while the IME is running — a bug waiting to happen in exchange for nothing measurable.
        /// </summary>
        public static IReadOnlyList<string> ScanInstalledDictionaries()
        {
            var found = new List<string>();
            try
            {
                string root = DictionaryRootDirectory();
                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) return found;
                foreach (string dir in System.IO.Directory.GetDirectories(root))
                {
                    if (!System.IO.File.Exists(System.IO.Path.Combine(dir, DictionaryManifestFile))) continue;
                    if (!System.IO.File.Exists(System.IO.Path.Combine(dir, DictionaryLemmaFile))) continue;
                    string name = System.IO.Path.GetFileName(dir);
                    if (!string.IsNullOrEmpty(name)) found.Add(name);
                }
                found.Sort(StringComparer.Ordinal);
            }
            catch (Exception)
            {
            }
            return found;
        }

        private string? _dictionaryOverride;

        /// <summary>
        /// The active dictionary — and it is guaranteed to be one that is actually installed
        /// whenever anything is. A configuration naming a dictionary that is not on disk must not leave
        /// the engine pointing at it: the module would fail to load, and every lookup would come back
        /// empty with nothing on screen to say why.
        /// </summary>
        public string ActiveDictionary
        {
            get
            {
                string wanted = _dictionaryOverride ?? _config.GetString("dictionary.active", DefaultDictionaryName);
                var installed = ScanInstalledDictionaries();
                if (installed.Count == 0) return wanted;      // nothing installed: keep the name, say nothing new
                foreach (string n in installed)
                    if (string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase)) return n;
                return installed[0];
            }
        }


        /// <summary> What the settings screen says when there is nothing to cycle to. English,
        /// this is release UI.</summary>
        public const string OnlyOneDictionarySuffix = "  (only one installed)";

        /// <summary> What the settings screen says when the axis is EMPTY. English, release UI.</summary>
        public const string NoDictionarySuffix = "  (none installed)";

        /// <summary>
        /// The suffix the settings screen puts after the dictionary's name.
        ///
        /// ZERO IS ITS OWN CASE, and saying so is the whole reason this is a method.
        /// The NAME still shows in the empty case.
        /// A blank would say less and answer nothing.
        /// </summary>
        internal static string DictionaryCountSuffix(int installed) =>
            installed == 0 ? NoDictionarySuffix
            : installed == 1 ? OnlyOneDictionarySuffix
            : string.Empty;

        public void CycleDictionary()
        {
            string current = ActiveDictionary;
            var names = ScanInstalledDictionaries();

            if (names.Count <= 1)
            {
                return;
            }

            int i = 0;
            for (int k = 0; k < names.Count; k++)
                if (string.Equals(names[k], current, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            string? previousOverride = _dictionaryOverride;
            _dictionaryOverride = names[(i + 1) % names.Count];

            // AND MAKE IT TAKE EFFECT.
            //
            // ON FAILURE THE AXIS ROLLS BACK, and that is a deliberate choice, not tidiness.
            // The module keeps serving the dictionary it already had, which is its own rule, so if the
            // label stayed on the new name the settings menu would name one dictionary while lookups
            // came from another. The menu has to be trustable, and letting it lie here would undo
            // that.
            //
            // When no module is compiled in (DictionaryModuleRegistry.Factory == null) there is nothing
            // to roll back TO: the axis is a label over a feature that is not present, and it keeps
            // that behaviour.
            bool applied = DictionaryModule?.ReloadDictionary() ?? false;
            if (!applied && DictionaryModule != null)
            {
                _dictionaryOverride = previousOverride;
            }

            // EMITTED AFTER THE ROLLBACK DECISION, DELIBERATELY. This axis can refuse to
            // move (the module keeps serving what it already had), and exists so the settings screen
            // can be trusted. A toast fired before the rollback would announce a dictionary the lookups
            // are not using — the same lie, on a second surface. Reading the value back here means the
            // toast says whatever actually happened, including "it did not move".
            int settled = 0;
            for (int k = 0; k < names.Count; k++)
                if (string.Equals(names[k], ActiveDictionary, StringComparison.OrdinalIgnoreCase)) { settled = k; break; }
            EmitSwitchToast(ToastAxisDictionary, SettingsSummaryEntries()[ToastAxisDictionary].Value,
                            settled + 1, names.Count, deferred: false);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SETTINGS, FROM ONE TABLE.
        //
        // Each row is one setting: its name, how to read its current value, and how to move it to the
        // next value. The settings menu (Ctrl+Shift+`) lists the rows in this order and adds a last row
        // for the dictionary window; the switch toast takes the names from here. A
        // setting that is not in this table exists for none of them. Adding a setting is one row here.
        //
        // THE THEME is the one value the engine does not own: the palette lives on the presenter, which
        // supplies ThemeNameProvider and ThemeCycler.
        private static readonly (string Topic, Func<ManjuEngine, string> Value, Action<ManjuEngine> Cycle)[] SettingsSources =
            new (string, Func<ManjuEngine, string>, Action<ManjuEngine>)[]
        {
            ("input scheme",        e => e.ActiveInputScheme,                                                e => e.CycleInputScheme()),
            ("display scheme",      e => e.ActiveDisplayScheme,                                              e => e.CycleDisplayScheme()),
            ("dictionary",          e => e.ActiveDictionary +
                                        DictionaryCountSuffix(ScanInstalledDictionaries().Count),        e => e.CycleDictionary()),
            ("separator shape",     e => $"U+{(int)e.SeparatorDisplayChar:X4}  ({e.SeparatorDisplayStyle})", e => e.CycleSeparatorDisplay()),
            ("apostrophe shape",    e => $"U+{(int)e.ApostropheDisplayChar:X4}",                            e => e.CycleApostropheDisplay()),
            ("connector shape",     e => $"U+{(int)e.ConnectorDisplayChar:X4}  ({e.ConnectorDisplayStyle})", e => e.CycleConnectorDisplay()),
            ("connector codepoint", e => $"U+{(int)e.SuffixConnectorChar:X4}",                              e => e.ToggleSuffixConnector()),
            ("theme",               e => e.ThemeNameProvider?.Invoke() ?? ThemeValueWithNoPresenter,        e => e.ThemeCycler?.Invoke()),
            ("positional form",     e => e.ForcedForm.ToString().ToLowerInvariant(),                        e => e.CyclePositionalForcing()),
            ("forcing method",      e => ForcingMethodName(e.ForcingMethodIndex),                          e => e.CycleForcingMethod()),
        };

        /// <summary> What the theme row reads when nothing has supplied a theme name. Not
        /// blank and not a plausible-looking name: a row that silently shows the wrong thing is worse than
        /// one that says it does not know.</summary>
        public const string ThemeValueWithNoPresenter = "(no window attached)";

        /// <summary> The theme name, supplied by whoever owns the palette. The engine has no
        /// theme of its own — <c>CandidateListUIPresenter</c> sets this in its constructor.</summary>
        public Func<string>? ThemeNameProvider { get; set; }

        /// <summary> Moves to the next theme. Supplied by the presenter next to
        /// <see cref="ThemeNameProvider"/>; while it is null the theme row changes nothing.</summary>
        public Action? ThemeCycler { get; set; }

        /// <summary> The values of appearance.theme, one for each theme in the presenter's
        /// order: the theme's name in lower case, without spaces.</summary>
        public static readonly string[] ThemeKeys =
            { "windows11", "soyoung", "solarizeddark", "court", "highcontrast", "beangreen", "google", "android" };

        /// <summary> Moves to the theme with that index. Supplied by the presenter next to
        /// <see cref="ThemeCycler"/>; while it is null the settings file's theme changes nothing.</summary>
        public Action<int>? ThemeSetter { get; set; }

        /// <summary> Moves the palette to the theme appearance.theme names, or to the first
        /// one when the key is not there. Called when the presenter is attached and when the key
        /// changes; a value that is not a theme is refused with the whole file before this runs.</summary>
        public void ApplyConfiguredTheme()
        {
            string value = _config?.GetString("appearance.theme", ThemeKeys[0]) ?? ThemeKeys[0];
            int index = Array.IndexOf(ThemeKeys, value);
            if (index >= 0) ThemeSetter?.Invoke(index);
        }


        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SETTINGS MENU. One row per entry of SettingsSources, in its order, then the dictionary
        // window. The menu reads the values from SettingsMenuRows and moves a row with
        // CycleSettingsMenuRow; it holds no copy of either.
        // ══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary> The name of the menu's last row.</summary>
        public const string DictionaryWindowTopic = "dictionary window";

        /// <summary> What the dictionary-window row reads when no dictionary window can exist.</summary>
        public const string DictionaryWindowUnavailable = "(no dictionary)";

        /// <summary> Every setting, then the dictionary window.</summary>
        public static int SettingsMenuRowCount => SettingsSources.Length + 1;

        /// <summary> Topic and current value of every menu row, in menu order. An input-scheme switch
        /// made during a composition applies when the word ends; until then the row shows the scheme
        /// chosen, followed by <see cref="DeferredToastSuffix"/>.</summary>
        public IReadOnlyList<(string Topic, string Value)> SettingsMenuRows()
        {
            var rows = new List<(string Topic, string Value)>(SettingsMenuRowCount);
            foreach (var s in SettingsSources) rows.Add((s.Topic, s.Value(this)));
            if (_pendingSchemeSwitch != null)
                rows[ToastAxisInputScheme] = (rows[ToastAxisInputScheme].Topic, $"{_pendingSchemeSwitch} ({DeferredToastSuffix})");
            var module = DictionaryModule;
            string window = module == null || !module.IsAvailable
                ? DictionaryWindowUnavailable
                : module.IsWindowEnabledByUser ? "shown" : "hidden";
            rows.Add((DictionaryWindowTopic, window));
            return rows;
        }

        /// <summary> Moves one menu row to its next value. Returns false for an index outside the menu,
        /// and for the dictionary window when no dictionary window can exist.</summary>
        public bool CycleSettingsMenuRow(int row)
        {
            if (row >= 0 && row < SettingsSources.Length)
            {
                SettingsSources[row].Cycle(this);
                return true;
            }
            if (row == SettingsSources.Length)
                return DictionaryModule?.ToggleWindowVisibility() == true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SWITCH TOAST: one line, on screen for a few seconds, after any setting change.
        //
        // An input-scheme switch made during a composition is DEFERRED, because re-reading a live buffer
        // silently demotes `k'` to `k` and splits `ū` into `u`+`u`, shifting every index after it. The
        // rest of that word still uses the old scheme, and the toast says so with the suffix below. The
        // suffix appears ONLY on that path.
        //
        // The topic is indexed out of SettingsSources, the table the settings menu reads, so the toast
        // and the menu cannot name one axis two ways.
        // ══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary> Index into <c>SettingsSources</c>, in its order.
        /// Named constants instead of bare numbers, so a reordering of the table is a compile-time move,
        /// not a silently mislabelled toast.</summary>
        // - POSITIONAL FORM FORCING -----------------------------------------------
        //
        // The five forcing states: natural, then a forced initial, final or
        // isolated; how the forcing is realised belongs on a separate key.
        //
        // TWO switches: CyclePositionalForcing walks the five states, CycleForcingMethod walks the implementation methods.
        // There is deliberately NO key that types a bare ZWJ, which has no use.
        //
        // WHY A METHOD IS A PAIR OF CONTROLS, AND WHY THERE IS MORE THAN ONE. `ManjuArabicShaper.Join`
        // indexes its transition table by the NEIGHBOUR'S JOINING TYPE alone (`col = (int)jt`, with join-
        // causing folded onto dual-joining's column) and skips transparent characters before the table is
        // consulted. So the machine cannot see WHICH character sits beside a letter, only what type it is:
        // a `U` resets the run (a BREAKER) and a `C`/`D` writes INIT/MEDI back onto its left neighbour (a
        // JOINER). Every forcing is therefore "what sits on each side", and the number of methods is
        // (usable joiners) x (usable breakers). The candidates are enumerated from the joining table,
        // and a candidate is usable only if it renders invisibly.
        public enum PositionalForcing { Natural = 0, Initial = 1, Medial = 2, Final = 3, Isolated = 4 }

        /// <summary>(joiner, breaker) pairs. A control that paints ink would add a visible character
        /// the user never typed, so only controls that render invisibly qualify.</summary>
        public static readonly (char Joiner, char Breaker)[] ForcingMethods = new[]
        {
            ((char)0x200D, (char)0x180E),   // ZWJ + MVS
            ((char)0x200D, (char)0x200C),   // ZWJ + ZWNJ
        };

        private PositionalForcing _forcedForm = PositionalForcing.Natural;
        private int _forcingMethod = 0;

        public PositionalForcing ForcedForm => _forcedForm;
        public int ForcingMethodIndex => _forcingMethod;
        public static string ForcingMethodName(int i) =>
            $"U+{(int)ForcingMethods[i].Joiner:X4} / U+{(int)ForcingMethods[i].Breaker:X4}";

        /// <summary>The five states, in order.</summary>
        public void CyclePositionalForcing()
        {
            var all = (PositionalForcing[])Enum.GetValues(typeof(PositionalForcing));
            int i = Array.IndexOf(all, _forcedForm);
            _forcedForm = all[(i + 1) % all.Length];
            EmitSwitchToast(ToastAxisPositionalForm, SettingsSummaryEntries()[ToastAxisPositionalForm].Value,
                            Array.IndexOf(all, _forcedForm) + 1, all.Length, deferred: false);
        }

        /// <summary>Which pair of controls does the forcing.</summary>
        public void CycleForcingMethod()
        {
            _forcingMethod = (_forcingMethod + 1) % ForcingMethods.Length;
            EmitSwitchToast(ToastAxisForcingMethod, SettingsSummaryEntries()[ToastAxisForcingMethod].Value,
                            _forcingMethod + 1, ForcingMethods.Length, deferred: false);
        }

        /// <summary>
        /// Wraps a committed run so the shaper gives it the forced form.
        ///
        /// BOTH SIDES ARE ALWAYS WRITTEN. `X + ZWJ` does give INIT when X starts the run, but the switch
        /// has to mean the same thing when the letter already has a left neighbour, and there `W + X + ZWJ`
        /// is medial. The far-side breaker is what makes the forcing positional rather than contextual.
        ///
        /// Returns the input untouched for `Natural` - the switch must add nothing at all when it is off,
        /// or every commit in the product would start carrying control characters.
        /// </summary>
        public string ApplyPositionalForcing(string codePoints)
        {
            if (_forcedForm == PositionalForcing.Natural || string.IsNullOrEmpty(codePoints)) return codePoints;

            // THE FORM COMES OUT OF THE REACHABLE SET, NOT OUT OF A BLIND WRAP.
            //
            // A blind wrap can invent a glyph the script cannot otherwise contain: U+1860 forced to
            // FINAL produces gid 390, a shape that no natural context reaches.
            // Candidates are DRAWN FROM the forms a letter really reaches, so an
            // unreachable one is never built instead of built and then judged.
            //
            // A single letter is the case the switch is for. A longer run gets the plain wrap below: the
            // reachable set is a property of ONE letter at ONE position, so it cannot answer for a run,
            // and silently doing nothing would take the feature away from the user.
            if (IsSingleLetter(codePoints))
            {
                byte[]? font = GetFontData();
                if (font != null)
                {
                    int cp = char.ConvertToUtf32(codePoints, 0);
                    var picks = ManjuPositionalForms.Candidates(font, cp, _forcedForm);
                    // Nothing offered means the letter does not reach this position at all. Committing
                    // the letter untouched is the honest answer; inventing a shape is not.
                    if (picks.Count > 0)
                    {
                        // The method switch still chooses: prefer a candidate built with the
                        // method they selected. Always taking picks[0] would make CycleForcingMethod inert for
                        // a single letter.
                        foreach (var c in picks)
                            if (c.MethodIndex == _forcingMethod) return c.Text;
                        return picks[0].Text;
                    }
                    return codePoints;
                }
            }

            var (j, b) = ForcingMethods[_forcingMethod];
            return _forcedForm switch
            {
                PositionalForcing.Initial => b + codePoints + j,
                PositionalForcing.Medial => j + codePoints + j,
                PositionalForcing.Final => j + codePoints + b,
                PositionalForcing.Isolated => b + codePoints + b,
                _ => codePoints,
            };
        }

        /// <summary>One Unicode scalar, counting a surrogate pair as one.</summary>
        private static bool IsSingleLetter(string s) =>
            s.Length == 1 || (s.Length == 2 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1]));

        public const int ToastAxisInputScheme = 0;
        public const int ToastAxisDisplayScheme = 1;
        public const int ToastAxisDictionary = 2;
        public const int ToastAxisSeparatorShape = 3;
        public const int ToastAxisApostropheShape = 4;
        public const int ToastAxisConnectorShape = 5;
        public const int ToastAxisConnectorCodepoint = 6;
        public const int ToastAxisTheme = 7;
        public const int ToastAxisPositionalForm = 8;
        public const int ToastAxisForcingMethod = 9;

        /// <summary> Appears ONLY when the switch was deferred (<c>_pendingSchemeSwitch</c>
        /// non-null). The immediate path must never print it — a line that says the same thing on both
        /// paths would hide exactly the case the suffix exists for.</summary>
        public const string DeferredToastSuffix = "applies to the next word";

        /// <summary> Where a toast goes. The presenter sets this in its constructor, the same
        /// way it sets <see cref="ThemeNameProvider"/>.</summary>
        public Action<string>? SwitchToastSink { get; set; }

        /// <summary> The toast line for one axis. The topic is read from
        /// <c>SettingsSources[axis]</c> and cannot be supplied by the caller — that is what stops a
        /// second set of axis names from existing.</summary>
        public static string SwitchToastLineFor(int axis, string newValue, int position, int total, bool deferred)
        {
            string topic = SettingsSources[axis].Topic;
            string line = $"{topic} → {newValue}  ({position}/{total})";
            return deferred ? line + "   " + DeferredToastSuffix : line;
        }


        private void EmitSwitchToast(int axis, string newValue, int position, int total, bool deferred)
        {
            string line = SwitchToastLineFor(axis, newValue, position, total, deferred);
            SwitchToastSink?.Invoke(line);
        }

        /// <summary> Topic and value separately, so a caller can read ONE setting's value by its
        /// axis index (the Cycle methods do, for the toast) instead of parsing a line of text.</summary>
        public IReadOnlyList<(string Topic, string Value)> SettingsSummaryEntries()
        {
            var rows = new List<(string, string)>(SettingsSources.Length);
            foreach (var s in SettingsSources) rows.Add((s.Topic, s.Value(this)));
            return rows;
        }

        public void CycleInputScheme()
        {
            string current = _pendingSchemeSwitch ?? ActiveInputScheme;
            int i = 0;
            for (int k = 0; k < InputSchemes.Length; k++)
                if (string.Equals(InputSchemes[k], current, StringComparison.OrdinalIgnoreCase)) { i = k; break; }
            int nextIndex = (i + 1) % InputSchemes.Length;
            string next = InputSchemes[nextIndex];

            if (Builder_Composing.Length == 0)
            {
                _schemeOverride = next;
                _pendingSchemeSwitch = null;
                LoadMappings();
                // Applied now ⇒ no deferral suffix.
                EmitSwitchToast(ToastAxisInputScheme, next, nextIndex + 1, InputSchemes.Length, deferred: false);
            }
            else
            {
                _pendingSchemeSwitch = next;
                // THE CASE THE DEFERRED SUFFIX EXISTS FOR. The switch is real but has not taken
                // effect, so the next keystrokes still use the OLD scheme. The value shown is `next`
                // (what the user just chose), NOT ActiveInputScheme (which is still the old one) —
                // the suffix is what tells them the difference.
                EmitSwitchToast(ToastAxisInputScheme, next, nextIndex + 1, InputSchemes.Length, deferred: true);
            }
        }

        /// <summary> Apply a deferred switch. Called from Clear(), i.e. when the composition the
        /// user was in the middle of has ended, which is the point at which re-reading nothing is safe.</summary>
        private void ApplyPendingSchemeSwitch()
        {
            if (_pendingSchemeSwitch == null) return;
            string next = _pendingSchemeSwitch;
            _pendingSchemeSwitch = null;
            _schemeOverride = next;
            LoadMappings();
        }

        private void LoadMappings()
        {
            try
            {
                // CLEAR FIRST. The loading loop below only ADDS keys, and it runs again on every
                // scheme switch, so not clearing would leave the
                // UNION of the old and new schemes active: after switching refined→hu you could still type
                // `k'` (refined's loan velar) because its key was never removed.
                //
                // Clearing is what makes the switch mean anything.
                // Zero effect on startup: at construction these are already empty.
                Dictionary_Mappings.Clear();
                Dictionary_Alternatives.Clear();
                Dictionary_Meanings.Clear();
                Dictionary_Display.Clear();

                String String_DllPath = Globals.GetModulePath();
                String? String_DllDir = Path.GetDirectoryName(String_DllPath);
                if (String_DllDir == null) return;

                // Resource/ unified tree.
                // The keymap is YAML, parsed by the shared Core MiniYamlReader.
                // One file per romanization input scheme under Resource/Keymap/
                // schemes/<scheme>.yaml; the active scheme is config `keymap.scheme` (default "refined"
                // = Refined Romanisation, a hybrid scheme).
                // Each scheme is purely token->codepoint (transliteration-free); dictionary lookup
                // transliterates the COMPOSED CODEPOINTS to the dict's own index form, so the engine
                // carries no romanization mapping at all.
                string scheme = ActiveInputScheme;   // override if the user cycled, else the config
                String String_Path = Path.Combine(String_DllDir, "Resource", "Keymap", "schemes", scheme + ".yaml");
                if (File.Exists(String_Path))
                {
                    var root = CSharpTSFInput.Core.Configuration.MiniYamlReader.ParseFile(String_Path);
                    CSharpTSFInput.Core.Configuration.MiniYamlReader.Node? mapNode = null;
                    root.Mapping?.TryGetValue("mappings", out mapNode);
                    foreach (var kv in (mapNode?.Mapping ?? new Dictionary<string, CSharpTSFInput.Core.Configuration.MiniYamlReader.Node>()))
                    {
                        // Accept either string (1-to-1) or array (1-to-N) form.
                        // String: "i": "\u1822"                        → primary = "\u1822", no alternatives
                        // Array:  "i": ["\u1822", "\u1866", "\u1867"] → primary = "\u1822", alternatives = ["\u1866","\u1867"]
                        // scalar = 1-to-1 primary; list = 1-to-N (primary + alternatives).
                        if (kv.Value.List != null && kv.Value.List.Count > 0)
                        {
                            Dictionary_Mappings[kv.Key] = kv.Value.List[0];
                            if (kv.Value.List.Count > 1)
                                Dictionary_Alternatives[kv.Key] = kv.Value.List.GetRange(1, kv.Value.List.Count - 1);
                        }
                        else if (kv.Value.Scalar != null)
                        {
                            Dictionary_Mappings[kv.Key] = kv.Value.Scalar;
                        }
                    }

                    // Optional "meanings" section: codepoint → human-readable label.
                    CSharpTSFInput.Core.Configuration.MiniYamlReader.Node? meanNode = null;
                    root.Mapping?.TryGetValue("meanings", out meanNode);
                    if (meanNode?.Mapping != null)
                    {
                        foreach (var kvMeaning in meanNode.Mapping)
                            if (kvMeaning.Value.Scalar != null) Dictionary_Meanings[kvMeaning.Key] = kvMeaning.Value.Scalar;
                    }

                    // Optional "display:" section: token -> ON-SCREEN romanization
                    // (customisation interface). Absent/empty in all shipped schemes => identity
                    // (DisplayFormForKey shows the token as typed). Display-only — NOT a dictionary
                    // romanization (the dict transliterates codepoints itself), so the engine keeps no
                    // fixed romanization assumption. This is the boundary that keeps on-screen romanization
                    // customisable per scheme without binding the engine to any one system.
                    CSharpTSFInput.Core.Configuration.MiniYamlReader.Node? dispNode = null;
                    root.Mapping?.TryGetValue("display", out dispNode);
                    if (dispNode?.Mapping != null)
                    {
                        foreach (var kvDisp in dispNode.Mapping)
                            if (kvDisp.Value.Scalar != null) Dictionary_Display[kvDisp.Key] = kvDisp.Value.Scalar;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// Guaranteed font support for rendering:
        /// the built-in Noto Sans Mongolian is loaded automatically, so the render layer always has correct glyph data.
        /// </summary>
        private void LoadDefaultFont()
        {
            try
            {
                String String_DllPath = Globals.GetModulePath();
                String? String_DllDir = Path.GetDirectoryName(String_DllPath);
                if (String_DllDir == null) return;

                // Font path is configuration-driven. Default falls back to the bundled
                // Noto Sans Mongolian under the Resource/ tree if config is missing.
                string configuredFontRel = _config.GetString("font.path", "Resource/Font/NotoSansMongolian-Regular.ttf");
                // Normalize separators for cross-format tolerance (Windows path expects backslash;
                // YAML config uses forward slash for cross-platform readability).
                configuredFontRel = configuredFontRel.Replace('/', Path.DirectorySeparatorChar);
                String String_Path = Path.Combine(String_DllDir, configuredFontRel);
                if (File.Exists(String_Path))
                {
                    _fontFilePath = String_Path; // store the path for file-based loading
                    Buffer_FontData = File.ReadAllBytes(String_Path);
                }
            }
            catch (Exception)
            {
            }
        }

        public Byte[]? GetFontData() => Buffer_FontData;

        /// <summary>
        /// Returns the font file path for file-based loading.
        /// This avoids custom COM interface issues in NativeAOT.
        /// </summary>
        public String? GetFontFilePath() => _fontFilePath;

        // AN APPEND RELEASES THE MANUAL FOCUS, HERE, BEFORE THE JOB IS ENQUEUED.
        //
        // On an append or a tail delete, the focus follows the new last item. The obvious place to implement that is
        // the presenter, when the frame comes back, but that is one tick too late. The worker resolves the
        // focus for the frame it is building from UiFocusAuthoritative AS OF THE MOMENT THE JOB RUNS. Move
        // the focus only after the frame arrives and every keystroke ships a frame built for the OLD focus,
        // which the presenter must then throw away and rebuild: two frames per key, which shows as a
        // visible flick. Releasing it here, synchronously on the UI thread before EnqueueInput,
        // means the job is resolved against the tail in the first place and one keystroke stays one frame.
        //
        // Sentinel (-1), not a computed index: this method appends a raw CHARACTER, and units are not
        // characters — the tail unit index does not exist yet at this point and is only known once the
        // snapshot is built. -1 says the thing that is actually true, "the user is not holding a focus",
        // and every sentinel resolution point resolves that to the tail.
        public String AddInput(Char Char_Input)
        {
            _uiFocusAuthoritative = -1;
            Builder_Composing.Append(Char.ToLower(Char_Input));
            return GetTranslatedText();
        }

        // Tail-erase, same rule and same reason as AddInput above, and separable from it:
        // erasing the tail deletes the item the focus
        // may be sitting on, so "follow the tail" here also means the focus can never be left pointing at
        // something that no longer exists.
        public String DeleteLast()
        {
            if (Builder_Composing.Length > 0)
            {
                _uiFocusAuthoritative = -1;
                Builder_Composing.Length--;
            }
            return GetTranslatedText();
        }

        // Reset ALL per-composition focus state with the buffer. Clearing only the
        // UI-authoritative value is not enough, because with no UI focus established yet
        // (sentinel -1) EffectiveFocus() falls back to the WORKER's value, which would still hold the OLD
        // word's focus: old word "ka" resolved focus=1 → Clear → type "kak" →
        // InsertAtFocus("'") lands 'ka'k' instead of tail-default 'kak''. The worker snapshot baseline
        // is reset for the same reason (a new composition must classify as FRESH, not as a continuation
        // of the dead word).
        public void Clear()
        {
            // LINEARIZED with the worker's side effects. Separate
            // check-then-act guards are not enough: the worker could pass one, get preempted, and write
            // back or publish AFTER this method ran. Every worker side-effect
            // block takes _epochGate around {epoch check + effect}, and Clear takes the same lock around
            // {bump + reset}. Whichever side wins the lock, the loser observes a consistent world:
            // worker-first → its effects belong to the old epoch and are then fully reset here;
            // Clear-first → the worker's in-lock check sees the new epoch and drops the input.
            // A scheme the user picked mid-word takes effect here — the composition it would
            // have re-read is over, so there is nothing left to re-interpret.
            ApplyPendingSchemeSwitch();
            lock (_epochGate)
            {
                System.Threading.Interlocked.Increment(ref _compositionEpoch);
                Builder_Composing.Clear();
                _uiFocusAuthoritative = -1;
                _lastWorkerFocusResolved = -1;
                _lastWorkerSnapshot = string.Empty;
                // Per-composition SELECTION state dies with the composition. Without this call, a pick
                // from the word that just ended would survive Clear, and BuildCommitString for a next word
                // that starts with the same letter would return that pick (PruneStaleSelections keeps
                // same-position/same-origin picks, so a same-first-letter next word inherits the pick).
                ClearAllSelectionState();
            }
        }

        // Engine instance identity. GlobalState is process-static, so with two live
        // engines (for example two TextServices) BOTH could sit at epoch 0 and engine A's presenter
        // would happily consume engine B's frame: epoch alone cannot separate them.
        // Every frame is stamped with its producer; every consumer checks it.
        private static int _engineIdSeq;
        private readonly int _engineId = System.Threading.Interlocked.Increment(ref _engineIdSeq);
        public int EngineId => _engineId;

        /// <summary> Is this frame ours AND from the live composition? STRICT: treating
        /// owner/epoch &lt; 0 as a wildcard lets an unstamped frame pass every check, which is not owner
        /// validation at all. Every frame the worker publishes is
        /// stamped, so an unstamped one is by definition not ours.</summary>
        public bool IsFrameCurrent(Messaging.RenderFrame? frame)
            => frame != null
               && frame.OwnerEngineId == _engineId
               && frame.SourceEpoch == CurrentCompositionEpoch;

        /// <summary>
        /// Announce a published frame. Runs OUTSIDE _epochGate on purpose.
        ///
        /// Invoking the public event under the lock kills the product two ways: (a) a subscriber that
        /// throws propagates into the worker loop's
        /// outer catch and ENDS the one static worker for the whole process; (b) a handler that waits on
        /// another thread which is itself blocked on this lock deadlocks deterministically.
        ///
        /// So: no foreign code under the lock. Instead each subscriber is invoked individually, and
        ///   * the epoch is RE-CHECKED before every invocation, so if an earlier handler ended the
        ///     composition (for example by calling Clear() re-entrantly) the remaining handlers are not called;
        ///   * each invocation is isolated by try/catch, so one bad subscriber can neither kill the
        ///     worker nor stop the others.
        /// And the residual check→invoke window left by all of the above is
        /// closed by running this on the composition-owning thread (see the dispatcher hand-off at the
        /// top of the method). Delivery is not best-effort: consumers still validate, but they are not
        /// the only defence.
        /// </summary>
        internal void DeliverFrame(Messaging.RenderFrame frame, int epoch) => DeliverFrame(frame, epoch, DeliveryOrigin.Caller);

        internal void DeliverFrame(Messaging.RenderFrame frame, int epoch, DeliveryOrigin origin)
        {
            // STRONG GUARANTEE: a subscriber must never be
            // ENTERED for a composition that has already been cancelled.
            //
            // The check→invoke window cannot be closed on the worker thread, and closing it with a lock
            // held across foreign code lets a subscriber kill or deadlock the single static worker. So the dispatch is
            // instead handed to the thread that owns composition lifetime — the same thread that calls
            // Clear(). Once dispatch and Clear() are on one thread they cannot interleave at all, and the
            // epoch re-check below becomes exact rather than best-effort. No lock spans foreign code, and
            // nothing blocks waiting on a subscriber.
            // THE SUBSCRIBER LIST IS BOUND HERE, AT ENQUEUE, and not read again when the
            // queue runs. A queued lambda that captures only (frame, epoch) while the
            // consumer re-reads the live OnFrameReadyFrame lets a delivery queued under activation 1 and
            // drained after activation 2 subscribed bind itself to activation 2's delegates. Those
            // delegates carry the CURRENT generation, so the generation check passes; Deactivate does not
            // advance the composition epoch, so the owner/epoch check passes too. Binding the list to the
            // work item means an old delivery can only ever reach the subscribers that existed when it was
            // created — and each of those carries its own retired generation and rejects itself.
            var framedAtEnqueue = OnFrameReadyFrame;
            var legacyAtEnqueue = OnFrameReady;

            var dispatcher = System.Threading.Volatile.Read(ref _frameDeliveryDispatcher);
            if (dispatcher != null && !IsOnMyDeliveryThread())
            {
                try
                {
                    dispatcher(() => DeliverFrameOnOwningThread(frame, epoch, framedAtEnqueue, legacyAtEnqueue));
                    return;
                }
                catch (Exception)
                {
                    // NO RETRY. The dispatcher returns void, so a throw does not tell
                    // us whether it had already accepted the work. Retrying unconditionally
                    // could therefore enqueue the SAME delivery twice, i.e. deliver one frame's
                    // notification to subscribers twice. With an at-most-once contract the only safe
                    // reading of an exception is "outcome unknown", and the honest response is to drop:
                    // a missed frame costs one repaint, a duplicated notification is a correctness bug.
                    return;
                }
            }

            if (IsOnMyDeliveryThread())
            {
                // Already on the thread that owns composition lifetime — this IS the serialised path.
                DeliverFrameOnOwningThread(frame, epoch, framedAtEnqueue, legacyAtEnqueue);
                return;
            }

            // No dispatcher installed. Refuse ONLY when the caller is the shared
            // background worker — that is the thread whose interleaving with Clear() the strong rule
            // exists to prevent. Any other publisher is doing the work synchronously on its own thread,
            // where no cross-thread cancellation window exists. Refusing there would strand every frame
            // such a caller publishes before the presenter's window is created, or with no window at
            // all, which is a worse failure than the one being prevented.
            if (origin == DeliveryOrigin.Worker)
            {
                return;
            }
            DeliverFrameOnOwningThread(frame, epoch, framedAtEnqueue, legacyAtEnqueue);
        }

        // PER-ENGINE, not a process-global slot. With a public static single
        // slot, presenter 2's install silently overwrites presenter 1's, and presenter 1's teardown
        // then clears the slot out from under presenter 2, after which its deliveries silently revert
        // to the worker thread. Ownership is explicit: install returns nothing but records the owner
        // token, and uninstall is a no-op unless the caller presents that same token.
        private Action<Action>? _frameDeliveryDispatcher;
        private object? _dispatcherOwner;



        /// <summary> Route this engine's deliveries through <paramref name="dispatcher"/>,
        /// which must marshal onto the thread that owns composition lifetime for this engine. The
        /// <paramref name="owner"/> token is required to uninstall, so one presenter can neither replace
        /// nor tear down another's routing.</summary>
        public void InstallFrameDeliveryDispatcher(object owner, Action<Action> dispatcher)
        {
            lock (_dispatcherGate)
            {
                // A presenter's routing is protected from another presenter.
                if (_dispatcherOwner != null && !ReferenceEquals(_dispatcherOwner, owner))
                {
                    return;
                }
                _dispatcherOwner = owner;
                System.Threading.Volatile.Write(ref _frameDeliveryDispatcher, dispatcher);
            }
        }

        /// <summary> Remove routing installed by <paramref name="owner"/>. A non-owner's
        /// call is ignored, so a disposing presenter cannot strand a live one.</summary>
        public bool UninstallFrameDeliveryDispatcher(object owner)
        {
            lock (_dispatcherGate)
            {
                if (!ReferenceEquals(_dispatcherOwner, owner)) return false;
                _dispatcherOwner = null;
                System.Threading.Volatile.Write(ref _frameDeliveryDispatcher, null);
                return true;
            }
        }

        private readonly object _dispatcherGate = new();

        /// <summary> Set by the dispatcher while it is running a delivery, so a re-entrant
        /// DeliverFrame does not post to itself and deadlock behind its own queue.
        ///
        /// PER ENGINE, not a process-wide bool. With a plain
        /// <c>bool</c>, while engine A is running a delivery a re-entrant delivery on engine B sees
        /// "already on the delivery thread" and skips B's own dispatcher, so B's frame runs on A's
        /// thread. Holding the engine reference makes the question "am I on the delivery thread FOR THIS
        /// ENGINE", which is the only form of it that means anything.</summary>
        [ThreadStatic] private static ManjuEngine? _deliveringEngine;
        internal bool IsOnMyDeliveryThread() => ReferenceEquals(_deliveringEngine, this);

        /// <summary> Runs a queued delivery, marking this thread as THIS engine's delivery
        /// thread for the duration.</summary>
        public void RunDelivery(Action work)
        {
            var previous = _deliveringEngine;
            _deliveringEngine = this;
            try { work(); }
            catch (Exception) { }
            finally { _deliveringEngine = previous; }
        }

        private void DeliverFrameOnOwningThread(Messaging.RenderFrame frame, int epoch,
                                                Action<Messaging.RenderFrame>? framed, Action? legacy)
        {
            if (framed != null)
            {
                foreach (Delegate d in framed.GetInvocationList())
                {
                    if (CurrentCompositionEpoch != epoch)
                    {
                        return;
                    }
                    try { ((Action<Messaging.RenderFrame>)d)(frame); }
                    catch (Exception) { }
                }
            }


            // Bound at enqueue, same as above — not re-read here.
            if (legacy == null) return;
            foreach (Delegate d in legacy.GetInvocationList())
            {
                if (CurrentCompositionEpoch != epoch)
                {
                    return;
                }
                try { ((Action)d)(); }
                catch (Exception) { }
            }
        }

        // Monotonic composition generation. Bumped by Clear(); stamped into every enqueued
        // input AND into every published RenderFrame; enforced (a) by the worker inside _epochGate
        // around each side-effect block and (b) by consumers, which reject frames of a dead epoch.
        private int _compositionEpoch;
        internal readonly object _epochGate = new();
        public int CurrentCompositionEpoch => System.Threading.Volatile.Read(ref _compositionEpoch);



        // A pointer press on the IME's own no-activate surfaces (dictionary
        // window: WS_EX_NOACTIVATE + MA_NOACTIVATE) produces NO OnSetFocus(0) — the host keeps focus.
        // Those surfaces raise this so TextService can mark the Shift-tap tracker interrupted.
        public event Action? OnPointerInterruption;
        public void RaisePointerInterruption() => OnPointerInterruption?.Invoke();

        public String GetRawBuffer() => Builder_Composing.ToString();

        // FOCUS-AWARE INSERTION.
        //
        // AddInput only appends (StringBuilder.Append), and the caret / focus lives in the presenter.
        // Unless "the unit the user is looking at" is mapped back to a buffer offset, every
        // position-targeting action (the Shift transliteration mark, the real separator) silently lands
        // on the tail and never follows the focus.
        //
        // These helpers give the engine one shared, focus-correct insertion path. Every position-targeting
        // action (transliteration mark, real separator, any other symbol insertion) MUST go through them
        // instead of AddInput, otherwise it lands on the tail.

        /// <summary>Resolve the unit index the user is currently focused on, clamped to the live buffer.
        /// Returns -1 when there is no buffer (nothing to target).</summary>
        public int GetFocusUnitIndex()
        {
            var units = ParseSnapshotUnits(Builder_Composing.ToString());
            if (units.Count == 0) return -1;
            int focus = EffectiveFocus();   // user-synchronous focus, not the worker-lagged value
            if (focus < 0 || focus >= units.Count) return units.Count - 1; // sentinel/stale -> last unit
            return focus;
        }

        /// <summary>The raw-buffer character offset just AFTER the focused unit — i.e. where a mark or a
        /// separator that targets the focused unit must be inserted. Returns buffer length when there is
        /// no resolvable focus (degrades to plain append).</summary>
        public int GetFocusInsertOffset()
        {
            var units = ParseSnapshotUnits(Builder_Composing.ToString());
            if (units.Count == 0) return Builder_Composing.Length;
            int focus = EffectiveFocus();   // user-synchronous focus, not the worker-lagged value
            if (focus < 0 || focus >= units.Count) focus = units.Count - 1;
            return units[focus].snapshotStart + units[focus].snapshotLen;
        }

        /// <summary>The FOCUSED unit's key (the token as typed, e.g. "k", "ng", "k'"). Empty when none.
        /// This is what a transliteration mark must be validated against — NOT the tail character.</summary>
        public string GetFocusUnitKey()
        {
            var units = ParseSnapshotUnits(Builder_Composing.ToString());
            if (units.Count == 0) return string.Empty;
            int focus = EffectiveFocus();   // user-synchronous focus, not the worker-lagged value
            if (focus < 0 || focus >= units.Count) focus = units.Count - 1;
            return units[focus].key;
        }

        /// <summary>Insert text at the focused unit's trailing edge instead of appending to the tail.
        /// Returns true when it landed at a real focus position (false = degraded to append).</summary>
        public bool InsertAtFocus(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int at = GetFocusInsertOffset();
            bool focused = at < Builder_Composing.Length;
            if (at < 0 || at > Builder_Composing.Length) at = Builder_Composing.Length;
            Builder_Composing.Insert(at, text);
            return focused;
        }

        // THE TAIL-APPEND SIBLING OF InsertAtFocus, for marks that are their own item.
        //
        // The five Mongolian punctuation marks (U+1802 comma, U+1803 period,
        // U+1804 colon, U+1808 and U+1809) are ORDINARY INPUT — each becomes a unit of its own — so they
        // must land where ordinary input lands: at the tail. Going through InsertAtFocus, which
        // is for marks that ATTACH to an existing unit (the apostrophe adapter, the separator, the suffix
        // connector), puts them in the wrong place: with the focus resolving
        // to unit 0, typing a comma after a word drops it after the FIRST unit and re-cuts every unit
        // behind it, so the units the picks were keyed to no longer exist and the picks are lost.
        //
        // Focus is released for the same reason AddInput releases it and at the same point in the
        // tick — before the caller enqueues — so the mark's own frame is built for the tail directly.
        public void AppendAtTail(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _uiFocusAuthoritative = -1;
            Builder_Composing.Append(text);
        }

        // True if this char can begin (or be) a mapping key — i.e. it is a letter the
        // romanization framework actually uses. Auto-derives from the loaded keymap, so letters with
        // NO mapping (currently q/x/z) return false and the caller swallows them during composition
        // (an unused letter is not part of the input framework: pressing it shows nothing, so the input is not interrupted). The separator ' is accepted input.
        public bool IsAcceptedInputChar(Char c)
        {
            char lower = Char.ToLower(c);
            if (lower == '\'') return true;
            // Also the base letter of a key written with a diacritic (NFD), so that Möllendorff `ž`
            // can begin a word: z, then the Shift tap.
            foreach (var k in Dictionary_Mappings.Keys)
                if (k.Length > 0 && Char.ToLower(CanonicalDecomposition(k)[0]) == lower) return true;
            return false;
        }

        // Whether an apostrophe pressed NOW could serve as a syllable separator (breaks a
        // digraph: "n'g"->n+g) or an apostrophe-adapter (k'/g'/h'/r'), vs being a plain symbol. COEXISTENCE
        // rule (the separator wins) — keep ' ONLY when the preceding raw char is the first letter of some
        // multi-char mapping token (digraph-starter or apostrophe-adapter base), derived from the loaded
        // scheme so it stays correct across schemes (mirrors IsAcceptedInputChar). Plain → caller force-commits.
        //   false when: (1) buffer empty, (2) last char already ', (3) last char starts no multi-char token.
        public bool ApostropheCanSeparateOrAdapt()
        {
            string buf = GetRawBuffer();
            if (string.IsNullOrEmpty(buf)) return false;            // (1) nothing to attach to
            char last = Char.ToLower(buf[buf.Length - 1]);
            if (last == '\'')
            {
                // A 2nd consecutive ' is the DOUBLE-APOSTROPHE ESCAPE (r''h -> r+h, so arhat =
                // ar''hat) — KEEP it when the char before the 1st ' starts an apostrophe-token (r'/k'/g'/h'); the
                // tokenizer then breaks that token into letter + separator. A 3rd consecutive ' (buf ends "''") =
                // plain.
                if (buf.Length >= 2 && buf[buf.Length - 2] == '\'') return false;       // (2a) 3rd consecutive ' = plain
                if (buf.Length >= 2)
                {
                    char beforeApos = Char.ToLower(buf[buf.Length - 2]);
                    foreach (var k in Dictionary_Mappings.Keys)
                        if (k.Length >= 2 && k[k.Length - 1] == '\'' && Char.ToLower(k[0]) == beforeApos) return true;
                }
                return false;                                        // (2b) ' after a non-adapter = plain
            }
            foreach (var k in Dictionary_Mappings.Keys)             // (3) preceding letter must start a multi-char token
                if (k.Length > 1 && Char.ToLower(k[0]) == last) return true;
            return false;
        }

        // The engine holds NO dictionary-romanization map; dict
        // lookup transliterates composed code points inside the dictionary module (the dictionary's
        // own Romanizer). An engine-side Möllendorff map would hard-bind
        // the engine to Möllendorff.
        //
        // ON-SCREEN romanization is a SEPARATE, customisable layer: each unit's display string is
        // resolved by DisplayFormForKey, backed by the active scheme's OPTIONAL `display:` section
        // (token -> shown string). It is NOT hardwired to identity — identity is merely the DEFAULT
        // when a token has no `display:` entry (the case for all shipped schemes). The boundary keeps
        // on-screen romanization customisable per scheme: every shipped scheme mirrors the input, and
        // the `display:` section is the interface for changing that.
        private readonly Dictionary<String, String> Dictionary_Display = new Dictionary<String, String>();

        // Optional FIXED display romanization, selected by config
        // `display.scheme`. null => MIRROR the input scheme (what you type is what you see, token-based DisplayFormForKey).
        // Non-null => render each unit's code points through this romanizer (e.g. always Möllendorff /
        // Abkai), INDEPENDENT of what was typed. Loaded from Resource/Romanization/<scheme>.yaml.
        private CSharpTSFInput.Romanization.Romanizer? _displayRomanizer;

        /// <summary>Resolve `display.scheme`. "input"/empty => mirror (token-based). Any
        /// other value loads Resource/Romanization/&lt;scheme&gt;.yaml as a code-point→Latin romanizer.</summary>
        private void LoadDisplayScheme()
        {
            try
            {
                string scheme = ActiveDisplayScheme;   // override if cycled, else the config
                if (string.IsNullOrWhiteSpace(scheme) || scheme.Equals("input", StringComparison.OrdinalIgnoreCase))
                {
                    _displayRomanizer = null;
                    return;
                }
                string? dllDir = Path.GetDirectoryName(Globals.GetModulePath());
                if (dllDir == null) { _displayRomanizer = null; return; }
                string path = Path.Combine(dllDir, "Resource", "Romanization", scheme + ".yaml");
                var r = CSharpTSFInput.Romanization.Romanizer.Load(path, scheme);
                if (r.IsEmpty)
                {
                    _displayRomanizer = null;
                }
                else { _displayRomanizer = r; }
            }
            catch (Exception)
            {
                _displayRomanizer = null;
            }
        }

        /// <summary>On-screen romanization for ONE tokenizer key in MIRROR mode. Resolves
        /// via the active scheme's `display:` customisation table; falls back to the token AS TYPED
        /// (identity) when absent. Used only when display.scheme = input (the default).</summary>
        public string DisplayFormForKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            return Dictionary_Display.TryGetValue(key, out var shown) ? shown : key;
        }

        /// <summary> Replace a non-printing control char that would show as a BLANK
        /// composing row with a visible <c>&lt;NAME&gt;</c> label. Only the suffix connectors the IME can
        /// actually insert are named (MVS / NNBSP); ZWNJ never reaches here — it is a gap separator rendered
        /// as · by the caller, never a unit. Display-only; the real code point still commits non-printing.</summary>
        internal static string LabelControlChars(string disp)
        {
            if (string.IsNullOrEmpty(disp)) return disp;
            disp = disp.Replace(((char)0x180E).ToString(), "<MVS>");     // U+180E Mongolian Vowel Separator
            disp = disp.Replace(((char)0x202F).ToString(), "<NNBSP>");   // U+202F Narrow No-Break Space
            return disp;
        }


        // The REAL separator (the ' key = ZWNJ) must occupy its OWN
        // centered display row, not be folded onto the tail of the previous letter's row (the gap fold
        // does that). This returns the full prefix-grid ROW model:
        //   · one row per tokenized UNIT (UnitIndex = engine position, unchanged),
        //   · PLUS a standalone separator row for each gap that contains ≥1 ZWNJ (consecutive ZWNJ in one
        //     gap collapse to a single representative row).
        // The token-internal transliteration apostrophe (k'/g'/h'/r') stays attached to its letter (it is inside the unit's
        // token, tight). A standalone gap U+0027 (adapter that did not attach) also
        // stays tight on the previous unit. Only the ZWNJ separator becomes its own row.
        //
        // CRITICAL: separator rows carry UnitIndex = -1. They are display-only and NON-FOCUSABLE — the
        // presenter keeps _focusedRowIndex keyed to the ENGINE unit index and maps unit↔display-slot for
        // rendering / hit-test only, so pick / commit / candidate-matrix positions are untouched.
        public readonly struct PrefixDisplayRow
        {
            public readonly bool IsSeparator;
            public readonly string Display;
            public readonly int UnitIndex;       // -1 for a separator row
            public readonly int SnapshotStart;
            public readonly int SnapshotLen;
            public PrefixDisplayRow(bool isSeparator, string display, int unitIndex, int snapshotStart, int snapshotLen)
            { IsSeparator = isSeparator; Display = display; UnitIndex = unitIndex; SnapshotStart = snapshotStart; SnapshotLen = snapshotLen; }
        }

        public List<PrefixDisplayRow> GetPrefixDisplayRows(string snapshot)
        {
            var rows = new List<PrefixDisplayRow>();
            if (string.IsNullOrEmpty(snapshot)) return rows;
            var parsed = ParseSnapshotUnits(snapshot);
            if (parsed.Count == 0) return rows;

            string sep = SeparatorDisplayChar.ToString();
            char adp = ApostropheDisplayChar;
            // The connector's own row glyph, cycleable like the separator's and defaulting to a vertical
            // stroke (an arc such as U+2040 reads badly across the top of a stacked column). Whichever
            // the user picks, it can never equal a separator shape (see the invariant on ConnectorShapes).
            string conn = ConnectorDisplayChar.ToString();

            bool GapHasSeparator(int from, int to)
            {
                for (int gi = from; gi < to && gi < snapshot.Length; gi++)
                    if (snapshot[gi] == SeparatorMarker) return true;
                return false;
            }
            // Same shape as GapHasSeparator: consecutive connectors in one gap collapse to a
            // single representative row, per the rule for the separator.
            bool GapHasConnector(int from, int to)
            {
                for (int gi = from; gi < to && gi < snapshot.Length; gi++)
                    if (IsSuffixConnectorChar(snapshot[gi])) return true;
                return false;
            }
            string GapAdapters(int from, int to)   // standalone U+0027 in gap stays tight on prev unit
            {
                var sb = new StringBuilder();
                for (int gi = from; gi < to && gi < snapshot.Length; gi++)
                    if (snapshot[gi] == '\'') sb.Append(adp);
                return sb.ToString();
            }

            // Leading gap before unit 0 (rare — the ' / right-Shift both need a preceding letter).
            if (parsed[0].snapshotStart > 0 && GapHasSeparator(0, parsed[0].snapshotStart))
                rows.Add(new PrefixDisplayRow(true, sep, -1, 0, 0));
            if (parsed[0].snapshotStart > 0 && GapHasConnector(0, parsed[0].snapshotStart))
                rows.Add(new PrefixDisplayRow(true, conn, -1, 0, 0));

            for (int ui = 0; ui < parsed.Count; ui++)
            {
                var u = parsed[ui];
                string disp = _displayRomanizer != null
                    ? _displayRomanizer.Transliterate(u.primary)
                    : DisplayFormForKey(u.key);
                disp = LabelControlChars(disp);
                if (disp.IndexOf('\'') >= 0) disp = disp.Replace('\'', adp);   // token-internal transliteration apostrophe, tight

                int gapStart = u.snapshotStart + u.snapshotLen;
                int gapEnd = (ui + 1 < parsed.Count) ? parsed[ui + 1].snapshotStart : snapshot.Length;
                disp += GapAdapters(gapStart, gapEnd);   // gap adapter stays tight; separator does NOT
                if (ui == 0 && u.snapshotStart > 0)
                    disp = GapAdapters(0, u.snapshotStart) + disp;

                rows.Add(new PrefixDisplayRow(false, disp, ui, u.snapshotStart, u.snapshotLen));

                if (GapHasSeparator(gapStart, gapEnd))
                    rows.Add(new PrefixDisplayRow(true, sep, -1, gapStart, gapEnd - gapStart));
                // UnitIndex = -1 is the whole point: the presenter keys _focusedRowIndex to
                // the ENGINE unit index, so a -1 row is display-only and the arrow keys walk straight past
                // it. Same contract as the separator row above.
                if (GapHasConnector(gapStart, gapEnd))
                    rows.Add(new PrefixDisplayRow(true, conn, -1, gapStart, gapEnd - gapStart));
            }
            return rows;
        }

        public String GetTranslatedText() => GetTranslatedTextFromSnapshot(Builder_Composing.ToString());

        public String GetTranslatedTextFromSnapshot(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            StringBuilder Builder_Result = new StringBuilder();
            Int32 Index_Scan = 0;
            while (Index_Scan < input.Length)
            {
                // The ' key separator (ZWNJ): no glyph, breaks the greedy span, never adapts.
                if (input[Index_Scan] == SeparatorMarker) { Index_Scan++; continue; }
                // U+0027 (right Shift): separator / adapter-completer (n'g stays n+g; k'→ᠺ).
                if (input[Index_Scan] == '\'') { Index_Scan++; continue; }
                // Double-apostrophe escape (see IsDoubleApostropheEscape): letter + '' -> take
                // letter alone, '' separates → r''h = r+h (arhat = ar''hat); r'/rh still ᡰ. Before greedy match.
                if (IsDoubleApostropheEscape(input, Index_Scan))
                {
                    string esc = input[Index_Scan].ToString();
                    Builder_Result.Append(Dictionary_Mappings.ContainsKey(esc) ? Dictionary_Mappings[esc] : esc);
                    Index_Scan++;
                    continue;
                }
                // Greedy longest-match: 4-char ("dzhy") before 3-char before 2-char before 1-char.
                if (Index_Scan + 3 < input.Length)
                {
                    String String_Quad = input.Substring(Index_Scan, 4);
                    if (Dictionary_Mappings.ContainsKey(String_Quad))
                    {
                        Builder_Result.Append(Dictionary_Mappings[String_Quad]);
                        Index_Scan += 4;
                        continue;
                    }
                }
                if (Index_Scan + 2 < input.Length)
                {
                    String String_Tri = input.Substring(Index_Scan, 3);
                    if (Dictionary_Mappings.ContainsKey(String_Tri))
                    {
                        Builder_Result.Append(Dictionary_Mappings[String_Tri]);
                        Index_Scan += 3;
                        continue;
                    }
                }
                if (Index_Scan + 1 < input.Length)
                {
                    String String_Dual = input.Substring(Index_Scan, 2);
                    if (Dictionary_Mappings.ContainsKey(String_Dual))
                    {
                        Builder_Result.Append(Dictionary_Mappings[String_Dual]);
                        Index_Scan += 2;
                        continue;
                    }
                }
                String String_Single = input[Index_Scan].ToString();
                Builder_Result.Append(Dictionary_Mappings.ContainsKey(String_Single) ? Dictionary_Mappings[String_Single] : String_Single);
                Index_Scan++;
            }
            return Builder_Result.ToString();
        }

        // Extract the last non-FVS letter from a display text and format as metadata suffix.
        //   If the last-letter codepoint has a meaning entry → return that meaning (e.g. "Mongolian A")
        //   Else → return "U+XXXX" hex form
        //   If displayText empty / no valid letter → return empty string
        // FVS codepoints skipped: U+180B / U+180C / U+180D / U+180E / U+180F
        // Used by BuildCandidateMatrix to append "<type> · <meaning>" to semantic names so
        // same-glyph-different-codepoint candidates are distinguishable by reading the metadata row.
        private string GetLastLetterMeaningSuffix(string displayText)
        {
            if (string.IsNullOrEmpty(displayText)) return string.Empty;
            int codepoint = 0;
            // Walk from end, skipping FVS / MVS
            for (int i = displayText.Length - 1; i >= 0; i--)
            {
                char c = displayText[i];
                int cp;
                // Handle surrogate pairs walking backward (unlikely in Mongolian block but safe)
                if (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(displayText[i - 1]))
                {
                    cp = char.ConvertToUtf32(displayText[i - 1], c);
                    i--; // consume high
                }
                else
                {
                    cp = c;
                }
                if (cp >= 0x180B && cp <= 0x180F) continue; // FVS1-4 + MVS
                codepoint = cp;
                break;
            }
            if (codepoint == 0) return string.Empty;
            string cpStr = char.ConvertFromUtf32(codepoint);
            // g/h/k grouping note (a toggle on how the display is worded). Prepended to the meaning suffix
            // for the velar family so the card description frames native vs loan velars through the
            // chosen lens. See GetGhkGroupingNote.
            string ghk = GetGhkGroupingNote(codepoint);
            string baseMeaning = (Dictionary_Meanings.TryGetValue(cpStr, out string? meaning) && !string.IsNullOrEmpty(meaning))
                ? meaning
                : $"U+{codepoint:X4}";
            return string.IsNullOrEmpty(ghk) ? baseMeaning : $"{ghk} · {baseMeaning}";
        }

        // g/h/k description grouping: wording of the display only, input rules unchanged, because
        // this is presentation and not composition. Two lenses, switched by config
        // `appearance.ghk_grouping`:
        //   "loanword" (default) - native velars = ordinary Manchu letters (no extra note); the loan
        //                          velars ᠺᡬᡭ carry a loanword-layer tag = a borrowed, supplementary layer.
        //   "phonology"          - native + loan velars are ONE velar system: native ᡴᡤᡥ noted as inherited (following vowel harmony),
        //                          loan ᠺᡬᡭ noted as supplementary velars. Frames them as one phonological system.
        // Each lens has its own strings, so the toggle has a real, visible effect. Translatable, no
        // colloquialism.
        private string _ghkGrouping = "loanword";
        private string GetGhkGroupingNote(int codepoint)
        {
            bool phon = string.Equals(_ghkGrouping, "phonology", StringComparison.OrdinalIgnoreCase);
            switch (codepoint)
            {
                // native velars ᡴ(k) ᡤ(g) ᡥ(h)
                // Unlike initial / medial / final / isolated (the standard English positional-form
                // terms), these labels are THIS PROJECT'S OWN framing of the g/h/k grouping, not
                // received terminology:
                //   native (harmony-dependent)   native to Manchu; which of the pair appears follows
                //                                vowel harmony
                //   supplementary                the added loan-velar series
                //   loanword                     the layer they belong to
                // Full words only, no abbreviations.
                case 0x1874: return phon ? "velar · native (harmony-dependent)" : string.Empty;
                case 0x1864: return phon ? "velar · native (harmony-dependent)" : string.Empty;
                case 0x1865: return phon ? "velar · native (harmony-dependent)" : string.Empty;
                // loan velars ᠺ(k') ᡬ(g') ᡭ(h')
                case 0x183A: return phon ? "velar · supplementary" : "loanword";
                case 0x186C: return phon ? "velar · supplementary" : "loanword";
                case 0x186D: return phon ? "velar · supplementary" : "loanword";
                default: return string.Empty;
            }
        }

        // An empty-rime unit = a loan-syllable sibilant base + the apical vowel (ᡟ U+185F for
        // ci/si, ᡳ U+1873 for zi/chi/zhi/shi/ri). The base form is a fixed single glyph,
        // so the candidate only varies the tail, and the description must reflect that.
        private static bool IsEmptyRimeUnit(string cp)
        {
            if (string.IsNullOrEmpty(cp) || cp.Length < 2) return false;
            const string sibilantBases = "ᡮᠰᡯᡱᡷᡧᡰ"; // ᡮ ᠰ ᡯ ᡱ ᡷ ᡧ ᡰ
            return sibilantBases.IndexOf(cp[0]) >= 0 && (cp[1] == 'ᡟ' || cp[1] == 'ᡳ'); // + ᡟ / ᡳ
        }

        // Compute prefix-slice transliteration bases for a snapshot at a given
        // focus unit index. Returns the rendered string of prefix[0..focusUnit] where the focus unit's
        // codepoint takes the primary OR each declared alternative.
        //
        // - focusUnitIndex < 0  → sentinel "last unit" (the default, full input + alternatives at last)
        // - focusUnitIndex = k  → prefix-slice ending at unit k; alternatives expand at unit k
        //
        // Earlier units (before focus) always use their primary codepoint; units after focus are
        // dropped entirely (prefix-slice semantics).
        // When focusUnitIndex resolves to the last unit, the behaviour is the sentinel case above.
        public List<string> GetTranslationBasesFromSnapshot(string input, int focusUnitIndex = -1)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(input)) return result;

            // Use shared O(n) parser.
            var units = ParseSnapshotUnits(input);
            if (units.Count == 0) return result;

            // Sentinel resolution aligned with presenter: → last position.
            int focus = ResolveFocusUnitSentinel(focusUnitIndex, units.Count);

            // Iterative O(n) prefix; no recursion into candidate gen.
            string prefixStr = ComputePrefixWithLocksOrSelections(input, focus);

            string focusKey = units[focus].key;
            string focusPrimary = units[focus].primary;

            // At focus position: the candidate window always shows full alternatives
            // (primary + Dictionary_Alternatives + FVS). The user's current selection (if any) is recorded
            // in _selectedPositions / _positionHighlightIndex separately for UI rendering and commit;
            // the candidate list itself is NOT trimmed because user may want to RE-select.
            result.Add(prefixStr + focusPrimary);
            if (Dictionary_Alternatives.TryGetValue(focusKey, out var alts))
            {
                foreach (var alt in alts)
                {
                    string combo = prefixStr + alt;
                    if (!result.Contains(combo)) result.Add(combo);
                }
            }
            return result;
        }

        // Public wrapper for backward compatibility. Routes to ComputePrefixWithLocksOrSelections, which
        // consults the _selectedPositions + _positionHighlightIndex state.
        public string ComputePrefixWithLocks(string snapshot, int focusUnitIndex)
        {
            return ComputePrefixWithLocksOrSelections(snapshot, focusUnitIndex);
        }

        // Resolve the codepoint actually displayed at unit position i.
        //
        // Prefer the stored chosen slice when present (captured at pick time from
        // CandidateMatrix.Cells[i][hl].CodepointString; see MarkPositionSelected 5-arg). This is
        // CORRECT under FVS variants and collapse, both of which break the legacy
        // alts[hl-1] reconstruction, so a picked FVS variant is respected. Fallback to legacy path only
        // for legacy 3-arg/2-arg MarkPositionSelected callers.
        private string ResolveCurrentCodepointAtPosition(
            List<(string key, string primary, int snapshotStart, int snapshotLen)> units,
            int i)
        {
            if (i < 0 || i >= units.Count) return string.Empty;
            if (!_selectedPositions.Contains(i)) return units[i].primary;
            // Prefer the chosen slice captured at pick time. SAFE under FVS + collapse.
            if (_positionChosenCodepointSlice.TryGetValue(i, out var storedSlice) && !string.IsNullOrEmpty(storedSlice))
            {
                return storedSlice;
            }
            // No stored slice: reconstruct via alts[hl-1]. UNSAFE if FVS variants
            // exist for this key or the collapse dropped entries. This path serves the
            // legacy 3-arg / 2-arg MarkPositionSelected callers, which store no slice.
            int hl = GetPositionHighlight(i);
            if (hl <= 0) return units[i].primary;
            if (Dictionary_Alternatives.TryGetValue(units[i].key, out var alts))
            {
                int altIdx = hl - 1; // hl=1 maps to alts[0]
                if (altIdx >= 0 && altIdx < alts.Count) return alts[altIdx];
            }
            return units[i].primary;
        }

        // Build the per-position candidate matrix in one pass.
        //
        // Algorithm:
        //   1. Parse units (digraph-aware).
        //   2. Resolve each position's "currently-displayed" codepoint based on _selectedPositions /
        //      _positionHighlightIndex (primary for unselected, picked alt for selected).
        //   3. For each position k, enumerate the candidate codepoint set:
        //         primary + Dictionary_Alternatives[unit[k].key] + (FVS variants per base)
        //   4. For each candidate at position k, construct the hypothetical full input string by
        //      replacing position k's codepoint with this candidate. Shape via FullShape on the
        //      WHOLE hypothetical input — this is the crucial step that gives HarfBuzz enough
        //      context to apply initial/medial/final positional substitution correctly.
        //   5. Trim shaped glyphs to glyphs 0..k (the "up-to-this-position" view) for display.
        //   6. same-as-bare collapse applies per-cell (drop FVS variants whose shaped form
        //      matches the bare's shaped form for THIS position).
        //
        // Performance: N positions × ~M alts each × 1 full-input shape = N*M shape calls per
        // matrix build (80 shapes for N=20, M=4). Triggered on snapshot change only, not
        // on focus change (focus change is a cheap matrix-row lookup).
        public CandidateMatrix BuildCandidateMatrix(string snapshot)
        {
            var units = ParseSnapshotUnits(snapshot);
            int rowCount = units.Count;
            var cells = new List<List<MatrixCell>>(rowCount);

            if (rowCount == 0)
            {
                return new CandidateMatrix(0, cells, 1000);
            }

            // Snapshot of font + upem for shaping. Worker may have already loaded this; we just
            // need a single read here.
            byte[]? fontData = Buffer_FontData;
            if (fontData == null)
            {
                // No font → return empty cells. Downstream paint handles empty list gracefully.
                for (int k = 0; k < rowCount; k++) cells.Add(new List<MatrixCell>());
                return new CandidateMatrix(rowCount, cells, 1000);
            }

            ushort upem = 1000;
            unsafe
            {
                fixed (byte* pFont = fontData)
                {
                    try
                    {
                        var upemReader = new ManjuFontReader(pFont, fontData.Length);
                        upem = upemReader.GetUnitsPerEm();
                    }
                    catch { upem = 1000; }
                }
            }

            for (int k = 0; k < rowCount; k++)
            {
                var rowCells = new List<MatrixCell>();
                cells.Add(rowCells);

                // Candidate codepoint set at position k.
                var candidateCodepointsAtK = new List<(string codepoints, string label)>();
                string primaryAtK = units[k].primary;
                candidateCodepointsAtK.Add((primaryAtK, "primary"));
                if (Dictionary_Alternatives.TryGetValue(units[k].key, out var alts))
                {
                    int altIdx = 0;
                    foreach (var alt in alts)
                    {
                        candidateCodepointsAtK.Add((alt, $"alt{altIdx}"));
                        altIdx++;
                    }
                }
                // FVS variants for each base.
                int baseCount = candidateCodepointsAtK.Count;
                for (int b = 0; b < baseCount; b++)
                {
                    var baseEntry = candidateCodepointsAtK[b];
                    if (baseEntry.codepoints.Length == 0) continue;
                    char lastChar = baseEntry.codepoints[baseEntry.codepoints.Length - 1];
                    if (lastChar >= 'ᠠ' && lastChar <= '᢯')
                    {
                        // FVS1-4 = U+180B/180C/180D/180F (U+180E is MVS, not an FVS).
                        // FVS4 (U+180F) is included. The same-as-bare collapse downstream drops
                        // whichever variant yields no distinct glyph in the current font, so including FVS4 only
                        // surfaces a candidate where the font actually has a 4th form.
                        string[] fvsVariants = { "᠋", "᠌", "᠍", "᠏" };
                        string[] fvsLabels = { "FVS1", "FVS2", "FVS3", "FVS4" };
                        for (int v = 0; v < fvsVariants.Length; v++)
                        {
                            candidateCodepointsAtK.Add((baseEntry.codepoints + fvsVariants[v],
                                $"{baseEntry.label}+{fvsLabels[v]}"));
                        }
                    }
                }

                // Whether the PRIMARY (default) candidate at k fuses across a unit
                // boundary — recorded from its own iteration (it is always first); later candidates at the same
                // position that do NOT fuse are then the explicit separated-form opt-outs and get labeled so.
                bool primaryFusesAtK = false;

                // For each candidate at position k, build hypothetical full input + shape + trim.
                foreach (var cand in candidateCodepointsAtK)
                {
                    // Construct hypothetical full input: each position uses its
                    // currently-displayed codepoint, EXCEPT position k uses this candidate.
                    var hypothetical = new StringBuilder();
                    int endOffsetK = 0;   // UTF-16 length of units 0..k in hypotheticalString → cluster cutoff
                    for (int j = 0; j < rowCount; j++)
                    {
                        string part = (j == k) ? cand.codepoints : ResolveCurrentCodepointAtPosition(units, j);
                        hypothetical.Append(part);
                        if (j <= k) endOffsetK += part.Length;
                    }
                    string hypotheticalString = hypothetical.ToString();

                    // Shape full hypothetical via HarfBuzz. This is where joining-form rules fire.
                    List<ManjuShaperCore.FinalGlyph> fullShaped;
                    unsafe
                    {
                        fixed (byte* pFont = fontData)
                        {
                            try
                            {
                                fullShaped = ManjuShaperCore.FullShape(hypotheticalString, pFont, fontData.Length, isVertical: true);
                            }
                            catch
                            {
                                fullShaped = new List<ManjuShaperCore.FinalGlyph>();
                            }
                        }
                    }

                    // Trim to units 0..k by CLUSTER, not glyph index. Trimming by glyph count
                    // (Math.Min(k+1, count)) assumes 1 glyph per unit and drops the 2nd glyph of any
                    // multi-glyph unit: the empty-rime unit (ᡮᡟ = [473@0, 385@1]) would lose its tail at a
                    // 1-unit snapshot, and when a letter follows,
                    // the tail would fall into the NEXT position's slice while that letter's own glyph is
                    // dropped. FinalGlyph.Cluster is the source
                    // UTF-16 index, so keep every glyph whose
                    // cluster lies within units 0..k (< endOffsetK): a multi-glyph unit keeps ALL its
                    // glyphs at its own position regardless of any glyph/codepoint count mismatch.
                    var trimmedShaped = new List<ManjuShaperCore.FinalGlyph>();
                    foreach (var g in fullShaped)
                    {
                        if (g.Cluster < (uint)endOffsetK) trimmedShaped.Add(g);
                    }

                    // Build codepoint display string: codepoints 0..k (using current selections +
                    // this candidate at k).
                    var displayCodepoints = new StringBuilder();
                    for (int j = 0; j <= k && j < rowCount; j++)
                    {
                        if (j == k) displayCodepoints.Append(cand.codepoints);
                        else displayCodepoints.Append(ResolveCurrentCodepointAtPosition(units, j));
                    }
                    string codepointString = displayCodepoints.ToString();

                    // Semantic item for this cell.
                    var codePointSeq = SemanticCandidateItem.GenerateCodePointSequence(codepointString);
                    var variantLabel = SemanticCandidateItem.GenerateVariantLabel(codepointString);
                    // Resolve the kind FIRST so the semantic name reflects the variant (showing
                    // its FVS number for debugging) instead of always being generated as "base".
                    var candKind = cand.label.Contains("FVS") ? CandidateKind.FvsVariant : CandidateKind.BaseTransliteration;
                    // Append the focus letter's meaning/codepoint suffix (· …) to the core label
                    // ("base" / "variant FVSn") so candidates sharing a glyph but differing in codepoint stay distinguishable
                    // and the LIVE description is precise + consistent. BuildCandidateMatrix is the single label
                    // source.
                    var coreName = SemanticCandidateItem.GenerateSemanticName(candKind, codepointString, variantLabel);
                    // Row 2 is **specialist data** (of a piece
                    // with row 3's Unicode ground truth), not explanatory plain language. Format = `mechanism · positional class`:
                    //   mechanism: ligature b+u (a ligature, components in word order) / separated FVSn (the split variant at a fusing position) / base / variant FVSn;
                    //   positional class: initial / medial / final / isolated — the standard palaeographic terms, computed from the real
                    //   joining context of a hypothetical whole-word shaping of that candidate (can the neighbours at either end of the glyph's run join? MVS and whitespace break the join, FVS is transparent).
                    // Fusion is decided on render evidence, without exception: does the glyph containing this unit's start cross the unit boundary (in either direction)?
                    // The empty-rime override below takes precedence; the letter-meaning suffix (· meaning / U+xxxx) is appended.
                    int startK = endOffsetK - cand.codepoints.Length;
                    bool fusesAcrossBoundary = false, fusedForward = false;
                    int runStart = startK, runEnd = endOffsetK;
                    for (int gi = 0; gi < fullShaped.Count; gi++)
                    {
                        int cl = (int)fullShaped[gi].Cluster;
                        int nx = (gi + 1 < fullShaped.Count) ? (int)fullShaped[gi + 1].Cluster : hypotheticalString.Length;
                        if (cl <= startK && startK < nx)
                        {
                            fusedForward = nx > endOffsetK;
                            fusesAcrossBoundary = fusedForward || (cl < startK);
                            runStart = Math.Min(cl, startK); runEnd = Math.Max(nx, endOffsetK);
                            break;
                        }
                    }
                    if (cand.label == "primary") primaryFusesAtK = fusesAcrossBoundary;
                    // Positional class: can the neighbours at either end of the run join? (Transparent FVS is skipped; MVS U+180E, whitespace and non-Mongolian characters break it.)
                    static bool JoinsAt(string s, int idx, int step)
                    {
                        for (int i = idx; i >= 0 && i < s.Length; i += step)
                        {
                            char c = s[i];
                            if (c >= '᠋' && c <= '᠍' || c == '᠏') continue;   // FVS is transparent
                            return c >= 'ᠠ' && c <= 'ᢪ';                            // a letter joins; MVS, whitespace and anything else break
                        }
                        return false;
                    }
                    bool joinL = JoinsAt(hypotheticalString, runStart - 1, -1);
                    bool joinR = JoinsAt(hypotheticalString, runEnd, +1);
                    // These four are the STANDARD English names for the
                    // Mongolian/Manchu positional forms: initial / medial / final / isolated are what the
                    // Unicode standard and the grammars use.
                    string formClass = joinL ? (joinR ? "medial" : "final") : (joinR ? "initial" : "isolated");
                    if (fusesAcrossBoundary)
                    {
                        int partnerK = fusedForward ? k + 1 : k - 1;
                        string selfLatin = DisplayFormForKey(units[k].key);
                        string partnerLatin = (partnerK >= 0 && partnerK < rowCount) ? DisplayFormForKey(units[partnerK].key) : "?";
                        string compo = fusedForward ? $"{selfLatin}+{partnerLatin}" : $"{partnerLatin}+{selfLatin}";
                        // ligature: the term for two letters shaped as one joined glyph.
                        coreName = string.IsNullOrEmpty(variantLabel) ? $"ligature {compo} · {formClass}" : $"ligature {compo} {variantLabel} · {formClass}";
                    }
                    else if (primaryFusesAtK)
                        // separated: the same pair drawn as its components instead.
                        coreName = string.IsNullOrEmpty(variantLabel) ? $"separated · {formClass}" : $"separated {variantLabel} · {formClass}";
                    else
                        coreName = $"{coreName} · {formClass}";
                    string letterSuffix = GetLastLetterMeaningSuffix(cand.codepoints);
                    var semanticName = string.IsNullOrEmpty(letterSuffix) ? coreName : $"{coreName} · {letterSuffix}";
                    // Empty-rime candidate: a whole apical-vowel syllable,
                    // one indivisible unit, whose initial-consonant base is a fixed single glyph auto-
                    // determined by the -y spelling; the apical-vowel tail (ᡟ U+185F ci/si, ᡳ U+1873 others)
                    // is what varies. The label is "apical vowel", the standard phonology term, and not
                    // the colloquial "little tail". One wording for ALL 7 empty-rime syllables,
                    // translatable, each term mapping 1:1 (empty rime, consonant-locked base,
                    // apical vowel), with nothing that only works in one language. Describes BOTH the locked base and the tail.
                    //
                    // A " · " joins the suffix ("apical vowelbase" would not read), the same separator every
                    // other part of this name uses.
                    if (IsEmptyRimeUnit(cand.codepoints))
                        semanticName = "empty rime · consonant-locked base · apical vowel · " +
                            (candKind == CandidateKind.FvsVariant && !string.IsNullOrEmpty(variantLabel) ? variantLabel : "base");
                    var semanticItem = new SemanticCandidateItem(
                        codepointString,
                        codePointSeq,
                        candKind,
                        variantLabel,
                        semanticName);

                    // cand.codepoints IS the position-k slice as
                    // assembled by candidateCodepointsAtK construction above. Pass it explicitly
                    // into the cell so presenter never has to reverse-engineer slice from
                    // CodepointString via shared-prefix subtraction (which breaks for FVS).
                    rowCells.Add(new MatrixCell(codepointString, cand.codepoints, trimmedShaped, semanticItem));
                }
            }

            return new CandidateMatrix(rowCount, cells, upem);
        }

        public List<String> GetCandidatesFromSnapshot(string snapshot, int focusUnitIndex = -1)
        {
            if (String.IsNullOrEmpty(snapshot))
            {
                return new List<String>();
            }

            List<String> List_Candidates = new List<String>();
            List<String> List_DictMatches = LegacyDictSearchEmptyShim(snapshot);
            List_Candidates.AddRange(List_DictMatches);

            // Iterate prefix-slice transliteration bases at focus position
            // (focusUnitIndex < 0 = last unit). Each base contributes its own
            // bare + FVS variants. FVS attaches to the LAST char of base, which IS the focus
            // character under prefix-slice semantics.
            var bases = GetTranslationBasesFromSnapshot(snapshot, focusUnitIndex);
            foreach (string String_Base in bases)
            {
                if (String.IsNullOrEmpty(String_Base)) continue;
                if (!List_Candidates.Contains(String_Base)) List_Candidates.Add(String_Base);
                // FVS variant generation (per base)
                Char Char_Last = String_Base.Last();
                if (Char_Last >= '\u1820' && Char_Last <= '\u18AF')
                {
                    String[] Array_Variants = { "\u180B", "\u180C", "\u180D" };
                    foreach (String String_Variant in Array_Variants)
                    {
                        String String_VariantCombo = String_Base + String_Variant;
                        if (!List_Candidates.Contains(String_VariantCombo)) List_Candidates.Add(String_VariantCombo);
                    }
                }
            }
            string firstCand = List_Candidates.Count > 0 ? List_Candidates[0] : "";
            int firstLen = firstCand.Length;
            int sampleTake = Math.Min(30, firstLen);
            var sb = new StringBuilder();
            for (int i = 0; i < sampleTake; i++) sb.Append($"U+{(int)firstCand[i]:X4} ");
            return List_Candidates;
        }


        // Compares two shaped-glyph sequences for identity — equal means the FVS variant
        // produced the same glyph output as the bare candidate (i.e. the FVS selector has no visible effect
        // on this letter + position combination, so the metadata label should read "same as base" rather than "variant").
        // Comparison uses glyph ID only; position fields (advance, offsets) are secondary and unlikely to
        // differ without a glyph-ID change in practice.
        private static bool GlyphSequencesEqual(
            List<ManjuShaperCore.FinalGlyph> a,
            List<ManjuShaperCore.FinalGlyph> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].GlyphId != b[i].GlyphId) return false;
            }
            return true;
        }
    }
}
