namespace Copaste
{
    using Colossal.IO.AssetDatabase;
    using Game.Input;
    using Game.Modding;
    using Game.Settings;

    public enum PanelThemeOption
    {
        Copaste = 0,
        Vanilla = 1,
    }

    public enum ModLanguageOption
    {
        Auto = 0,
        English = 1,
        German = 2,
        French = 3,
        Serbian = 4,
    }

    [FileLocation("ModsSettings/Copaste/Copaste")]
    [SettingsUITabOrder(kGeneralTab, kKeybindingsTab)]
    [SettingsUIGroupOrder(kBehaviorGroup, kPanelGroup, kLimitsGroup, kToolGroup, kClipboardGroup, kEditingGroup, kNudgeGroup, kAlignGroup)]
    [SettingsUIShowGroupName(kBehaviorGroup, kPanelGroup, kLimitsGroup, kToolGroup, kClipboardGroup, kEditingGroup, kNudgeGroup, kAlignGroup)]
    public class CopasteSettings : ModSetting
    {
        public const string kGeneralTab = "General";
        public const string kKeybindingsTab = "Keybindings";
        public const string kBehaviorGroup = "Behavior";

        // Odvojene grupe = separatori u Options: panel-izgled iza Anarchy,
        // limiti iza Text size (grupe bez naslova daju čist razmak).
        public const string kPanelGroup = "PanelOptions";
        public const string kLimitsGroup = "Limits";
        public const string kToolGroup = "ToolKeys";
        public const string kClipboardGroup = "ClipboardKeys";
        public const string kEditingGroup = "EditingKeys";
        public const string kNudgeGroup = "NudgeKeys";
        public const string kAlignGroup = "AlignKeys";

        public const string kToggleAction = "CopasteToggle";
        public const string kCopyAction = "CopasteCopy";
        public const string kPasteAction = "CopastePaste";
        public const string kDeleteAction = "CopasteDelete";
        public const string kRaiseAction = "CopasteRaise";
        public const string kLowerAction = "CopasteLower";
        public const string kUndoAction = "CopasteUndo";
        public const string kRedoAction = "CopasteRedo";
        public const string kRelocateAction = "CopasteRelocate";
        public const string kSelectSameAction = "CopasteSelectSame";
        public const string kSnapGroundAction = "CopasteSnapGround";
        public const string kMatchHeightAction = "CopasteMatchHeight";
        public const string kNudgeUpAction = "CopasteNudgeUp";
        public const string kNudgeDownAction = "CopasteNudgeDown";
        public const string kNudgeLeftAction = "CopasteNudgeLeft";
        public const string kNudgeRightAction = "CopasteNudgeRight";
        public const string kAlignGapPlusAction = "CopasteAlignGapPlus";
        public const string kAlignGapMinusAction = "CopasteAlignGapMinus";

        public CopasteSettings(IMod mod)
            : base(mod)
        {
        }

