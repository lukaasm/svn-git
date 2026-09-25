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

/* Portable anchors contain only line numbers, columns and short fingerprints. Navigation can release
   the editor and its models; a fresh read can still follow a nearby insertion or clamp a removed line. */
window.editorReadingPosition = (function () {
  function fingerprint(model, line) {
    if (line < 1 || line > model.getLineCount()) return 0;
    var text = model.getLineContent(line), hash = 2166136261;
    // Long generated lines must not turn a scroll event into a walk over megabytes of text.
    text = text.length + ':' + text.slice(0, 128) + text.slice(-128);
    for (var i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619);
    return hash >>> 0;
  }
  function anchor(model, line) {
    return { line: line, hash: fingerprint(model, line), before: fingerprint(model, line - 1), after: fingerprint(model, line + 1) };
  }
  function capture(editor) {
    var model = editor.getModel(), selection = editor.getSelection(), range = editor.getVisibleRanges()[0];
    if (!model || !selection || !range) return null;
    return { top: anchor(model, range.startLineNumber), offset: editor.getScrollTop() - editor.getTopForLineNumber(range.startLineNumber),
      left: editor.getScrollLeft(), start: anchor(model, selection.selectionStartLineNumber), startColumn: selection.selectionStartColumn,
      end: anchor(model, selection.positionLineNumber), endColumn: selection.positionColumn };
  }
  function restore(editor, state) {
    var model = editor.getModel();
    if (!model || !state || !state.top || !state.start || !state.end) return;
    var hashes = new Map();
    function hash(line) {
      if (!hashes.has(line)) hashes.set(line, fingerprint(model, line));
      return hashes.get(line);
    }
    function map(saved) {
      var line = Math.max(1, Math.min(model.getLineCount(), saved.line)), nearest = null;
      function match(candidate) {
        if (candidate < 1 || candidate > model.getLineCount() || hash(candidate) !== saved.hash) return false;
        if (nearest === null) nearest = candidate;
        return hash(candidate - 1) === saved.before && hash(candidate + 1) === saved.after;
      }
      if (match(line)) return line;
      for (var distance = 1; distance <= 2000; distance++) {
        if (line - distance < 1 && line + distance > model.getLineCount()) break;
        if (match(line - distance)) return line - distance;
        if (match(line + distance)) return line + distance;
      }
      return nearest === null ? line : nearest;
    }
    var start = map(state.start), end = map(state.end), top = map(state.top);
    editor.setSelection(new monaco.Selection(start, Math.min(state.startColumn, model.getLineMaxColumn(start)),
      end, Math.min(state.endColumn, model.getLineMaxColumn(end))));
    editor.revealLineNearTop(top, monaco.editor.ScrollType.Immediate);
    editor.setScrollPosition({ scrollTop: editor.getTopForLineNumber(top) + state.offset, scrollLeft: state.left }, monaco.editor.ScrollType.Immediate);
  }
  return { capture: capture, restore: restore };
})();
