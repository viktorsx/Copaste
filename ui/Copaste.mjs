/*!
 * Cities: Skylines II UI Module
 *
 * Id: Copaste
 * Author: Viktor
 * Version: 1.3.0
 * Dependencies:
 */
const register = (moduleRegistry) => {
  try {
    const React = window.React;
    const api = window["cs2/api"];
    if (!React || !api) {
      console.log("[Copaste] missing globals: React=" + !!React + " api=" + !!api);
      return;
    }

    const { bindValue, trigger, useValue } = api;
    const ui = window["cs2/ui"] || {};
    const l10n = window["cs2/l10n"] || {};
    const h = React.createElement;

    const toolActive$ = bindValue("copaste", "toolActive", false);
    const pasteMode$ = bindValue("copaste", "pasteMode", false);
    const selectedCount$ = bindValue("copaste", "selectedCount", 0);
    const copyableCount$ = bindValue("copaste", "copyableCount", 0);
    const deletableCount$ = bindValue("copaste", "deletableCount", 0);
    const uiTheme$ = bindValue("copaste", "uiTheme", 0);
    const underground$ = bindValue("copaste", "underground", false);
    const panelScale$ = bindValue("copaste", "panelScale", 100);
    const textScale$ = bindValue("copaste", "textScale", 100);
    const propCount$ = bindValue("copaste", "propCount", 0);
    const heightCount$ = bindValue("copaste", "heightCount", 0);
    const clipboardCount$ = bindValue("copaste", "clipboardCount", 0);
    const blueprints$ = bindValue("copaste", "blueprints", "");
    const undoCount$ = bindValue("copaste", "undoCount", 0);
    const redoCount$ = bindValue("copaste", "redoCount", 0);
    const sameFilter$ = bindValue("copaste", "sameFilter", "");
    const heightPickArmed$ = bindValue("copaste", "heightPickArmed", false);
    const version$ = bindValue("copaste", "version", "");
    const selectedName$ = bindValue("copaste", "selectedName", "");
    const panelX$ = bindValue("copaste", "panelX", -1);
    const panelY$ = bindValue("copaste", "panelY", -1);
    const randomVariation$ = bindValue("copaste", "randomVariation", false);
    const roadSnap$ = bindValue("copaste", "roadSnap", true);
    const buildingProps$ = bindValue("copaste", "buildingProps", false);
    const selectionFilters$ = bindValue("copaste", "selectionFilters", 15);
    const relocateReady$ = bindValue("copaste", "relocateReady", false);
    const relocating$ = bindValue("copaste", "relocating", false);
    const alignGapLive$ = bindValue("copaste", "alignGapLive", -1);
    const alignPickArmed$ = bindValue("copaste", "alignPickArmed", false);
    const alignSessionSource$ = bindValue("copaste", "alignSessionSource", 0);
    const selectionList$ = bindValue("copaste", "selectionList", "");

    const withTooltip = (tooltip, element) =>
      ui.Tooltip ? h(ui.Tooltip, { tooltip: tooltip }, element) : element;

    // Mali ikon-prekidač od IGRINIH delova (Button + Icon, tinted) — boje,
    // hover i tema dolaze iz igre; aktivno stanje naša klasa preko igrinih
    // CSS varijabli, pa radi u obe teme.
    const iconToggle = (tooltip, active, onSelect, iconSrc) => {
      const cls = "copasteIconToggle" + (active ? " copasteIconToggleOn" : "");
      let inner = ui.Icon
        ? h(ui.Icon, { src: iconSrc, tinted: true })
        : h("img", { src: iconSrc });
      let button = ui.Button
        ? h(ui.Button, { variant: "flat", className: cls, onSelect: onSelect }, inner)
        : h("button", { className: cls, onClick: onSelect }, inner);
      return withTooltip(tooltip, button);
    };

    // Vanila floating Button iz cs2/ui
    // (Button + variant:"floating" + svg sa width/height 100%). Bez naših klasa:
    // pozadinu i selected izgled diktira igra ili tema mod.

    const CopasteButton = () => {
      const active = useValue(toolActive$);
      const onToggle = () => trigger("copaste", "toggleTool");

      let button;
      if (ui.Button) {
        button = h(ui.Button, {
          src: "coui://copaste/copaste.svg",
          variant: "floating",
          onSelect: onToggle,
        });
      } else {
        button = h(
          "button",
          {
            onClick: onToggle,
            style: {
              width: "40rem",
              height: "40rem",
              borderRadius: "50%",
              border: "none",
              backgroundColor: "rgba(0,0,0,0.4)",
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              cursor: "pointer",
              pointerEvents: "auto",
            },
          },
          h("img", {
            src: "coui://copaste/copaste.svg",
            style: { width: "24rem", height: "24rem" },
          })
        );
      }

      return withTooltip("Copaste", button);
    };

    const CopastePanel = () => {
      const active = useValue(toolActive$);
      const pasteMode = useValue(pasteMode$);
      const selected = useValue(selectedCount$);
      // Copy/Save gate-uju na copyable (goli čvorovi se ne kopiraju);
      // Delete na deletable (čvor briše svoje krake, pa se broji).
      const copyable = useValue(copyableCount$);
      const deletable = useValue(deletableCount$);
      // 0 = Copaste stil, 1 = vanila (igrine CSS varijable preko naših klasa).
      const uiTheme = useValue(uiTheme$);
      const underground = useValue(underground$);
      const panelScale = useValue(panelScale$);
      const textScale = useValue(textScale$);
      const propCount = useValue(propCount$);
      const heightCount = useValue(heightCount$);
      const clipboard = useValue(clipboardCount$);
      const blueprintsRaw = useValue(blueprints$);
      const undoCount = useValue(undoCount$);
      const redoCount = useValue(redoCount$);
      const sameFilter = useValue(sameFilter$);
      const heightPickArmed = useValue(heightPickArmed$);
      const [renaming, setRenaming] = React.useState(null);
      const [renameValue, setRenameValue] = React.useState("");
      const [alignGap, setAlignGap] = React.useState("");
      const version = useValue(version$);
      const selectedName = useValue(selectedName$);
      const savedX = useValue(panelX$);
      const savedY = useValue(panelY$);
      const randomVariation = useValue(randomVariation$);
      const roadSnap = useValue(roadSnap$);
      const buildingProps = useValue(buildingProps$);
      const selectionFilters = useValue(selectionFilters$);
      const relocateReady = useValue(relocateReady$);
      const relocating = useValue(relocating$);
      const alignGapLive = useValue(alignGapLive$);
      const alignPickArmed = useValue(alignPickArmed$);
      const alignSessionSource = useValue(alignSessionSource$);
      const selectionListRaw = useValue(selectionList$);

      // Lokalizacija panela: ključevi žive u C# rečnicima (EN/DE/FR/SR);
      // bez cs2/l10n modula ili prevoda pada se na engleski fallback.
      const loc = l10n.useLocalization ? l10n.useLocalization() : null;
      const t = (id, fallback) => {
        if (!loc || !loc.translate) return fallback;
        const s = loc.translate("COPASTE." + id, fallback);
        return s == null || s === "" ? fallback : s;
      };

      // "idx:ver:ime" po redu — hover crta prsten oko propa, klik ostavlja samo njega.
      const selectionEntries = selectionListRaw
        ? selectionListRaw.split("\n").map((line) => {
            const first = line.indexOf(":");
            const second = line.indexOf(":", first + 1);
            return { id: line.substring(0, second), name: line.substring(second + 1) };
          })
        : [];

      // Stepper: dok je align sesija živa, strelice idu ISTIM putem kao [ i ]
      // prečice (C# korak od 0,5 m); bez sesije samo menjaju lokalnu vrednost
      // koja će važiti za sledeći align. Prikaz prati živu sesiju.
      const [gapFocused, setGapFocused] = React.useState(false);
      const sessionGapText = alignGapLive > 0 ? String(Math.round(alignGapLive * 10) / 10) : "";
      const gapDisplay = gapFocused ? alignGap : sessionGapText || alignGap;

      const stepGap = (dir) => {
        if (alignGapLive > 0) {
          trigger("copaste", "adjustAlignGap", dir);
          return;
        }
        let value = parseFloat((alignGap || "").replace(",", "."));
        value = isNaN(value) ? (dir > 0 ? 0.5 : 0) : value + dir * 0.5;
        if (value < 0.5) {
          setAlignGap("");
          return;
        }
        setAlignGap(String(Math.round(value * 10) / 10));
      };
      const [dragPos, setDragPos] = React.useState(null);
      const panelRef = React.useRef(null);

      // Blueprints i Selected props: stranice od po 5, listanje strelicama u naslovu.
      const [bpPage, setBpPage] = React.useState(0);
      const [selPage, setSelPage] = React.useState(0);

      if (!active) {
        return null;
      }

      // Pozicija panela: sačuvana/prevučena u pikselima, inače CSS default.
      const hasPos = dragPos !== null || (savedX >= 0 && savedY >= 0);
      const rawX = dragPos ? dragPos.x : savedX;
      const rawY = dragPos ? dragPos.y : savedY;
      const winW = window.innerWidth || 1920;
      const winH = window.innerHeight || 1080;
      const posX = Math.max(0, Math.min(rawX, winW - 60));
      const posY = Math.max(0, Math.min(rawY, winH - 60));
      // Skala celog panela (Options → Panel size) — transform hvata i tekst
      // i razmake; origin gore-levo da pozicija ostane tačna.
      const scaleStyle = panelScale !== 100
        ? { transform: "scale(" + panelScale / 100 + ")", transformOrigin: "top left" }
        : {};
      const panelStyle = hasPos
        ? Object.assign({ top: posY + "px", left: posX + "px" }, scaleStyle)
        : (panelScale !== 100 ? scaleStyle : undefined);

      const onHeaderMouseDown = (e) => {
        const el = panelRef.current;
        if (!el || !el.getBoundingClientRect) return;
        const rect = el.getBoundingClientRect();
        const startX = e.clientX;
        const startY = e.clientY;
        const baseX = rect.left;
        const baseY = rect.top;
        const onMove = (ev) => {
          setDragPos({ x: baseX + (ev.clientX - startX), y: baseY + (ev.clientY - startY) });
        };
        const onUp = (ev) => {
          window.removeEventListener("mousemove", onMove);
          window.removeEventListener("mouseup", onUp);
          const fx = Math.max(0, Math.round(baseX + (ev.clientX - startX)));
          const fy = Math.max(0, Math.round(baseY + (ev.clientY - startY)));
          setDragPos({ x: fx, y: fy });
          trigger("copaste", "setPanelPos", fx + "," + fy);
        };
        window.addEventListener("mousemove", onMove);
        window.addEventListener("mouseup", onUp);
      };

      const blueprints = blueprintsRaw ? blueprintsRaw.split("\n").filter((n) => n.length > 0) : [];
      const bpPages = Math.max(1, Math.ceil(blueprints.length / 5));
      const bpPageSafe = Math.min(bpPage, bpPages - 1);
      const bpPageItems = blueprints.slice(bpPageSafe * 5, (bpPageSafe * 5) + 5);
      const selPages = Math.max(1, Math.ceil(selectionEntries.length / 5));
      const selPageSafe = Math.min(selPage, selPages - 1);
      const selPageItems = selectionEntries.slice(selPageSafe * 5, (selPageSafe * 5) + 5);

      const pager = (pageSafe, pages, setPage) =>
        pages > 1
          ? h(
              "div",
              { className: "copasteStepper" },
              h(
                "button",
                { className: "copasteStepperBtn", onClick: () => setPage(Math.max(0, pageSafe - 1)) },
                h("img", { src: "coui://copaste/chevl.svg" })
              ),
              h("div", { className: "copastePageLabel" }, (pageSafe + 1) + "/" + pages),
              h(
                "button",
                { className: "copasteStepperBtn", onClick: () => setPage(Math.min(pages - 1, pageSafe + 1)) },
                h("img", { src: "coui://copaste/chevr.svg" })
              )
            )
          : null;

      // Dugme: label ili SVG ikonica, sa tooltip-om i vidljivim stanjem.
      const actionBtn = (content, tooltip, enabled, onClick, isActive, variant, icon) => {
        const inner =
          typeof content === "string" && content.endsWith(".svg")
            ? h("img", { src: "coui://copaste/" + content })
            : icon
            ? [
                h("img", { key: "i", className: "copasteBtnIcon", src: "coui://copaste/" + icon }),
                h("span", { key: "t" }, content),
              ]
            : content;

        const variantClass = isActive
          ? variant === "info"
            ? " copasteBtnActiveInfo"
            : " copasteBtnActive"
          : !enabled
          ? " copasteBtnDisabled"
          : variant === "primary"
          ? " copasteBtnPrimary"
          : variant === "danger"
          ? " copasteBtnDanger"
          : variant === "info"
          ? " copasteBtnInfo"
          : "";

        const btn = h(
          "button",
          {
            key: tooltip,
            className: "copasteBtn" + variantClass,
            onClick: enabled ? onClick : undefined,
          },
          inner
        );

        return withTooltip(tooltip, btn);
      };

      const section = (title, ...rows) =>
        h("div", { key: title, className: "copasteCard" },
          h("div", { className: "copasteSectionTitle" }, title),
          ...rows);

      // Selection filter čip: klik = on/off, desni klik = solo (samo ta
      // kategorija; ponovni desni klik na jedinu uključenu vraća sve).
      const chip = (label, bit, tooltip, icon) =>
        withTooltip(
          tooltip + " • " + t("CHIP_SOLO_TIP", "Right click: just this one / everything"),
          h(
            "div",
            {
              key: label,
              className: "copasteChip" + ((selectionFilters & bit) ? " copasteChipOn" : ""),
              onClick: () => trigger("copaste", "toggleSelectionFilter", bit),
              onMouseDown: (e) => {
                if (e.button === 2) {
                  trigger("copaste", "soloSelectionFilter", bit);
                }
              },
            },
            icon ? h("img", { src: "coui://copaste/" + icon }) : null,
            label
          )
        );

      const stat = (label, value, pad, extra) =>
        h("div", { className: "copasteStat" + (pad ? " copasteStatPad" : "") },
          h("div", { className: "copasteStatValueRow" },
            h("div", { className: "copasteStatValue" + (value > 0 ? " copasteStatValueLive" : "") }, String(value)),
            extra || null),
          h("div", { className: "copasteStatLabel" }, label));

      const commitRename = (oldName) => {
        if (renameValue && renameValue !== oldName) {
          trigger("copaste", "renameBlueprint", oldName + "\n" + renameValue);
        }
        setRenaming(null);
        trigger("copaste", "setTyping", false);
      };

      const cancelRename = () => {
        setRenaming(null);
        trigger("copaste", "setTyping", false);
      };

      const bpRow = (name) => {
        if (renaming === name) {
          return h(
            "div",
            { key: name, className: "copasteBpRow" },
            h("input", {
              className: "copasteInput",
              value: renameValue,
              onChange: (e) => setRenameValue(e.target.value),
              onKeyDown: (e) => {
                if (e.key === "Enter") commitRename(name);
                if (e.key === "Escape") cancelRename();
              },
              autoFocus: true,
              onFocus: () => trigger("copaste", "setTyping", true),
              onBlur: () => commitRename(name),
            })
          );
        }

        return h(
          "div",
          { key: name, className: "copasteBpRow" },
          withTooltip(t("BP_LOAD_TIP", "Load and start pasting"), h(
            "button",
            { className: "copasteBpLoad", onClick: () => trigger("copaste", "loadBlueprint", name) },
            // Ellipsis nosi unutrašnji div (dugme je flex kontejner).
            h("div", { className: "copasteSelName" }, name)
          )),
          withTooltip(t("BP_RENAME_TIP", "Rename"), h(
            "button",
            {
              className: "copasteBpIcon",
              onClick: () => {
                // Kucanje se prijavljuje tek kad polje stvarno dobije fokus
                // (onFocus nize). Ranije se prijavljivalo ovde, pa je klik
                // pored polja ostavljao alat ubedjen da korisnik kuca — i sav
                // ulaz je cutao dok se igra ne restartuje.
                setRenaming(name);
                setRenameValue(name);
              },
            },
            h("img", { src: "coui://copaste/rename.svg" })
          )),
          withTooltip(t("BP_DELETE_TIP", "Delete blueprint"), h(
            "button",
            {
              className: "copasteBpIcon copasteBpIconDelete",
              onClick: () => trigger("copaste", "deleteBlueprint", name),
            },
            h("img", { src: "coui://copaste/delete.svg" })
          )),
          // Pun naziv na hover — isti šablon kao Selected props red.
          name.length > 24
            ? h("div", { className: "copasteNameTip" }, name)
            : null
        );
      };

      // PRAVA vanila: ceo panel zivi u IGRINOJ Panel komponenti —
      // nas sadrzaj je telo, drag ide
      // na Panel header. Bez ui.Panel (starije verzije igre) pada se na
      // CSS varijantu (igrine boje preko nasih klasa).
      // Uputstvo se vise ne ispisuje na dnu panela — zivi u tooltip-u
      // logotipa (hover na COPASTE natpis), i dalje prati aktivni mod.
      const hintText = relocating
        ? t("HINT_RELOCATE", "Building follows the cursor • Road snap sets the facing • Click: place • Tab/RMB: cancel")
        : pasteMode
        ? t("HINT_PASTE", "Click: place • RMB drag: rotate • PgUp/PgDn: height • RMB: back")
        : heightPickArmed
        ? t("HINT_HEIGHT_PICK", "Click a prop to copy its height • RMB: cancel")
        : alignPickArmed
        ? t("HINT_ALIGN_PICK", "Click a reference prop: row through it, all rotated like it • RMB: cancel")
        : t("HINT_SELECT", "Click/box: select • Ctrl+click: pick overlapped • Alt+drag: move one • Alt+wheel: spin each • RMB drag: rotate");

      const nativeVanilla = uiTheme === 1 && !!ui.Panel;

      // Veličina TEKSTA (Options → Text size): koren nosi font-size, sve
      // unutrašnje veličine teksta su u em — gabarit panela ostaje isti.
      const textStyle = { fontSize: Math.round(13 * textScale) / 100 + "rem" };
      const content = h(
        "div",
        nativeVanilla
          ? { className: "copastePanel copasteThemeVanilla copasteVanillaBody", style: textStyle }
          : { className: "copastePanel" + (uiTheme === 1 ? " copasteThemeVanilla" : ""), ref: panelRef, style: Object.assign({}, panelStyle, textStyle) },
        h(
          "div",
          { className: "copasteHeader", onMouseDown: onHeaderMouseDown },
          withTooltip(hintText, h("img", { className: "copasteTitleLogo", src: "coui://copaste/copastelogotext.svg" })),
          version ? h("div", { className: "copasteVersion" }, "v" + version) : null
        ),
        h(
          "div",
          { className: "copasteCard copasteCardFlush", style: { marginTop: "0" } },
          h(
            "div",
            { className: "copasteStatsRow" },
            stat(t("STAT_SELECTED", "Selected"), selected, false),
            h("div", { className: "copasteStatDivider" }),
            stat(
              t("STAT_CLIPBOARD", "Clipboard"),
              clipboard,
              true,
              clipboard > 0
                ? withTooltip(
                    t("CLEAR_TIP", "Clear the clipboard"),
                    h(
                      "button",
                      { className: "copasteStatClear", onClick: () => trigger("copaste", "clearClipboard") },
                      t("CLEAR", "Clear")
                    )
                  )
                : null
            ),
            // Dugme ostaje i kad se Networks cip ugasi DOK JE rezim upaljen —
            // inace nestane, a filtriranje ostane, pa korisnik nema cime da
            // ga vrati.
            ((selectionFilters & 64) || underground) ? h("div", { className: "copasteStatDivider" }) : null,
            ((selectionFilters & 64) || underground)
              ? withTooltip(
                  t("UNDERGROUND_TIP", "Underground view: pick and box-select only what is below ground (metro, tunnels). Copy/paste and undo work the same in both worlds (U)"),
                  h(
                    "button",
                    {
                      className: "copasteUndergroundBtn" + (underground ? " copasteUndergroundBtnOn" : ""),
                      onClick: () => trigger("copaste", "toggleUnderground"),
                    },
                    ui.Icon
                      ? h(ui.Icon, { src: "Media/Tools/Net Tool/Underground.svg", tinted: true })
                      : h("img", { src: "Media/Tools/Net Tool/Underground.svg" })
                  )
                )
              : null
          ),
          selectedName
            ? h(
                "div",
                { className: "copasteFilterRow copastePropRow" },
                h("img", { src: "coui://copaste/prop.svg" }),
                h("div", { className: "copasteFilterLabel" }, t("PROP_LABEL", "Prop")),
                h("div", { className: "copasteFilterValue copastePropName" }, selectedName),
                // Dugačka imena se seku na "..." — pun naziv u tooltip-u iznad reda.
                selectedName.length > 20
                  ? h("div", { className: "copasteNameTip" }, selectedName)
                  : null
              )
            : null,
          // Uvek vidljiv red: [ikonica + Filter] dugme pali/gasi tip-filter.
          h(
            "div",
            { className: "copasteFilterRow" },
            withTooltip(
              t("FILTER_TIP", "Marquee prop filter: box select only picks this prop type. Click a prop to set it, click again to turn off (T)"),
              h(
                "button",
                { className: "copasteFilterBtn", onClick: () => trigger("copaste", "actionSelectSame") },
                h("img", { src: "coui://copaste/filter.svg" }),
                h("span", null, t("FILTER", "Filter"))
              )
            ),
            h(
              "div",
              { className: "copasteFilterValue copasteFilterValueClip" + (sameFilter ? "" : " copasteFilterValueOff") },
              sameFilter || t("FILTER_OFF", "Off")
            )
          )
        ),
        h(
          "div",
          { key: "Selection", className: "copasteCard" },
          h("div", { className: "copasteSectionTitle" }, t("SEC_SELECTION", "Selection")),
          h(
            "div",
            { className: "copasteChipRow" },
            chip(t("CHIP_PROPS", "Props"), 1, t("CHIP_PROPS_TIP", "Benches, lamps and other props"), "prop.svg"),
            chip(t("CHIP_TREES", "Trees"), 2, t("CHIP_TREES_TIP", "Trees, bushes and other vegetation"), "tree.svg"),
            chip(t("CHIP_DECALS", "Decals"), 4, t("CHIP_DECALS_TIP", "Road markings, stains and other decals"), "decal.svg")
          ),
          h(
            "div",
            { className: "copasteChipRow" },
            chip(t("CHIP_SURFACES", "Surfaces"), 8, t("CHIP_SURFACES_TIP", "Painted surfaces"), "surface.svg"),
            chip(t("CHIP_BUILDINGS", "Buildings"), 16, t("CHIP_BUILDINGS_TIP", "Service buildings, unique ones and grown homes"), "building.svg"),
            chip(t("CHIP_FENCES", "Fences"), 32, t("CHIP_FENCES_TIP", "Fences and hedges drawn along a line"), "fence.svg")
          ),
          h(
            "div",
            { className: "copasteChipRow" },
            chip(t("CHIP_NETWORKS", "Networks"), 64, t("CHIP_NETWORKS_TIP", "Road, path and track nodes and segments — move, rotate, copy and delete; power lines and pipes stay untouched"), "network.svg")
          ),
          h(
            "div",
            { className: "copasteSubTitleRow" },
            h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, t("SEC_BUILDING_ELEMENTS", "Building elements")),
            withTooltip(
              t("BUILDING_ELEMENTS_TIP", "When on, selection also reaches elements that belong to buildings — their props, trees, decals and lot surfaces, each following its filter above. Deleting a lot decoration surface also removes what it keeps spawning. When off, nothing building-owned can be selected, not even by click"),
              h(
                "button",
                {
                  className: "copasteSwitch" + (buildingProps ? " copasteSwitchOn" : ""),
                  onClick: () => trigger("copaste", "setBuildingProps", !buildingProps),
                },
                buildingProps ? t("ON", "On") : t("OFF", "Off")
              )
            )
          )
        ),
        selectionEntries.length > 0
          ? h(
              "div",
              { className: "copasteCard" },
              h(
                "div",
                { className: "copasteSubTitleRow", style: { margin: "0 2rem 5rem" } },
                h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, t("SEC_SELECTED_PROPS", "Selected props")),
                pager(selPageSafe, selPages, setSelPage)
              ),
              h(
                "div",
                null,
                selPageItems.map((entry, i) =>
                  h(
                    "div",
                    { key: entry.id + "-" + i, className: "copasteBpRow" },
                    h(
                      "button",
                      {
                        className: "copasteBpLoad",
                        onClick: () => trigger("copaste", "selectOnlyProp", entry.id),
                        onMouseEnter: () => trigger("copaste", "focusProp", entry.id),
                        onMouseLeave: () => trigger("copaste", "focusProp", ""),
                      },
                      // Unutrašnji div nosi ellipsis — dugme je flex kontejner,
                      // pa text-overflow na njemu samom ne radi.
                      h("div", { className: "copasteSelName" }, entry.name)
                    ),
                    // Dugačka imena (kuće!) se seku na "..." — pun naziv u
                    // tooltip-u iznad reda, isti šablon kao Prop red.
                    entry.name.length > 24
                      ? h("div", { className: "copasteNameTip" }, entry.name)
                      : null
                  )
                )
              )
            )
          : null,
        section(
          t("SEC_CLIPBOARD", "Clipboard"),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(t("COPY", "Copy"), t("COPY_TIP", "Copy selection (Ctrl+C)"), copyable > 0 && !pasteMode, () => trigger("copaste", "actionCopy"), false, "primary", "copy.svg"),
            actionBtn(t("PASTE", "Paste"), t("PASTE_TIP", "Paste mode on/off (Ctrl+V)"), clipboard > 0, () => trigger("copaste", "actionPaste"), pasteMode, undefined, "paste.svg"),
            copyable > 0
              ? actionBtn(t("SAVE", "Save"), t("SAVE_TIP", "Save selection as blueprint"), true, () => trigger("copaste", "saveBlueprint"), false, undefined, "save.svg")
              : null
          ),
          h("div", { className: "copasteSectionTitle copasteSubTitle" }, t("SEC_PASTE_LOOK", "Paste look")),
          withTooltip(
            t("PASTE_LOOK_TIP", "Original: pasted props keep the copied prop's color variation. Random: the game picks one"),
            h(
              "div",
              { className: "copasteToggleTrack" },
              h(
                "div",
                {
                  className: "copasteToggleOpt" + (!randomVariation ? " copasteToggleOptActive" : ""),
                  onClick: () => trigger("copaste", "setRandomVariation", false),
                },
                t("ORIGINAL", "Original")
              ),
              h(
                "div",
                {
                  className: "copasteToggleOpt" + (randomVariation ? " copasteToggleOptActive" : ""),
                  onClick: () => trigger("copaste", "setRandomVariation", true),
                },
                t("RANDOM", "Random")
              )
            )
          ),
          // Road snap red samo dok je Buildings filter uključen — da ne buni
          // kod čistog prop kopiranja (snap se ionako pali samo uz zgradu).

          (selectionFilters & 16)
            ? h(
                "div",
                { className: "copasteSubTitleRow" },
                h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, t("SEC_ROAD_SNAP", "Road snap")),
                withTooltip(
                  t("ROAD_SNAP_TIP", "Pasted buildings snap to the nearest road like normal plopping — the whole group rotates to face it. While snapped, rotation follows the road"),
                  h(
                    "button",
                    {
                      className: "copasteSwitch" + (roadSnap ? " copasteSwitchOn" : ""),
                      onClick: () => trigger("copaste", "setRoadSnap", !roadSnap),
                    },
                    roadSnap ? t("ON", "On") : t("OFF", "Off")
                  )
                )
              )
            : null
        ),
        section(
          t("SEC_EDIT", "Edit"),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(t("UNDO", "Undo"), t("UNDO_TIP", "Undo last action (Ctrl+Z)"), undoCount > 0, () => trigger("copaste", "actionUndo"), false, undefined, "undo.svg"),
            actionBtn(t("REDO", "Redo"), t("REDO_TIP", "Redo the last undone action (Ctrl+Y)"), redoCount > 0, () => trigger("copaste", "actionRedo"), false, undefined, "redo.svg")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(
              t("RELOCATE", "Relocate"),
              t("RELOCATE_TIP", "Move the selected building (Tab): it follows the cursor, road snap sets the facing. Click to place, Tab or right click to cancel. To rotate freely, place it first and use RMB drag"),
              relocateReady || relocating,
              () => trigger("copaste", "actionRelocate"),
              relocating,
              "info",
              "relocate.svg"
            ),
            actionBtn(t("DELETE", "Delete"), t("DELETE_TIP", "Delete selection (Del)"), deletable > 0 && !pasteMode, () => trigger("copaste", "actionDelete"), false, "danger", "trash.svg")
          )
        ),
        section(
          t("SEC_ALIGN", "Align"),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(t("GROUND", "Ground"), t("GROUND_TIP", "Snap selection to terrain (End)"), heightCount > 0 || pasteMode, () => trigger("copaste", "actionSnapGround"), false, undefined, "ground.svg"),
            actionBtn(t("MATCHH", "Match H"), t("MATCHH_TIP", "Pick height from a prop (Home)"), heightCount > 0 && !pasteMode, () => trigger("copaste", "actionMatchHeight"), heightPickArmed, undefined, "matchh.svg")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("rotl.svg", t("ROTL_TIP", "Rotate 45° left"), selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", -45), false),
            actionBtn("rotr.svg", t("ROTR_TIP", "Rotate 45° right"), selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", 45), false),
            actionBtn("up.svg", t("RAISE_TIP", "Raise 0.5 m (PgUp)"), heightCount > 0 || pasteMode, () => trigger("copaste", "actionHeight", 1), false),
            actionBtn("down.svg", t("LOWER_TIP", "Lower 0.5 m (PgDn)"), heightCount > 0 || pasteMode, () => trigger("copaste", "actionHeight", -1), false)
          ),
          h(
            "div",
            { className: "copasteSubTitleRow" },
            h(
              "div",
              { className: "copasteSectionTitle copasteSubTitleFlat" },
              t("SEC_ALIGN_GAP", "Align props") + (alignGapLive > 0 ? " · " + alignGapLive.toFixed(1) + " m" : "")
            ),
            withTooltip(
              t("GAP_TIP", "Gap in meters for the align tools (empty = auto). Adjusts a live align too — same as [ and ] keys"),
              h(
                "div",
                { className: "copasteStepper" },
                h(
                  "button",
                  { className: "copasteStepperBtn", onClick: () => stepGap(-1) },
                  h("img", { src: "coui://copaste/chevl.svg" })
                ),
                h("input", {
                  className: "copasteStepperInput",
                  value: gapDisplay,
                  placeholder: t("GAP_AUTO", "auto"),
                  onChange: (e) => setAlignGap(e.target.value),
                  onFocus: () => {
                    setGapFocused(true);
                    setAlignGap(gapDisplay);
                    trigger("copaste", "setTyping", true);
                  },
                  onKeyDown: (e) => {
                    // Enter odmah primenjuje ukucani razmak na živu sesiju.
                    if (e.key === "Enter") {
                      if (alignGapLive > 0) {
                        trigger("copaste", "setAlignGapLive", alignGap);
                      }
                      if (e.target && e.target.blur) {
                        e.target.blur();
                      }
                    }
                  },
                  onBlur: () => {
                    setGapFocused(false);
                    trigger("copaste", "setTyping", false);
                    // Ručno ukucana vrednost odmah važi i za živu sesiju.
                    if (alignGapLive > 0) {
                      trigger("copaste", "setAlignGapLive", alignGap);
                    }
                  },
                }),
                h(
                  "button",
                  { className: "copasteStepperBtn", onClick: () => stepGap(1) },
                  h("img", { src: "coui://copaste/chevr.svg" })
                )
              )
            )
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(
              t("LINE", "Line"),
              t("LINE_TIP", "Tidy row: straight line, equal gaps AND all props rotated the same way. While lit, [ ] or the stepper adjust the gap"),
              propCount > 1 && !pasteMode,
              () => trigger("copaste", "actionAlignLine", gapDisplay),
              alignSessionSource === 1,
              undefined,
              "alignline.svg"
            ),
            actionBtn(
              t("TOPROP", "To prop"),
              t("TOPROP_TIP", "Pick a reference prop: the row goes through it, side by side along its facing, all rotated like it. RMB cancels"),
              propCount > 0 && !pasteMode,
              () => trigger("copaste", "actionAlignRef", gapDisplay),
              alignPickArmed || alignSessionSource === 2,
              undefined,
              "alignspaced.svg"
            ),
            actionBtn(
              t("CIRCLE", "Circle"),
              t("CIRCLE_TIP", "Arrange evenly on a circle (stepper = gap in meters, empty = keep size). While lit, [ ] fine-tune"),
              propCount > 2 && !pasteMode,
              () => trigger("copaste", "actionAlignCircle", gapDisplay),
              alignSessionSource === 3,
              undefined,
              "aligncircle.svg"
            )
          )
        ),
        h(
          "div",
          { key: "Blueprints", className: "copasteCard" },
          h(
            "div",
            { className: "copasteSubTitleRow", style: { margin: "0 2rem 5rem" } },
            h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, t("SEC_BLUEPRINTS", "Blueprints")),
            pager(bpPageSafe, bpPages, setBpPage)
          ),
          blueprints.length === 0
            ? h("div", { className: "copasteEmpty" }, t("NO_BLUEPRINTS", "No saved blueprints"))
            : h("div", null, bpPageItems.map(bpRow))
        )
      );

      if (nativeVanilla) {
        return h(
          "div",
          { className: "copasteVanillaDock", ref: panelRef, style: panelStyle },
          h(
            ui.Panel,
            {
              className: "copasteVanillaFrame",
              header: h(
                "div",
                { className: "copasteVanillaHeader", onMouseDown: onHeaderMouseDown },
                withTooltip(hintText, h("img", { className: "copasteTitleLogo", src: "coui://copaste/copastelogotext.svg" })),
                version ? h("div", { className: "copasteVersion" }, "v" + version) : null
              ),
            },
            content
          )
        );
      }

      return content;
    };

    moduleRegistry.append("GameTopLeft", CopasteButton);
    moduleRegistry.append("Game", CopastePanel);
    console.log("[Copaste] UI registered");
  } catch (err) {
    console.log("[Copaste] UI error: " + err);
  }
};

const hasCSS = true;

export { register as default, hasCSS };