        [SettingsUIKeyboardBinding(BindingKeyboard.C, kToggleAction, ctrl: true, shift: true)]
        [SettingsUISection(kKeybindingsTab, kToolGroup)]
        public ProxyBinding ToggleBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.C, kCopyAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kClipboardGroup)]
        public ProxyBinding CopyBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.V, kPasteAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kClipboardGroup)]
        public ProxyBinding PasteBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Delete, kDeleteAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding DeleteBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.PageUp, kRaiseAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding RaiseBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.PageDown, kLowerAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding LowerBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Z, kUndoAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding UndoBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Y, kRedoAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding RedoBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Tab, kRelocateAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding RelocateBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.T, kSelectSameAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding SelectSameBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.End, kSnapGroundAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding SnapGroundBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Home, kMatchHeightAction)]
        [SettingsUISection(kKeybindingsTab, kEditingGroup)]
        public ProxyBinding MatchHeightBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.UpArrow, kNudgeUpAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kNudgeGroup)]
        public ProxyBinding NudgeUpBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.DownArrow, kNudgeDownAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kNudgeGroup)]
        public ProxyBinding NudgeDownBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.LeftArrow, kNudgeLeftAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kNudgeGroup)]
        public ProxyBinding NudgeLeftBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.RightArrow, kNudgeRightAction, ctrl: true)]
        [SettingsUISection(kKeybindingsTab, kNudgeGroup)]
        public ProxyBinding NudgeRightBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.RightBracket, kAlignGapPlusAction)]
        [SettingsUISection(kKeybindingsTab, kAlignGroup)]
        public ProxyBinding AlignGapPlusBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.LeftBracket, kAlignGapMinusAction)]
        [SettingsUISection(kKeybindingsTab, kAlignGroup)]
        public ProxyBinding AlignGapMinusBinding { get; set; }

        // Jezik SAMO za mod (panel + ove opcije) — igra ostaje na svom.
        [SettingsUISection(kGeneralTab, kBehaviorGroup)]
        public ModLanguageOption ModLanguage { get; set; } = ModLanguageOption.Auto;

        [SettingsUISection(kGeneralTab, kBehaviorGroup)]
        public bool AnarchyPaste { get; set; } = true;

        // Izgled panela: nas potpis ili cista igrina paleta (getovi kroz
        // igrine CSS varijable — korisnici su trazili vanila izgled).
        [SettingsUISection(kGeneralTab, kPanelGroup)]
        public PanelThemeOption PanelTheme { get; set; } = PanelThemeOption.Copaste;

        // Velicina panela u procentima — nekome je default sitan.
        [SettingsUISlider(min = 80, max = 125, step = 5)]
        [SettingsUISection(kGeneralTab, kPanelGroup)]
        public int PanelScale { get; set; } = 100;

        // Samo TEKST — panel ostaje istih dimenzija.
        [SettingsUISlider(min = 90, max = 130, step = 5)]
        [SettingsUISection(kGeneralTab, kPanelGroup)]
        public int TextScale { get; set; } = 100;

        // Zaštitni limiti kao opcije — jače mašine mogu više, slabije manje.
        // Defaulti = dosadašnje konstante.
        [SettingsUISlider(min = 500, max = 5000, step = 100)]
        [SettingsUISection(kGeneralTab, kLimitsGroup)]
        public int MaxSelection { get; set; } = 1000;

        [SettingsUISlider(min = 100, max = 1000, step = 50)]
        [SettingsUISection(kGeneralTab, kLimitsGroup)]
        public int MaxOverlayShapes { get; set; } = 400;

        [SettingsUISlider(min = 10, max = 100, step = 5)]
        [SettingsUISection(kGeneralTab, kLimitsGroup)]
        public int SelectionListMax { get; set; } = 50;

        // Sačuvana pozicija panela u pikselima ekrana; -1 = default (CSS pozicija).
        [SettingsUIHidden]
        public int PanelX { get; set; } = -1;

        [SettingsUIHidden]
        public int PanelY { get; set; } = -1;

        // Paste izgled: false = nalepljeni prop zadržava boju/varijaciju originala,
        // true = igra bira nasumično (staro ponašanje).
        [SettingsUIHidden]
        public bool RandomPasteVariation { get; set; } = false;

        // Zgrade u selekciji (v1.1 "Buildings"): kad je uključeno, klik/marquee/Ctrl+klik
        // biraju i zgrade — copy/paste/blueprint rade, transformacije ih preskaču (faza 2).
        // Selection filteri (panel kartica "Selection"): svaka kategorija se
        // pali/gasi nezavisno. Podrazumevano sve sem zgrada (siguran default).
        [SettingsUIHidden]
        public bool SelectProps { get; set; } = true;

        [SettingsUIHidden]
        public bool SelectTrees { get; set; } = true;

        [SettingsUIHidden]
        public bool SelectDecals { get; set; } = true;

        [SettingsUIHidden]
        public bool SelectSurfaces { get; set; } = true;

        [SettingsUIHidden]
        public bool SelectBuildings { get; set; } = false;

        // Samostalne ograde/živice (net lanes). Default isključeno, kao zgrade.
        [SettingsUIHidden]
        public bool SelectFences { get; set; } = false;

        // Mreže: čvorovi i segmenti puteva/staza/šina (samo pomeranje).
        [SettingsUIHidden]
        public bool SelectNetworks { get; set; } = false;

        // Road snap pri paste-u zgrada: sidro-zgrada se lepi na najbližu ivicu
        // puta kao kod običnog plopovanja, grupa prati.
        [SettingsUIHidden]
        public bool RoadSnapPaste { get; set; } = true;

        // Marquee sme da hvata i propove koji pripadaju zgradama (klik ih
        // uvek može izabrati, ovo otvara samo box select). Default isključeno.
        [SettingsUIHidden]
        public bool SelectBuildingProps { get; set; } = false;

        public override void Apply()
        {
            base.Apply();
            Mod.ApplyLanguageOverride();
        }

        public override void SetDefaults()
        {
            ModLanguage = ModLanguageOption.Auto;
            AnarchyPaste = true;
            PanelTheme = PanelThemeOption.Copaste;
            PanelScale = 100;
            TextScale = 100;
            MaxSelection = 1000;
            MaxOverlayShapes = 400;
            SelectionListMax = 50;
            PanelX = -1;
            PanelY = -1;
            RandomPasteVariation = false;
            SelectProps = true;
            SelectTrees = true;
            SelectDecals = true;
            SelectSurfaces = true;
            SelectBuildings = false;
            SelectFences = false;
            SelectNetworks = false;
            RoadSnapPaste = true;
            SelectBuildingProps = false;
        }
    }
}
