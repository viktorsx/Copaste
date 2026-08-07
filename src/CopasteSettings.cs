namespace Copaste
{
    using Colossal.IO.AssetDatabase;
    using Game.Input;
    using Game.Modding;
    using Game.Settings;

    [FileLocation("ModsSettings/Copaste/Copaste")]
    public class CopasteSettings : ModSetting
    {
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

        public CopasteSettings(IMod mod)
            : base(mod)
        {
        }

        [SettingsUIKeyboardBinding(BindingKeyboard.C, kToggleAction, ctrl: true, shift: true)]
        public ProxyBinding ToggleBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.C, kCopyAction, ctrl: true)]
        public ProxyBinding CopyBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.V, kPasteAction, ctrl: true)]
        public ProxyBinding PasteBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Delete, kDeleteAction)]
        public ProxyBinding DeleteBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.PageUp, kRaiseAction)]
        public ProxyBinding RaiseBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.PageDown, kLowerAction)]
        public ProxyBinding LowerBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Z, kUndoAction, ctrl: true)]
        public ProxyBinding UndoBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.T, kSelectSameAction)]
        public ProxyBinding SelectSameBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.End, kSnapGroundAction)]
        public ProxyBinding SnapGroundBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.Home, kMatchHeightAction)]
        public ProxyBinding MatchHeightBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.UpArrow, kNudgeUpAction, ctrl: true)]
        public ProxyBinding NudgeUpBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.DownArrow, kNudgeDownAction, ctrl: true)]
        public ProxyBinding NudgeDownBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.LeftArrow, kNudgeLeftAction, ctrl: true)]
        public ProxyBinding NudgeLeftBinding { get; set; }

        [SettingsUIKeyboardBinding(BindingKeyboard.RightArrow, kNudgeRightAction, ctrl: true)]
        public ProxyBinding NudgeRightBinding { get; set; }

        public bool AnarchyPaste { get; set; } = true;

        public override void SetDefaults()
        {
            AnarchyPaste = true;
        }
    }
}
