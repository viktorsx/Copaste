namespace Copaste
{
    using Colossal.UI.Binding;
    using Game.Tools;
    using Game.UI;

    public partial class CopasteUISystem : UISystemBase
    {
        private ValueBinding<bool> m_ToolActive;
        private ValueBinding<bool> m_PasteMode;
        private ValueBinding<int> m_SelectedCount;
        private ValueBinding<int> m_CopyableCount;
        private ValueBinding<int> m_DeletableCount;
        private ValueBinding<int> m_UiTheme;
        private ValueBinding<bool> m_Underground;
        private ValueBinding<int> m_PanelScale;
        private ValueBinding<int> m_TextScale;

        // Maska filtera od pre poslednjeg solo-a. Zastavica je odvojena jer je
        // 0 VALIDNA maska (svi čipovi ugašeni) — sentinel bi je pojeo.
        private int m_PreSoloMask;
        private bool m_HasPreSoloMask;
        private ValueBinding<int> m_PropCount;
        private ValueBinding<int> m_HeightCount;
        private ValueBinding<int> m_ClipboardCount;
        private ValueBinding<string> m_Blueprints;
        private ValueBinding<int> m_UndoCount;
        private ValueBinding<int> m_RedoCount;
        private ValueBinding<string> m_SameFilter;
        private ValueBinding<bool> m_HeightPickArmed;
        private ValueBinding<string> m_SelectedName;
        private ValueBinding<int> m_PanelX;
        private ValueBinding<int> m_PanelY;
        private ValueBinding<bool> m_RandomVariation;
        private ValueBinding<bool> m_RoadSnap;
        private ValueBinding<bool> m_BuildingProps;
        private ValueBinding<bool> m_RelocateReady;
        private ValueBinding<bool> m_Relocating;
        private ValueBinding<int> m_SelectionFilters;

        private static int CurrentFilterMask()
        {
            if (Mod.Settings == null)
            {
                return 1 | 2 | 4 | 8;
            }

            return (Mod.Settings.SelectProps ? 1 : 0) |
                (Mod.Settings.SelectTrees ? 2 : 0) |
                (Mod.Settings.SelectDecals ? 4 : 0) |
                (Mod.Settings.SelectSurfaces ? 8 : 0) |
                (Mod.Settings.SelectBuildings ? 16 : 0) |
                (Mod.Settings.SelectFences ? 32 : 0) |
                (Mod.Settings.SelectNetworks ? 64 : 0);
        }
        private ValueBinding<float> m_AlignGapLive;
        private ValueBinding<bool> m_AlignPickArmed;
        private ValueBinding<int> m_AlignSessionSource;
        private ValueBinding<string> m_SelectionList;

        private static bool TryParseEntityId(string payload, out int index, out int version)
        {
            index = 0;
            version = 0;
            if (string.IsNullOrEmpty(payload))
            {
                return false;
            }

            string[] parts = payload.Split(':');
            return parts.Length == 2 &&
                int.TryParse(parts[0], out index) &&
                int.TryParse(parts[1], out version);
        }

        private static float ParseGap(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return -1f;
            }

            payload = payload.Trim().Replace(',', '.');
            if (!float.TryParse(payload, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gap) || gap <= 0f)
            {
                return -1f;
            }

            return gap;
        }
        private ToolSystem m_ToolSystem;
        private CopasteToolSystem m_CopasteToolSystem;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ToolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
            m_CopasteToolSystem = World.GetOrCreateSystemManaged<CopasteToolSystem>();

            string version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
            AddBinding(new ValueBinding<string>("copaste", "version", version));
            AddBinding(m_ToolActive = new ValueBinding<bool>("copaste", "toolActive", false));
            AddBinding(m_PasteMode = new ValueBinding<bool>("copaste", "pasteMode", false));
            AddBinding(m_SelectedCount = new ValueBinding<int>("copaste", "selectedCount", 0));
            AddBinding(m_CopyableCount = new ValueBinding<int>("copaste", "copyableCount", 0));
            AddBinding(m_DeletableCount = new ValueBinding<int>("copaste", "deletableCount", 0));
            AddBinding(m_UiTheme = new ValueBinding<int>("copaste", "uiTheme", 0));
            AddBinding(m_Underground = new ValueBinding<bool>("copaste", "underground", false));
            AddBinding(m_PanelScale = new ValueBinding<int>("copaste", "panelScale", 100));
            AddBinding(m_TextScale = new ValueBinding<int>("copaste", "textScale", 100));
            AddBinding(new TriggerBinding("copaste", "toggleUnderground", () =>
            {
                m_CopasteToolSystem.UndergroundMode = !m_CopasteToolSystem.UndergroundMode;
            }));
            AddBinding(m_PropCount = new ValueBinding<int>("copaste", "propCount", 0));
            AddBinding(m_HeightCount = new ValueBinding<int>("copaste", "heightCount", 0));
            AddBinding(m_ClipboardCount = new ValueBinding<int>("copaste", "clipboardCount", 0));
            AddBinding(m_Blueprints = new ValueBinding<string>("copaste", "blueprints", string.Empty));
            AddBinding(m_UndoCount = new ValueBinding<int>("copaste", "undoCount", 0));
            AddBinding(m_RedoCount = new ValueBinding<int>("copaste", "redoCount", 0));
            AddBinding(m_SameFilter = new ValueBinding<string>("copaste", "sameFilter", string.Empty));
            AddBinding(m_HeightPickArmed = new ValueBinding<bool>("copaste", "heightPickArmed", false));
            AddBinding(m_SelectedName = new ValueBinding<string>("copaste", "selectedName", string.Empty));
            AddBinding(m_PanelX = new ValueBinding<int>("copaste", "panelX", Mod.Settings != null ? Mod.Settings.PanelX : -1));
            AddBinding(m_PanelY = new ValueBinding<int>("copaste", "panelY", Mod.Settings != null ? Mod.Settings.PanelY : -1));

            // Selection filteri kao bitmask: 1=Props, 2=Trees, 4=Decals, 8=Surfaces, 16=Buildings.
            AddBinding(m_SelectionFilters = new ValueBinding<int>("copaste", "selectionFilters", CurrentFilterMask()));
            AddBinding(new TriggerBinding<int>("copaste", "toggleSelectionFilter", (bit) =>
            {
                if (Mod.Settings == null)
                {
                    return;
                }

                switch (bit)
                {
                    case 1: Mod.Settings.SelectProps = !Mod.Settings.SelectProps; break;
                    case 2: Mod.Settings.SelectTrees = !Mod.Settings.SelectTrees; break;
                    case 4: Mod.Settings.SelectDecals = !Mod.Settings.SelectDecals; break;
                    case 8: Mod.Settings.SelectSurfaces = !Mod.Settings.SelectSurfaces; break;
                    case 16: Mod.Settings.SelectBuildings = !Mod.Settings.SelectBuildings; break;
                    case 32: Mod.Settings.SelectFences = !Mod.Settings.SelectFences; break;
                    case 64: Mod.Settings.SelectNetworks = !Mod.Settings.SelectNetworks; break;
                    default: return;
                }

                // Ručna promena čipova poništava zapamćeno pre-solo stanje.
                m_HasPreSoloMask = false;
                Mod.Settings.ApplyAndSave();
                m_SelectionFilters.Update(CurrentFilterMask());
            }));

            // Desni klik na čip: "solo" — samo ta kategorija; ako je već jedina
            // uključena, vrati stanje od PRE solo-a (Photoshop layer ponašanje).
            AddBinding(new TriggerBinding<int>("copaste", "soloSelectionFilter", (bit) =>
            {
                if (Mod.Settings == null)
                {
                    return;
                }

                // Un-solo vraća zapamćenu masku od pre solo-a; fallback je
                // FABRIČKI skup (props, drveće, dekali, površine). Zgrade,
                // ograde i putevi su svi strogo opt-in i nijedan ne sme da se
                // upali kao nuspojava vraćanja filtera — ranije je fallback
                // bio 63, pa je palio zgrade i ograde prvi put u životu.
                int current = CurrentFilterMask();
                int target;
                if (current == bit)
                {
                    target = m_HasPreSoloMask ? m_PreSoloMask : 15;
                    m_HasPreSoloMask = false;
                }
                else
                {
                    // Pamti se samo PRVI solo — solo A pa solo B pa un-solo
                    // vraća originalno stanje, ne međukorak.
                    if (!m_HasPreSoloMask)
                    {
                        m_PreSoloMask = current;
                        m_HasPreSoloMask = true;
                    }

                    target = bit;
                }
                Mod.Settings.SelectProps = (target & 1) != 0;
                Mod.Settings.SelectTrees = (target & 2) != 0;
                Mod.Settings.SelectDecals = (target & 4) != 0;
                Mod.Settings.SelectSurfaces = (target & 8) != 0;
                Mod.Settings.SelectBuildings = (target & 16) != 0;
                Mod.Settings.SelectFences = (target & 32) != 0;
                Mod.Settings.SelectNetworks = (target & 64) != 0;
                Mod.Settings.ApplyAndSave();
                m_SelectionFilters.Update(CurrentFilterMask());
            }));

            AddBinding(m_BuildingProps = new ValueBinding<bool>("copaste", "buildingProps", Mod.Settings != null && Mod.Settings.SelectBuildingProps));
            AddBinding(new TriggerBinding<bool>("copaste", "setBuildingProps", (include) =>
            {
                if (Mod.Settings != null)
                {
                    Mod.Settings.SelectBuildingProps = include;
                    Mod.Settings.ApplyAndSave();
                    m_BuildingProps.Update(include);
                }
            }));

            AddBinding(m_RoadSnap = new ValueBinding<bool>("copaste", "roadSnap", Mod.Settings == null || Mod.Settings.RoadSnapPaste));
            AddBinding(new TriggerBinding<bool>("copaste", "setRoadSnap", (snap) =>
            {
                if (Mod.Settings != null)
                {
                    Mod.Settings.RoadSnapPaste = snap;
                    Mod.Settings.ApplyAndSave();
                    m_RoadSnap.Update(snap);
                }
            }));

            AddBinding(m_RandomVariation = new ValueBinding<bool>("copaste", "randomVariation", Mod.Settings != null && Mod.Settings.RandomPasteVariation));
            AddBinding(new TriggerBinding<bool>("copaste", "setRandomVariation", (random) =>
            {
                if (Mod.Settings != null)
                {
                    Mod.Settings.RandomPasteVariation = random;
                    Mod.Settings.ApplyAndSave();
                    m_RandomVariation.Update(random);
                }
            }));
            // Line = red (pozicije + rotacije); To prop = red po uzor-propu (pick).
            AddBinding(new TriggerBinding<string>("copaste", "actionAlignLine", (payload) =>
                m_CopasteToolSystem.TriggerAlignRow(true, ParseGap(payload))));
            AddBinding(new TriggerBinding<string>("copaste", "actionAlignRef", (payload) =>
                m_CopasteToolSystem.TriggerAlignPick(ParseGap(payload))));
            AddBinding(new TriggerBinding<string>("copaste", "setAlignGapLive", (payload) => m_CopasteToolSystem.SetAlignSessionGap(ParseGap(payload))));
            AddBinding(new TriggerBinding<int>("copaste", "adjustAlignGap", (direction) => m_CopasteToolSystem.AdjustAlignSessionGap(direction)));
            AddBinding(m_AlignPickArmed = new ValueBinding<bool>("copaste", "alignPickArmed", false));
            AddBinding(m_AlignSessionSource = new ValueBinding<int>("copaste", "alignSessionSource", 0));
            AddBinding(m_SelectionList = new ValueBinding<string>("copaste", "selectionList", string.Empty));
            AddBinding(m_RelocateReady = new ValueBinding<bool>("copaste", "relocateReady", false));
            AddBinding(m_Relocating = new ValueBinding<bool>("copaste", "relocating", false));

            AddBinding(new TriggerBinding<string>("copaste", "focusProp", (payload) =>
            {
                if (TryParseEntityId(payload, out int index, out int version))
                {
                    m_CopasteToolSystem.SetListFocus(index, version);
                }
                else
                {
                    m_CopasteToolSystem.ClearListFocus();
                }
            }));
            AddBinding(new TriggerBinding<string>("copaste", "selectOnlyProp", (payload) =>
            {
                if (TryParseEntityId(payload, out int index, out int version))
                {
                    m_CopasteToolSystem.SelectOnly(index, version);
                }
            }));

            AddBinding(new TriggerBinding<string>("copaste", "actionAlignCircle", (payload) =>
                m_CopasteToolSystem.TriggerAlignCircle(ParseGap(payload))));
            AddBinding(m_AlignGapLive = new ValueBinding<float>("copaste", "alignGapLive", -1f));

            AddBinding(new TriggerBinding<string>("copaste", "setPanelPos", (payload) =>
            {
                string[] parts = payload.Split(',');
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out int x) &&
                    int.TryParse(parts[1], out int y) &&
                    Mod.Settings != null)
                {
                    Mod.Settings.PanelX = x;
                    Mod.Settings.PanelY = y;
                    Mod.Settings.ApplyAndSave();
                    m_PanelX.Update(x);
                    m_PanelY.Update(y);
                }
            }));

            AddBinding(new TriggerBinding("copaste", "toggleTool", () => m_CopasteToolSystem.ToggleTool()));
            AddBinding(new TriggerBinding("copaste", "saveBlueprint", () =>
            {
                m_CopasteToolSystem.SaveBlueprint();
                RefreshBlueprints();
            }));
            AddBinding(new TriggerBinding<string>("copaste", "loadBlueprint", (name) =>
            {
                // Klik na blueprint odmah kreće u lepljenje.
                if (m_CopasteToolSystem.LoadBlueprint(name))
                {
                    if (m_CopasteToolSystem.IsPasteMode)
                    {
                        // Već lepimo: preview mora iznova za novi sadržaj clipboard-a.
                        m_CopasteToolSystem.RefreshPastePreview();
                    }
                    else
                    {
                        m_CopasteToolSystem.TriggerPaste();
                    }
                }
            }));
            AddBinding(new TriggerBinding<string>("copaste", "deleteBlueprint", (name) =>
            {
                m_CopasteToolSystem.DeleteBlueprint(name);
                RefreshBlueprints();
            }));
            AddBinding(new TriggerBinding<bool>("copaste", "setTyping", (typing) => m_CopasteToolSystem.SetUiTyping(typing)));
            AddBinding(new TriggerBinding<string>("copaste", "renameBlueprint", (payload) =>
            {
                string[] parts = payload.Split('\n');
                if (parts.Length == 2)
                {
                    m_CopasteToolSystem.RenameBlueprint(parts[0], parts[1]);
                    RefreshBlueprints();
                }
            }));
            AddBinding(new TriggerBinding("copaste", "actionMatchHeight", () => m_CopasteToolSystem.TriggerMatchHeight()));

            AddBinding(new TriggerBinding("copaste", "actionCopy", () => m_CopasteToolSystem.TriggerCopy()));
            AddBinding(new TriggerBinding("copaste", "actionPaste", () => m_CopasteToolSystem.TriggerPaste()));
            AddBinding(new TriggerBinding("copaste", "actionDelete", () => m_CopasteToolSystem.TriggerDelete()));
            AddBinding(new TriggerBinding("copaste", "actionUndo", () => m_CopasteToolSystem.TriggerUndo()));
            AddBinding(new TriggerBinding("copaste", "actionRedo", () => m_CopasteToolSystem.TriggerRedo()));
            AddBinding(new TriggerBinding("copaste", "actionRelocate", () => m_CopasteToolSystem.TriggerRelocate()));
            AddBinding(new TriggerBinding("copaste", "clearClipboard", () => m_CopasteToolSystem.TriggerClearClipboard()));
            AddBinding(new TriggerBinding("copaste", "actionSelectSame", () => m_CopasteToolSystem.TriggerSelectSame()));
            AddBinding(new TriggerBinding("copaste", "actionSnapGround", () => m_CopasteToolSystem.TriggerSnapGround()));
            AddBinding(new TriggerBinding<int>("copaste", "actionRotate", (degrees) => m_CopasteToolSystem.TriggerRotate(degrees)));
            AddBinding(new TriggerBinding<int>("copaste", "actionHeight", (steps) => m_CopasteToolSystem.TriggerHeight(steps)));

            m_ToolSystem.EventToolChanged += (tool) =>
            {
                bool active = tool == m_CopasteToolSystem;
                m_ToolActive.Update(active);
                if (active)
                {
                    RefreshBlueprints();
                }
            };
        }

        private void RefreshBlueprints()
        {
            m_Blueprints.Update(string.Join("\n", m_CopasteToolSystem.GetBlueprintNames()));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            // Uvek ažuriraj — gating na toolActive je ostavljao ustajale vrednosti
            // (npr. Paste dugme "svetli" iako je alat u Select modu).
            m_PasteMode.Update(m_CopasteToolSystem.IsPasteMode);
            m_SelectedCount.Update(m_CopasteToolSystem.SelectedCount);
            m_CopyableCount.Update(m_CopasteToolSystem.CopyableSelectedCount);
            m_DeletableCount.Update(m_CopasteToolSystem.DeletableSelectedCount);
            m_UiTheme.Update(Mod.Settings != null ? (int)Mod.Settings.PanelTheme : 0);
            m_Underground.Update(m_CopasteToolSystem.UndergroundMode);
            m_PanelScale.Update(Mod.Settings != null ? Mod.Settings.PanelScale : 100);
            m_TextScale.Update(Mod.Settings != null ? Mod.Settings.TextScale : 100);
            m_PropCount.Update(m_CopasteToolSystem.PropTargetCount);
            m_HeightCount.Update(m_CopasteToolSystem.HeightTargetCount);
            m_ClipboardCount.Update(m_CopasteToolSystem.ClipboardCount);
            m_UndoCount.Update(m_CopasteToolSystem.UndoCount);
            m_RedoCount.Update(m_CopasteToolSystem.RedoCount);
            m_SameFilter.Update(m_CopasteToolSystem.SameFilterName);
            m_HeightPickArmed.Update(m_CopasteToolSystem.HeightPickArmed);
            m_SelectedName.Update(m_CopasteToolSystem.SelectedPropName);
            m_AlignGapLive.Update(m_CopasteToolSystem.AlignSessionGap);
            m_AlignPickArmed.Update(m_CopasteToolSystem.AlignPickArmed);
            m_AlignSessionSource.Update(m_CopasteToolSystem.AlignSessionSource);
            m_SelectionList.Update(m_CopasteToolSystem.GetSelectionList());
            m_RelocateReady.Update(m_CopasteToolSystem.CanRelocate);
            m_Relocating.Update(m_CopasteToolSystem.IsRelocating);
        }
    }
}
