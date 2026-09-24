/* Inline review presentation. The host owns persistence, validation and all mutations. */
window.createReviewThreads = function (diff, post) {
  var documentId = 0, threads = [], expanded = null;
  var editors = { original: diff.getOriginalEditor(), modified: diff.getModifiedEditor() };
  var decorations = {}, subscriptions = [], commentKeys = [];

  function element(tag, className, text) {
    var node = document.createElement(tag);
    node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function button(label, action, parent) {
    var node = element('button', 'sg-review-button', label);
    node.type = 'button'; node.addEventListener('click', action); parent.appendChild(node);
    return node;
  }
  function close(focus) {
    if (!expanded) return;
    var old = expanded; expanded = null;
    old.observer.disconnect(); old.layout.dispose();
    old.editor.changeViewZones(function (accessor) { accessor.removeZone(old.id); });
    if (focus) old.editor.focus();
  }
  function reveal(id, retained) {
    var thread = threads.find(function (t) { return t.id === id; });
    if (!thread) return;
    close(false);
    var editor = editors[thread.side], model = editor.getModel();
    if (!model || thread.first < 1 || thread.last > model.getLineCount()) return;
    var group = threads.filter(function (t) { return t.side === thread.side && t.first === thread.first; });
    var zoneNode = element('div', 'sg-review-zone');
    var panel = element('section', 'sg-review-panel'); zoneNode.appendChild(panel);
    panel.setAttribute('aria-label', 'Code review at line ' + thread.first);
    var heading = element('header', 'sg-review-heading'); panel.appendChild(heading);
    heading.appendChild(element('strong', '', 'Comments · ' + thread.side + ' line ' + thread.first));
    var closeButton = button('Close ×', function () { close(true); }, heading);
    closeButton.setAttribute('aria-label', 'Close inline comments');
    var history = element('div', 'sg-review-history'); panel.appendChild(history);
    var token = documentId, selected = null;
    group.forEach(function (item) {
      var card = element('article', 'sg-review-thread'); card.dataset.threadId = item.id;
      history.appendChild(card);
      var status = item.conflict ? 'Concurrent feedback · needs review' : item.state === 'resolved' ? 'Resolved' : 'Open';
      card.appendChild(element('span', 'sg-review-status ' + (item.conflict ? 'conflict' : item.state), (item.state === 'resolved' ? '✓ ' : '● ') + status));
      if (item.last !== item.first) card.appendChild(element('span', 'sg-review-range', 'Lines ' + item.first + '–' + item.last));
      if (item.messages.length > 20) card.appendChild(element('p', 'sg-review-meta', 'Showing the latest 20 entries. Earlier history is in the comments pane.'));
      item.messages.slice(-20).forEach(function (message) {
        var meta = element('div', 'sg-review-meta'), author = element('span', 'sg-review-author', message.actor);
        author.style.color = 'var(--sg-user-' + message.actorColor + ')';
        meta.appendChild(author); meta.appendChild(document.createTextNode(' · ' + message.action + ' · ' + message.at)); card.appendChild(meta);
        card.appendChild(element('p', 'sg-review-body', message.body));
      });
      var actions = element('div', 'sg-review-actions'); card.appendChild(actions);
      function invoke(action) { post('review:' + JSON.stringify({ document: token, id: item.id, action: action })); }
      var reply = button('↩ Reply', function () { invoke('reply'); }, actions); reply.dataset.action = 'reply';
      var action = item.state === 'resolved' ? 'reopen' : 'resolve';
      var address = button(action === 'resolve' ? '✓ Resolve' : '↻ Reopen', function () { invoke(action); }, actions);
      address.dataset.action = action;
      if (item.id === id) selected = reply;
    });
    panel.addEventListener('keydown', function (event) { if (event.key === 'Escape') { event.stopPropagation(); close(true); } });
    var zone = { afterLineNumber: thread.last, heightInPx: 120, domNode: zoneNode, suppressMouseDown: false };
    var zoneId;
    editor.changeViewZones(function (accessor) { zoneId = accessor.addZone(zone); });
    function resize() {
      if (!expanded || expanded.id !== zoneId) return;
      panel.style.width = Math.max(120, editor.getLayoutInfo().contentWidth - 16) + 'px';
      var height = Math.ceil(panel.getBoundingClientRect().height) + 16;
      if (zone.heightInPx !== height) {
        zone.heightInPx = height;
        editor.changeViewZones(function (accessor) { accessor.layoutZone(zoneId); });
      }
    }
    var observer = new ResizeObserver(resize);
    expanded = { id: zoneId, thread: id, panel: panel, history: history, signature: JSON.stringify(group), editor: editor, observer: observer, layout: editor.onDidLayoutChange(resize) };
    observer.observe(panel); resize();
    if (retained) {
      // Monaco attaches/measures a replacement view zone on its next render. Before that the
      // history has no scroll range, so assigning scrollTop immediately silently clamps to zero.
      requestAnimationFrame(function () {
        if (!expanded || expanded.id !== zoneId) return;
        history.scrollTop = retained.history;
        if (retained.focus) {
          var card = Array.from(panel.querySelectorAll('[data-thread-id]')).find(function (n) { return n.dataset.threadId === retained.focus.thread; });
          var target = card && Array.from(card.querySelectorAll('[data-action]')).find(function (n) { return n.dataset.action === retained.focus.action; });
          if (!target && card) { target = card.querySelector('.sg-review-status'); target.tabIndex = -1; }
          if (target) target.focus({ preventScroll: true });
        }
      });
    } else {
      editor.revealLineNearTop(thread.first);
      if (selected) selected.focus({ preventScroll: true });
      post('review:' + JSON.stringify({ document: documentId, id: id, action: 'select' }));
    }
  }
  function clear() {
    close(false); threads = [];
    Object.keys(editors).forEach(function (side) { decorations[side].clear(); editors[side].updateOptions({ glyphMargin: false }); });
  }
  Object.keys(editors).forEach(function (side) {
    var editor = editors[side];
    var commentKey = editor.createContextKey('sgCanComment', false);
    commentKeys.push(commentKey);
    subscriptions.push(editor.addAction({
      id: 'sg.comment', label: 'Comment on selected lines',
      precondition: 'sgCanComment', contextMenuGroupId: 'sg', contextMenuOrder: 0,
      run: function () {
        var selection = editor.getSelection(), model = editor.getModel();
        if (!selection || !model || !model.getValueLength()) return;
        // A selection ending at column one excludes that final line, as in other editor commands.
        var last = selection.endLineNumber - (selection.endColumn === 1 && selection.endLineNumber > selection.startLineNumber ? 1 : 0);
        post('review-comment:' + JSON.stringify({ document: documentId, side: side, first: selection.startLineNumber, last: last }));
      }
    }));
    decorations[side] = editor.createDecorationsCollection();
    subscriptions.push(editor.onMouseDown(function (event) {
      if (event.target.type !== monaco.editor.MouseTargetType.GUTTER_GLYPH_MARGIN || !event.target.position) return;
      var thread = threads.find(function (t) { return t.side === side && t.first === event.target.position.lineNumber; });
      if (thread) reveal(thread.id);
    }));
  });
  return {
    allowComments: function (enabled) { commentKeys.forEach(function (key) { key.set(enabled); }); },
    set: function (message) {
      var sameDocument = documentId === message.document, old = expanded, retained = null;
      var views = sameDocument ? Object.keys(editors).map(function (side) { return [editors[side], editors[side].saveViewState()]; }) : [];
      if (!sameDocument) clear();
      documentId = message.document; threads = message.threads;
      if (sameDocument && old) {
        var next = threads.find(function (t) { return t.id === old.thread; });
        var group = next && threads.filter(function (t) { return t.side === next.side && t.first === next.first; });
        if (!next || JSON.stringify(group) !== old.signature) {
          var active = document.activeElement, card = active && active.closest('[data-thread-id]');
          retained = next ? { id: next.id, history: old.history.scrollTop,
            focus: document.hasFocus() && old.panel.contains(active) && card ? { thread: card.dataset.threadId, action: active.dataset.action } : null } : null;
          close(false);
        }
      }
      Object.keys(editors).forEach(function (side) {
        var groups = new Map(), editor = editors[side], model = editor.getModel();
        threads.filter(function (t) { return t.side === side && model && t.first > 0 && t.last <= model.getLineCount(); }).forEach(function (t) {
          if (!groups.has(t.first)) groups.set(t.first, []);
          groups.get(t.first).push(t);
        });
        editor.updateOptions({ glyphMargin: threads.length > 0 });
        decorations[side].set(Array.from(groups, function (entry) {
          var line = entry[0], group = entry[1], open = group.some(function (t) { return t.state === 'open'; }), conflict = group.some(function (t) { return t.conflict; });
          return { range: new monaco.Range(line, 1, line, 1), options: {
            glyphMarginClassName: 'sg-review-glyph ' + (conflict ? 'sg-review-conflict' : open ? 'sg-review-open' : 'sg-review-resolved'),
            glyphMarginHoverMessage: { value: group.length + ' comment thread(s). Click to open, or use Show in code in the comments pane.' },
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges
          } };
        }));
      });
      if (retained) reveal(retained.id, retained);
      views.forEach(function (view) { view[0].restoreViewState(view[1]); });
    },
    reveal: function (message) { if (message.document === documentId) reveal(message.id); },
    close: function () { close(false); },
    clear: clear,
    dispose: function () { clear(); subscriptions.forEach(function (s) { s.dispose(); }); }
  };
};
