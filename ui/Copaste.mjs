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

    const withTooltip = (tooltip, element) =>
      ui.Tooltip ? h(ui.Tooltip, { tooltip: tooltip }, element) : element;

    // Vanila ToolButton (isti kao Node Controller i ostali) + naše accent boje iz Copaste.css.
    let ToolButton = null;
    try {
      const tb = moduleRegistry.registry.get(
        "game-ui/game/components/tool-options/tool-button/tool-button.tsx"
      );
      ToolButton = tb ? tb.ToolButton : null;
    } catch (e) {
      console.log("[Copaste] ToolButton unavailable: " + e);
    }

    const CopasteButton = () => {
      const active = useValue(toolActive$);
      const onToggle = () => trigger("copaste", "toggleTool");

      let button;
      if (ToolButton) {
        button = h(ToolButton, {
          src: "coui://copaste/copaste.svg",
          selected: active,
          onSelect: onToggle,
          className: "copasteToggle" + (active ? " copasteToggleSelected" : ""),
        });
      } else {
        button = h(
          "button",
          {
            onClick: onToggle,
            style: {
              width: "40rem",
              height: "40rem",
              borderRadius: "10rem",
              border: "none",
              backgroundColor: active ? "#0e9cd8" : "#45b8e6",
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
      const version = useValue(version$);

      if (!active) {
        return null;
      }

      const blueprints = blueprintsRaw ? blueprintsRaw.split("\n").filter((n) => n.length > 0) : [];

      // Dugme: label ili SVG ikonica, sa tooltip-om i vidljivim stanjem.
      const actionBtn = (content, tooltip, enabled, onClick, isActive) => {
        const inner =
          typeof content === "string" && content.endsWith(".svg")
            ? h("img", { src: "coui://copaste/" + content, style: { width: "14rem", height: "14rem" } })
            : content;

        const btn = h(
          "button",
          {
            key: tooltip,
            className:
              "copasteBtn" +
              (isActive ? " copasteBtnActive" : "") +
              (!enabled && !isActive ? " copasteBtnDisabled" : ""),
            onClick: enabled ? onClick : undefined,
          },
          inner
        );

        return withTooltip(tooltip, btn);
      };

      const section = (title, ...rows) =>
        h("div", { key: title, className: "copasteSection" },
          h("div", { className: "copasteSectionTitle" }, title),
          ...rows);

      const commitRename = (oldName) => {
        if (renameValue && renameValue !== oldName) {
          trigger("copaste", "renameBlueprint", oldName + "\n" + renameValue);
        }
        setRenaming(null);
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
                if (e.key === "Escape") setRenaming(null);
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
              },
            },
            h("img", { src: "coui://copaste/rename.svg", style: { width: "12rem", height: "12rem" } })
          )),
          withTooltip("Delete blueprint", h(
            "button",
            {
              className: "copasteBpIcon copasteBpIconDelete",
              onClick: () => trigger("copaste", "deleteBlueprint", name),
            },
            h("img", { src: "coui://copaste/delete.svg", style: { width: "12rem", height: "12rem" } })
          ))
        );
      };

      return h(
        "div",
        { className: "copastePanel" },
        h(
          "div",
          { className: "copasteTitle" },
          "COPASTE" + (version ? " v" + version : "")
        ),
        h(
          "div",
          { className: "copasteRow" },
          h("div", { className: "copasteRowLabel" }, "Selected"),
          h("div", null, String(selected))
        ),
        h(
          "div",
          { className: "copasteRow" },
          h("div", { className: "copasteRowLabel" }, "Clipboard"),
          h("div", null, String(clipboard))
        ),
        sameFilter
          ? h(
              "div",
              { className: "copasteRow" },
              h("div", { className: "copasteRowLabel" }, "Filter"),
              h("div", null, sameFilter)
            )
          : null,
        section(
          "Clipboard",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Copy", "Copy selection (Ctrl+C)", selected > 0 && !pasteMode, () => trigger("copaste", "actionCopy"), false),
            actionBtn("Paste", "Paste mode on/off (Ctrl+V)", clipboard > 0, () => trigger("copaste", "actionPaste"), pasteMode),
            actionBtn("Save", "Save selection as blueprint", selected > 0 || clipboard > 0, () => trigger("copaste", "saveBlueprint"), false)
          )
        ),
        section(
          "Edit",
          h(
            "div",
            { className: "copasteBtns" },
            actionBtn("Undo", "Undo last action (Ctrl+Z)", undoCount > 0, () => trigger("copaste", "actionUndo"), false),
            actionBtn("Delete", "Delete selection (Del)", selected > 0 && !pasteMode, () => trigger("copaste", "actionDelete"), false),
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
