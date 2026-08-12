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
      const actionBtn = (content, tooltip, enabled, onClick, isActive, variant) => {
        const inner =
          typeof content === "string" && content.endsWith(".svg")
            ? h("img", { src: "coui://copaste/" + content })
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
        section(
          "Clipboard",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Copy", "Copy selection (Ctrl+C)", selected > 0 && !pasteMode, () => trigger("copaste", "actionCopy"), false, "primary"),
            actionBtn("Paste", "Paste mode on/off (Ctrl+V)", clipboard > 0, () => trigger("copaste", "actionPaste"), pasteMode),
            selected > 0
              ? actionBtn("Save", "Save selection as blueprint", true, () => trigger("copaste", "saveBlueprint"), false)
              : null
          )
        ),
        section(
          "Edit",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Undo", "Undo last action (Ctrl+Z)", undoCount > 0, () => trigger("copaste", "actionUndo"), false),
            actionBtn("Delete", "Delete selection (Del)", selected > 0 && !pasteMode, () => trigger("copaste", "actionDelete"), false, "danger"),
            actionBtn("Filter", "Type filter for marquee (T)", (selected > 0 || sameFilter) && !pasteMode, () => trigger("copaste", "actionSelectSame"), !!sameFilter)
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Ground", "Snap selection to terrain (End)", selected > 0 || pasteMode, () => trigger("copaste", "actionSnapGround"), false),
            actionBtn("Match H", "Pick height from a prop (Home)", selected > 0 && !pasteMode, () => trigger("copaste", "actionMatchHeight"), heightPickArmed)
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
            { className: "copasteSectionTitle copasteSubTitle" },
            "Align" + (alignGapLive > 0 ? " · " + alignGapLive.toFixed(1) + " m (Alt+←/→)" : "")
          ),
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Line", "Line up the selection between its two farthest props", selected > 1 && !pasteMode, () => trigger("copaste", "actionAlign", 0), false),
            actionBtn(
              "Spaced",
              "Line up with equal gaps (box = exact meters, empty = auto). Then Alt+Left/Right fine-tunes",
              selected > 1 && !pasteMode,
              () => trigger("copaste", "actionAlignSpaced", alignGap),
              false
            ),
            actionBtn(
              "Circle",
              "Arrange evenly on a circle (box = gap in meters, empty = keep size). Then Alt+Left/Right fine-tunes",
              selected > 2 && !pasteMode,
              () => trigger("copaste", "actionAlignCircle", alignGap),
              false
            ),
            withTooltip(
              "Optional gap in meters for Spaced and Circle (empty = auto)",
              h("input", {
                className: "copasteGapInput",
                value: alignGap,
                placeholder: "auto",
                onChange: (e) => setAlignGap(e.target.value),
                onFocus: () => trigger("copaste", "setTyping", true),
                onBlur: () => trigger("copaste", "setTyping", false),
              })
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
            : "Click/box: select • Drag prop: move • RMB drag: rotate • Ctrl+arrows: nudge"
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
