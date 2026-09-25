/* Shared by paired diffs and the plain/unified editor. The host owns navigation history; this small
   cache also covers switching files before the latest browser report has reached the host. */
window.createDiffNavigation = function (post, editors, visible) {
  var key = null, pending = null, restoring = false, timer = null, positions = new Map();
  function remember(id, state) {
    if (!id || !state) return;
    positions.delete(id); positions.set(id, state);
    while (positions.size > 32) positions.delete(positions.keys().next().value);
  }
  function capture() {
    clearTimeout(timer); timer = null;
    if (!key || pending || restoring || !visible()) return null;
    var values = editors().map(window.editorReadingPosition.capture);
    if (values.some(function (value) { return !value; })) return null;
    var state = { editors: values };
    remember(key, state);
    var message = { key: key, state: state };
    post('reading:' + JSON.stringify(message));
    return message;
  }
  function report() {
    if (!timer && !pending && !restoring) timer = setTimeout(capture, 80);
  }
  function gesture() { pending = null; report(); }
  // Diff-side DOM nodes do not exist until the first model is installed.
  document.addEventListener('wheel', gesture, { passive: true });
  function restore() {
    var target = pending;
    if (!target || !target.ready || !visible()) return;
    requestAnimationFrame(function () {
      if (pending !== target || !visible()) return;
      var current = editors();
      if (current.some(function (editor, i) { return editor.getModel() !== target.models[i]; })) return;
      restoring = true;
      try {
        current.forEach(function (editor, i) {
          if (target.restart) {
            editor.setPosition({ lineNumber: 1, column: 1 });
            editor.setScrollPosition({ scrollTop: 0, scrollLeft: 0 }, monaco.editor.ScrollType.Immediate);
          } else window.editorReadingPosition.restore(editor, target.state.editors[i]);
        });
      }
      finally { restoring = false; pending = null; }
      capture();
    });
  }
  return {
    remember: remember,
    capture: capture,
    // Capture the previous document before setModel resets its cursor and scroll position.
    begin: function (id, preserve, restartOnFileChange) {
      // A review page can prefer the first change on every file pick. Its first model still uses
      // the host's saved state when Back navigation creates a fresh editor for the same page.
      var restart = !preserve && restartOnFileChange && !!key && key !== id;
      capture(); key = id;
      pending = { state: preserve || restart ? null : positions.get(id), restart: restart };
    },
    shown: function (waitForDiff) {
      if (!pending || (!pending.state && !pending.restart)) { pending = null; report(); return; }
      pending.models = editors().map(function (editor) { return editor.getModel(); });
      pending.ready = pending.ready || !waitForDiff;
      restore();
    },
    diffReady: function () { if (pending) { pending.ready = true; restore(); } else report(); },
    watch: function (editor) {
      editor.onDidChangeCursorSelection(capture);
      editor.onDidScrollChange(report);
      editor.onDidBlurEditorWidget(capture);
      editor.onDidLayoutChange(restore);
      editor.onMouseDown(gesture); editor.onKeyDown(gesture);
    }
  };
};
