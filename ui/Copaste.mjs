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
    const h = React.createElement;

    const toolActive$ = bindValue("copaste", "toolActive", false);
    const pasteMode$ = bindValue("copaste", "pasteMode", false);
    const selectedCount$ = bindValue("copaste", "selectedCount", 0);
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

    // Vanila floating Button iz cs2/ui — isti šablon kao Traffic i Node Controller
    // (Button + variant:"floating" + svg sa width/height 100%). Bez naših klasa:
    // pozadinu i selected izgled diktira igra/tema mod (npr. Redesigned Top Buttons).

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
      const panelStyle = hasPos ? { top: posY + "px", left: posX + "px" } : undefined;

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
          tooltip + " • Right click: just this one / everything",
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
              onBlur: () => commitRename(name),
            })
          );
        }

        return h(
          "div",
          { key: name, className: "copasteBpRow" },
          withTooltip("Load and start pasting", h(
            "button",
            { className: "copasteBpLoad", onClick: () => trigger("copaste", "loadBlueprint", name) },
            // Ellipsis nosi unutrašnji div (dugme je flex kontejner).
            h("div", { className: "copasteSelName" }, name)
          )),
          withTooltip("Rename", h(
            "button",
            {
              className: "copasteBpIcon",
              onClick: () => {
                setRenaming(name);
                setRenameValue(name);
                trigger("copaste", "setTyping", true);
              },
            },
            h("img", { src: "coui://copaste/rename.svg" })
          )),
          withTooltip("Delete blueprint", h(
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

      return h(
        "div",
        { className: "copastePanel", ref: panelRef, style: panelStyle },
        h(
          "div",
          { className: "copasteHeader", onMouseDown: onHeaderMouseDown },
          h("div", { className: "copasteLogo" }, h("img", { src: "coui://copaste/copaste.svg" })),
          h("img", { className: "copasteTitleLogo", src: "coui://copaste/copastelogotext.svg" }),
          version ? h("div", { className: "copasteVersion" }, "v" + version) : null
        ),
        h(
          "div",
          { className: "copasteCard copasteCardFlush", style: { marginTop: "0" } },
          h(
            "div",
            { className: "copasteStatsRow" },
            stat("Selected", selected, false),
            h("div", { className: "copasteStatDivider" }),
            stat(
              "Clipboard",
              clipboard,
              true,
              clipboard > 0
                ? withTooltip(
                    "Clear the clipboard",
                    h(
                      "button",
                      { className: "copasteStatClear", onClick: () => trigger("copaste", "clearClipboard") },
                      "Clear"
                    )
                  )
                : null
            )
          ),
          selectedName
            ? h(
                "div",
                { className: "copasteFilterRow copastePropRow" },
                h("img", { src: "coui://copaste/prop.svg" }),
                h("div", { className: "copasteFilterLabel" }, "Prop"),
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
              "Marquee prop filter: box select only picks this prop type. Click a prop to set it, click again to turn off (T)",
              h(
                "button",
                { className: "copasteFilterBtn", onClick: () => trigger("copaste", "actionSelectSame") },
                h("img", { src: "coui://copaste/filter.svg" }),
                h("span", null, "Filter")
              )
            ),
            h(
              "div",
              { className: "copasteFilterValue copasteFilterValueClip" + (sameFilter ? "" : " copasteFilterValueOff") },
              sameFilter || "Off"
            )
          )
        ),
        h(
          "div",
          { key: "Selection", className: "copasteCard" },
          h("div", { className: "copasteSectionTitle" }, "Selection"),
          h(
            "div",
            { className: "copasteChipRow" },
            chip("Props", 1, "Benches, lamps, fences and other props", "prop.svg"),
            chip("Trees", 2, "Trees, bushes and other vegetation", "tree.svg"),
            chip("Decals", 4, "Road markings, stains and other decals", "decal.svg")
          ),
          h(
            "div",
            { className: "copasteChipRow" },
            chip("Surfaces", 8, "Painted surfaces", "surface.svg"),
            chip("Buildings", 16, "Service buildings, unique ones and grown homes", "building.svg")
          ),
          h(
            "div",
            { className: "copasteSubTitleRow" },
            h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, "Building elements"),
            withTooltip(
              "When on, selection also reaches elements that belong to buildings — their props, trees, decals and lot surfaces, each following its filter above. Deleting a lot decoration surface also removes what it keeps spawning. When off, nothing building-owned can be selected, not even by click",
              h(
                "button",
                {
                  className: "copasteSwitch" + (buildingProps ? " copasteSwitchOn" : ""),
                  onClick: () => trigger("copaste", "setBuildingProps", !buildingProps),
                },
                buildingProps ? "On" : "Off"
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
                h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, "Selected props"),
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
          "Clipboard",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Copy", "Copy selection (Ctrl+C)", selected > 0 && !pasteMode, () => trigger("copaste", "actionCopy"), false, "primary", "copy.svg"),
            actionBtn("Paste", "Paste mode on/off (Ctrl+V)", clipboard > 0, () => trigger("copaste", "actionPaste"), pasteMode, undefined, "paste.svg"),
            selected > 0
              ? actionBtn("Save", "Save selection as blueprint", true, () => trigger("copaste", "saveBlueprint"), false, undefined, "save.svg")
              : null
          ),
          h("div", { className: "copasteSectionTitle copasteSubTitle" }, "Paste look"),
          withTooltip(
            "Original: pasted props keep the copied prop's color variation. Random: the game picks one",
            h(
              "div",
              { className: "copasteToggleTrack" },
              h(
                "div",
                {
                  className: "copasteToggleOpt" + (!randomVariation ? " copasteToggleOptActive" : ""),
                  onClick: () => trigger("copaste", "setRandomVariation", false),
                },
                "Original"
              ),
              h(
                "div",
                {
                  className: "copasteToggleOpt" + (randomVariation ? " copasteToggleOptActive" : ""),
                  onClick: () => trigger("copaste", "setRandomVariation", true),
                },
                "Random"
              )
            )
          ),
          // Road snap red samo dok je Buildings filter uključen — da ne buni
          // kod čistog prop kopiranja (snap se ionako pali samo uz zgradu).
          (selectionFilters & 16)
            ? h(
                "div",
                { className: "copasteSubTitleRow" },
                h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, "Road snap"),
                withTooltip(
                  "Pasted buildings snap to the nearest road like normal plopping — the whole group rotates to face it. While snapped, rotation follows the road",
                  h(
                    "button",
                    {
                      className: "copasteSwitch" + (roadSnap ? " copasteSwitchOn" : ""),
                      onClick: () => trigger("copaste", "setRoadSnap", !roadSnap),
                    },
                    roadSnap ? "On" : "Off"
                  )
                )
              )
            : null
        ),
        section(
          "Edit",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Undo", "Undo last action (Ctrl+Z)", undoCount > 0, () => trigger("copaste", "actionUndo"), false, undefined, "undo.svg"),
            actionBtn("Redo", "Redo the last undone action (Ctrl+Y)", redoCount > 0 && !pasteMode, () => trigger("copaste", "actionRedo"), false, undefined, "redo.svg")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn(
              "Relocate",
              "Move the selected building (Tab): it follows the cursor, road snap sets the facing. Click to place, Tab or right click to cancel. To rotate freely, place it first and use RMB drag",
              relocateReady || relocating,
              () => trigger("copaste", "actionRelocate"),
              relocating,
              "info",
              "relocate.svg"
            ),
            actionBtn("Delete", "Delete selection (Del)", selected > 0 && !pasteMode, () => trigger("copaste", "actionDelete"), false, "danger", "trash.svg")
          )
        ),
        section(
          "Align",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Ground", "Snap selection to terrain (End)", heightCount > 0 || pasteMode, () => trigger("copaste", "actionSnapGround"), false, undefined, "ground.svg"),
            actionBtn("Match H", "Pick height from a prop (Home)", heightCount > 0 && !pasteMode, () => trigger("copaste", "actionMatchHeight"), heightPickArmed, undefined, "matchh.svg")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("rotl.svg", "Rotate 45° left", selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", -45), false),
            actionBtn("rotr.svg", "Rotate 45° right", selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", 45), false),
            actionBtn("up.svg", "Raise 0.5 m (PgUp)", heightCount > 0 || pasteMode, () => trigger("copaste", "actionHeight", 1), false),
            actionBtn("down.svg", "Lower 0.5 m (PgDn)", heightCount > 0 || pasteMode, () => trigger("copaste", "actionHeight", -1), false)
          ),
          h(
            "div",
            { className: "copasteSubTitleRow" },
            h(
              "div",
              { className: "copasteSectionTitle copasteSubTitleFlat" },
              "Align props" + (alignGapLive > 0 ? " · " + alignGapLive.toFixed(1) + " m" : "")
            ),
            withTooltip(
              "Gap in meters for the align tools (empty = auto). Adjusts a live align too — same as [ and ] keys",
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
                  placeholder: "auto",
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
              "Line",
              "Tidy row: straight line, equal gaps AND all props rotated the same way. While lit, [ ] or the stepper adjust the gap",
              propCount > 1 && !pasteMode,
              () => trigger("copaste", "actionAlignLine", gapDisplay),
              alignSessionSource === 1,
              undefined,
              "alignline.svg"
            ),
            actionBtn(
              "To prop",
              "Pick a reference prop: the row goes through it, side by side along its facing, all rotated like it. RMB cancels",
              propCount > 0 && !pasteMode,
              () => trigger("copaste", "actionAlignRef", gapDisplay),
              alignPickArmed || alignSessionSource === 2,
              undefined,
              "alignspaced.svg"
            ),
            actionBtn(
              "Circle",
              "Arrange evenly on a circle (stepper = gap in meters, empty = keep size). While lit, [ ] fine-tune",
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
            h("div", { className: "copasteSectionTitle copasteSubTitleFlat" }, "Blueprints"),
            pager(bpPageSafe, bpPages, setBpPage)
          ),
          blueprints.length === 0
            ? h("div", { className: "copasteEmpty" }, "No saved blueprints")
            : h("div", null, bpPageItems.map(bpRow))
        ),
        h(
          "div",
          { className: "copasteHint" },
          relocating
            ? "Building follows the cursor • Road snap sets the facing • Click: place • Tab/RMB: cancel"
            : pasteMode
            ? "Click: place • RMB drag: rotate • PgUp/PgDn: height • RMB: back"
            : heightPickArmed
            ? "Click a prop to copy its height • RMB: cancel"
            : alignPickArmed
            ? "Click a reference prop: row through it, all rotated like it • RMB: cancel"
            : "Click/box: select • Ctrl+click: pick overlapped • Alt+drag: move one • Alt+wheel: spin each • RMB drag: rotate"
        )
      );
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
