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
        private ValueBinding<int> m_ClipboardCount;
        private ValueBinding<string> m_Blueprints;
        private ValueBinding<int> m_UndoCount;
        private ValueBinding<string> m_SameFilter;
        private ValueBinding<bool> m_HeightPickArmed;
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
            AddBinding(m_ClipboardCount = new ValueBinding<int>("copaste", "clipboardCount", 0));
            AddBinding(m_Blueprints = new ValueBinding<string>("copaste", "blueprints", string.Empty));
            AddBinding(m_UndoCount = new ValueBinding<int>("copaste", "undoCount", 0));
            AddBinding(m_SameFilter = new ValueBinding<string>("copaste", "sameFilter", string.Empty));
            AddBinding(m_HeightPickArmed = new ValueBinding<bool>("copaste", "heightPickArmed", false));

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
                    if (!m_CopasteToolSystem.IsPasteMode)
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

            if (m_ToolActive.value)
            {
                m_PasteMode.Update(m_CopasteToolSystem.IsPasteMode);
                m_SelectedCount.Update(m_CopasteToolSystem.SelectedCount);
                m_ClipboardCount.Update(m_CopasteToolSystem.ClipboardCount);
                m_UndoCount.Update(m_CopasteToolSystem.UndoCount);
                m_SameFilter.Update(m_CopasteToolSystem.SameFilterName);
                m_HeightPickArmed.Update(m_CopasteToolSystem.HeightPickArmed);
            }
        }
    }
}
