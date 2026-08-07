using System.IO;
using System.Reflection;
using Colossal.IO.AssetDatabase;
using Colossal.Localization;
using Colossal.Logging;
using Colossal.UI;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace Copaste
{
    public sealed class Mod : IMod
    {
        public static readonly ILog Log = LogManager.GetLogger("Copaste").SetShowsErrorsInUI(false);

        public static CopasteSettings Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info("Copaste OnLoad");

            Settings = new CopasteSettings(this);
            Settings.RegisterInOptionsUI();
            AssetDatabase.global.LoadSettings("Copaste", Settings, new CopasteSettings(this));
            Settings.RegisterKeyBindings();

            GameManager.instance.localizationManager.AddSource("en-US", new MemorySource(Localization.BuildEnglish(Settings)));

            // Srpski prevod — samo ako igra (ili I18N mod) podržava sr lokalizaciju.
            foreach (string localeId in GameManager.instance.localizationManager.GetSupportedLocales())
            {
                if (localeId.StartsWith("sr"))
                {
                    GameManager.instance.localizationManager.AddSource(localeId, new MemorySource(Localization.BuildSerbian(Settings)));
                    Log.Info($"Serbian locale registered for {localeId}");
                }
            }

            // coui://copaste/ host za ikonicu dugmeta.
            string modPath = GetAssemblyDirectory();
            if (modPath != null)
            {
                UIManager.defaultUISystem.AddHostLocation("copaste", modPath);
                Log.Info($"UI host registered at {modPath}");
            }
            else
            {
                Log.Warn("Mod directory not found; toolbar icon will be missing");
            }

            updateSystem.UpdateAt<CopasteToolSystem>(SystemUpdatePhase.ToolUpdate);
            updateSystem.UpdateAt<CopasteUISystem>(SystemUpdatePhase.UIUpdate);
        }

        private static string GetAssemblyDirectory()
        {
            string assemblyName = Assembly.GetExecutingAssembly().FullName;
            ExecutableAsset modAsset = AssetDatabase.global.GetAsset(SearchFilter<ExecutableAsset>.ByCondition(x => x.definition?.FullName == assemblyName));
            return modAsset == null ? null : Path.GetDirectoryName(modAsset.GetMeta().path);
        }

        public void OnDispose()
        {
            Log.Info("Copaste OnDispose");
            Settings?.UnregisterInOptionsUI();
            Settings = null;
        }
    }
}
