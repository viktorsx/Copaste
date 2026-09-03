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

            // Prevodi — svaki samo ako igra (ili I18N mod) podržava taj locale.
            foreach (string localeId in GameManager.instance.localizationManager.GetSupportedLocales())
            {
                if (localeId.StartsWith("sr"))
                {
                    GameManager.instance.localizationManager.AddSource(localeId, new MemorySource(Localization.BuildSerbian(Settings)));
                    Log.Info($"Serbian locale registered for {localeId}");
                }
                else if (localeId.StartsWith("de"))
                {
                    GameManager.instance.localizationManager.AddSource(localeId, new MemorySource(Localization.BuildGerman(Settings)));
                    Log.Info($"German locale registered for {localeId}");
                }
                else if (localeId.StartsWith("fr"))
                {
                    GameManager.instance.localizationManager.AddSource(localeId, new MemorySource(Localization.BuildFrench(Settings)));
                    Log.Info($"French locale registered for {localeId}");
                }
            }

            // Jezik moda nezavisno od igre: izabrani rečnik se doda kao
            // POSLEDNJI izvor za aktivni locale (poslednji upis pobeđuje),
            // i ponovo primeni na svaku promenu jezika igre.
            ApplyLanguageOverride();
            GameManager.instance.localizationManager.onActiveDictionaryChanged += ApplyLanguageOverride;

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

        // Aktivni override izvor — da se pri promeni ukloni pre novog.
        private static MemorySource s_LanguageOverrideSource;
        private static string s_LanguageOverrideLocale;
        private static bool s_ReapplyingLanguage;

        // Poslednje PRIMENJENO stanje — bez ovoga se posao radio i kad se
        // ništa nije promenilo.
        private static ModLanguageOption s_AppliedLanguage = (ModLanguageOption)(-1);
        private static string s_AppliedLocale;

        public static void ApplyLanguageOverride()
        {
            if (Settings == null || GameManager.instance == null || s_ReapplyingLanguage)
            {
                return;
            }

            // Ovo se zove iz Settings.Apply(), a Apply() ide na SVAKI klik na
            // filter čip u panelu — a posao unutra je ReloadActiveLocale, pun
            // reload rečnika igre. Otud vidljivo kočenje pri prebacivanju
            // Props/Decals. Kad se ni jezik ni aktivni locale nisu promenili,
            // nema šta da se radi.
            string activeLocale = GameManager.instance.localizationManager.activeLocaleId;
            if (Settings.ModLanguage == s_AppliedLanguage && activeLocale == s_AppliedLocale)
            {
                return;
            }

            s_AppliedLanguage = Settings.ModLanguage;
            s_AppliedLocale = activeLocale;

            // Guard oko CELE funkcije: i AddSource i RemoveSource SAMI okidaju
            // onActiveDictionaryChanged (dokazano crash-om: beskonačna
            // rekurzija kroz naš handler) — dok mi menjamo izvore, event se
            // ignoriše.
            s_ReapplyingLanguage = true;
            try
            {
                var manager = GameManager.instance.localizationManager;
                if (s_LanguageOverrideSource != null)
                {
                    manager.RemoveSource(s_LanguageOverrideLocale, s_LanguageOverrideSource);
                    s_LanguageOverrideSource = null;
                }

                if (Settings.ModLanguage != ModLanguageOption.Auto)
                {
                    System.Collections.Generic.Dictionary<string, string> dictionary = Settings.ModLanguage switch
                    {
                        ModLanguageOption.German => Localization.BuildGerman(Settings),
                        ModLanguageOption.French => Localization.BuildFrench(Settings),
                        ModLanguageOption.Serbian => Localization.BuildSerbian(Settings),
                        _ => Localization.BuildEnglish(Settings),
                    };

                    s_LanguageOverrideLocale = manager.activeLocaleId;
                    s_LanguageOverrideSource = new MemorySource(dictionary);
                    manager.AddSource(s_LanguageOverrideLocale, s_LanguageOverrideSource);
                }

                // Reload gura novo stanje u aktivni rečnik (bitno pri povratku
                // na Auto, kad RemoveSource mora da OČISTI naše ključeve).
                manager.ReloadActiveLocale();
            }
            finally
            {
                s_ReapplyingLanguage = false;
            }
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
