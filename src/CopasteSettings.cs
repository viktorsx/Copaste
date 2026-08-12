namespace Copaste
{
    using Colossal.IO.AssetDatabase;
    using Game.Input;
    using Game.Modding;
    using Game.Settings;

    [FileLocation("ModsSettings/Copaste/Copaste")]
    [SettingsUITabOrder(kGeneralTab, kKeybindingsTab)]
    [SettingsUIGroupOrder(kBehaviorGroup, kToolGroup, kClipboardGroup, kEditingGroup, kNudgeGroup, kAlignGroup)]
    [SettingsUIShowGroupName(kToolGroup, kClipboardGroup, kEditingGroup, kNudgeGroup, kAlignGroup)]
    public class CopasteSettings : ModSetting
    {
        public const string kGeneralTab = "General";
        public const string kKeybindingsTab = "Keybindings";
        public const string kBehaviorGroup = "Behavior";
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

        [SettingsUISection(kGeneralTab, kBehaviorGroup)]
        public bool AnarchyPaste { get; set; } = true;

        // Sačuvana pozicija panela u pikselima ekrana; -1 = default (CSS pozicija).
        [SettingsUIHidden]
        public int PanelX { get; set; } = -1;

        [SettingsUIHidden]
        public int PanelY { get; set; } = -1;

        // Paste izgled: false = nalepljeni prop zadržava boju/varijaciju originala,
        // true = igra bira nasumično (staro ponašanje).
        [SettingsUIHidden]
        public bool RandomPasteVariation { get; set; } = false;

        public override void SetDefaults()
        {
            AnarchyPaste = true;
            PanelX = -1;
            PanelY = -1;
            RandomPasteVariation = false;
        }
    }
}
