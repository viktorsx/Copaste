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
    const clipboardCount$ = bindValue("copaste", "clipboardCount", 0);
    const blueprints$ = bindValue("copaste", "blueprints", "");
    const undoCount$ = bindValue("copaste", "undoCount", 0);
    const sameFilter$ = bindValue("copaste", "sameFilter", "");
    const heightPickArmed$ = bindValue("copaste", "heightPickArmed", false);
    const version$ = bindValue("copaste", "version", "");
    const selectedName$ = bindValue("copaste", "selectedName", "");
    const panelX$ = bindValue("copaste", "panelX", -1);
    const panelY$ = bindValue("copaste", "panelY", -1);
    const randomVariation$ = bindValue("copaste", "randomVariation", false);
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
      const clipboard = useValue(clipboardCount$);
      const blueprintsRaw = useValue(blueprints$);
      const undoCount = useValue(undoCount$);
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
          const fx = Math.round(baseX + (ev.clientX - startX));
          const fy = Math.round(baseY + (ev.clientY - startY));
          setDragPos({ x: fx, y: fy });
          trigger("copaste", "setPanelPos", fx + "," + fy);
        };
        window.addEventListener("mousemove", onMove);
        window.addEventListener("mouseup", onUp);
      };

      const blueprints = blueprintsRaw ? blueprintsRaw.split("\n").filter((n) => n.length > 0) : [];

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
          ? " copasteBtnActive"
          : !enabled
          ? " copasteBtnDisabled"
          : variant === "primary"
          ? " copasteBtnPrimary"
          : variant === "danger"
          ? " copasteBtnDanger"
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

      const stat = (label, value, pad) =>
        h("div", { className: "copasteStat" + (pad ? " copasteStatPad" : "") },
          h("div", { className: "copasteStatValue" + (value > 0 ? " copasteStatValueLive" : "") }, String(value)),
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
            name
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
          ))
        );
      };

      return h(
        "div",
        { className: "copastePanel", ref: panelRef, style: panelStyle },
        h(
          "div",
          { className: "copasteHeader", onMouseDown: onHeaderMouseDown },
          h("div", { className: "copasteLogo" }, h("img", { src: "coui://copaste/copaste.svg" })),
          h("div", { className: "copasteTitle" }, "COPASTE"),
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
            stat("Clipboard", clipboard, true)
          ),
          selectedName
            ? h(
                "div",
                { className: "copasteFilterRow" },
                h("img", { src: "coui://copaste/prop.svg" }),
                h("div", { className: "copasteFilterLabel" }, "Prop"),
                h("div", { className: "copasteFilterValue copastePropName" }, selectedName)
              )
            : null,
          sameFilter
            ? h(
                "div",
                { className: "copasteFilterRow" },
                h("img", { src: "coui://copaste/filter.svg" }),
                h("div", { className: "copasteFilterLabel" }, "Filter"),
                h("div", { className: "copasteFilterValue" }, sameFilter)
              )
            : null
        ),
        selectionEntries.length > 0
          ? h(
              "div",
              { className: "copasteCard" },
              h("div", { className: "copasteSectionTitle" }, "Selected props"),
              h(
                "div",
                { className: "copasteBpList" },
                selectionEntries.map((entry, i) =>
                  h(
                    "button",
                    {
                      key: entry.id + "-" + i,
                      className: "copasteBpLoad copasteSelRow",
                      onClick: () => trigger("copaste", "selectOnlyProp", entry.id),
                      onMouseEnter: () => trigger("copaste", "focusProp", entry.id),
                      onMouseLeave: () => trigger("copaste", "focusProp", ""),
                    },
                    entry.name
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
          )
        ),
        section(
          "Edit",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Undo", "Undo last action (Ctrl+Z)", undoCount > 0, () => trigger("copaste", "actionUndo"), false, undefined, "undo.svg"),
            actionBtn("Delete", "Delete selection (Del)", selected > 0 && !pasteMode, () => trigger("copaste", "actionDelete"), false, "danger", "trash.svg"),
            actionBtn("Filter", "Type filter for marquee (T)", (selected > 0 || sameFilter) && !pasteMode, () => trigger("copaste", "actionSelectSame"), !!sameFilter, undefined, "filterw.svg")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Ground", "Snap selection to terrain (End)", selected > 0 || pasteMode, () => trigger("copaste", "actionSnapGround"), false, undefined, "ground.svg"),
            actionBtn("Match H", "Pick height from a prop (Home)", selected > 0 && !pasteMode, () => trigger("copaste", "actionMatchHeight"), heightPickArmed, undefined, "matchh.svg")
          )
        ),
        section(
          "Rotate & height",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("rotl.svg", "Rotate 45° left", selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", -45), false),
            actionBtn("rotr.svg", "Rotate 45° right", selected > 0 || pasteMode, () => trigger("copaste", "actionRotate", 45), false),
            actionBtn("up.svg", "Raise 0.5 m (PgUp)", selected > 0 || pasteMode, () => trigger("copaste", "actionHeight", 1), false),
            actionBtn("down.svg", "Lower 0.5 m (PgDn)", selected > 0 || pasteMode, () => trigger("copaste", "actionHeight", -1), false)
          ),
          h(
            "div",
            { className: "copasteSubTitleRow" },
            h(
              "div",
              { className: "copasteSectionTitle copasteSubTitleFlat" },
              "Align" + (alignGapLive > 0 ? " · " + alignGapLive.toFixed(1) + " m" : "")
            ),
            withTooltip(
              "Gap in meters for Spaced and Circle (empty = auto). Adjusts a live align too — same as [ and ] keys",
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
              selected > 1 && !pasteMode,
              () => trigger("copaste", "actionAlignLine", alignGap),
              alignSessionSource === 1,
              undefined,
              "alignline.svg"
            ),
            actionBtn(
              "To prop",
              "Pick a reference prop: the row goes through it, side by side along its facing, all rotated like it. RMB cancels",
              selected > 0 && !pasteMode,
              () => trigger("copaste", "actionAlignRef", alignGap),
              alignPickArmed || alignSessionSource === 2,
              undefined,
              "alignspaced.svg"
            ),
            actionBtn(
              "Circle",
              "Arrange evenly on a circle (stepper = gap in meters, empty = keep size). While lit, [ ] fine-tune",
              selected > 2 && !pasteMode,
              () => trigger("copaste", "actionAlignCircle", alignGap),
              alignSessionSource === 3,
              undefined,
              "aligncircle.svg"
            )
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
          )
        ),
        section(
          "Blueprints",
          blueprints.length === 0
            ? h("div", { className: "copasteEmpty" }, "No saved blueprints")
            : h("div", { className: "copasteBpList" }, blueprints.map(bpRow))
        ),
        h(
          "div",
          { className: "copasteHint" },
          pasteMode
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
