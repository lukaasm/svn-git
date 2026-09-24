/* Reading anchors for an explicit reload of the same file. Unchanged prefixes/suffixes follow
   inserted or removed lines; replaced ranges clamp to the nearest surviving line. */
window.captureDiffReading = function (diff) {
  return [diff.getOriginalEditor(), diff.getModifiedEditor()].map(function (editor) {
    var model = editor.getModel(), visible = editor.getVisibleRanges()[0];
    if (!model) return null;
    var line = visible ? visible.startLineNumber : 1;
    return { editor: editor, view: editor.saveViewState(), lines: model.getLinesContent(),
      selection: editor.getSelection(), line: line, offset: editor.getScrollTop() - editor.getTopForLineNumber(line),
      left: editor.getScrollLeft() };
  });
};
window.restoreDiffReading = function (states) {
  states.forEach(function (state) {
    if (!state || !state.editor.getModel()) return;
    var editor = state.editor, model = editor.getModel(), before = state.lines, after = model.getLinesContent();
    var prefix = 0, suffix = 0;
    while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix++;
    while (suffix < before.length - prefix && suffix < after.length - prefix && before[before.length - suffix - 1] === after[after.length - suffix - 1]) suffix++;
    function map(line) {
      if (line > before.length - suffix) line += after.length - before.length;
      else if (line > prefix) line = Math.min(line, Math.max(prefix + 1, after.length - suffix));
      return Math.max(1, Math.min(after.length, line));
    }
    editor.restoreViewState(state.view);
    if (state.selection) {
      var s = state.selection;
      editor.setSelection(new monaco.Selection(map(s.selectionStartLineNumber), s.selectionStartColumn, map(s.positionLineNumber), s.positionColumn));
    }
    editor.setScrollPosition({ scrollTop: editor.getTopForLineNumber(map(state.line)) + state.offset, scrollLeft: state.left });
  });
};
