/*
 * JellyDVR Space - "Move to archive..." in an item's "..." action sheet (admins, DVR recordings
 * only). Picking it opens a small window listing the archive locations that take this kind of
 * recording (TV, movies, sports; "both" locations last), with free space; choosing one moves the
 * recording there in the background (POST /JellyDvrSpace/Archive/{id}?target=...).
 *
 * Injected into jellyfin-web's index.html by install-web.sh. jellyfin-web has no plugin
 * extension point for the action sheet, so this watches the DOM for it opening - the same
 * mechanism, markup and item detection as the Comskip plugin's itemmenu.js (confirmed live on
 * this server's Jellyfin 12.0.0 web client): `.actionSheetScroller` holds
 * `button.actionSheetMenuItem` entries; the item is the `.card[data-id]` / `.listItem[data-id]`
 * whose `[data-action="menu"]` (or right-click) opened the sheet, else the details page's item.
 * Our button's click must bubble (no stopPropagation): that is what closes the sheet.
 */
(function () {
  'use strict';

  var isAdminPromise = null;
  var menuItemId = null;
  var menuClickedAt = 0;

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[JellyDvrSpace] ' + msg);
    }
  }

  function notify(message) {
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
      window.Dashboard.alert(message);
    } else {
      log(message);
    }
  }

  function checkIsAdmin() {
    if (!isAdminPromise) {
      var apiClient = window.ApiClient;
      isAdminPromise = apiClient && apiClient.getCurrentUser
        ? apiClient.getCurrentUser()
          .then(function (u) { return !!(u && u.Policy && u.Policy.IsAdministrator); })
          .catch(function () { return false; })
        : Promise.resolve(false);
    }
    return isAdminPromise;
  }

  function rememberMenuTarget(e, isContextMenu) {
    var target = e.target;
    if (!target || !target.closest) {
      return;
    }
    var holder = target.closest('.card[data-id], .listItem[data-id]');
    if (holder && holder.classList.contains('actionSheetMenuItem')) {
      return;
    }
    if (holder && (isContextMenu || target.closest('[data-action="menu"]'))) {
      menuItemId = holder.getAttribute('data-id');
      menuClickedAt = Date.now();
    } else if (target.closest('.btnMoreCommands')) {
      menuItemId = null;
      menuClickedAt = Date.now();
    }
  }

  document.addEventListener('click', function (e) { rememberMenuTarget(e, false); }, true);
  document.addEventListener('contextmenu', function (e) { rememberMenuTarget(e, true); }, true);

  function getMenuItemId() {
    if (menuItemId && Date.now() - menuClickedAt < 5000) {
      return menuItemId;
    }
    var hash = window.location.hash || '';
    var q = hash.indexOf('?');
    if (hash.indexOf('#/details') !== 0 || q === -1) {
      return null;
    }
    return new URLSearchParams(hash.slice(q + 1)).get('id');
  }

  function failureMessage(err, fallback) {
    if (!err || typeof err.json !== 'function') {
      return Promise.resolve(fallback);
    }
    return err.json()
      .then(function (body) { return (body && body.Message) || fallback; })
      .catch(function () { return fallback + ' (HTTP ' + err.status + ')'; });
  }

  function getChoices(itemId) {
    var apiClient = window.ApiClient;
    return apiClient.ajax({
      type: 'GET',
      url: apiClient.getUrl('JellyDvrSpace/Archive/' + itemId),
      dataType: 'json'
    }).catch(function () { return null; });
  }

  function gb(bytes) {
    return (bytes / 1073741824).toFixed(bytes >= 100 * 1073741824 ? 0 : 1) + ' GB';
  }

  var KIND_LABELS = { tv: 'TV', movies: 'Movies', sports: 'Sports', both: 'TV and movies' };

  // ---- the picker ---------------------------------------------------------------------------

  function el(tag, style, text) {
    var e = document.createElement(tag);
    if (style) {
      e.setAttribute('style', style);
    }
    if (text !== undefined) {
      e.textContent = text;
    }
    return e;
  }

  // A bar for one destination: what's used, what this recording would take, and what stays
  // free; the tick is the location's own free-space floor.
  function spaceBar(t, bytes) {
    var total = t.TotalBytes;
    var used = Math.max(0, total - t.FreeBytes);
    var fits = t.FreeBytes >= bytes;
    var add = fits ? bytes : t.FreeBytes;
    var belowFloor = fits && t.FreeBytes - bytes < t.KeepFreeGb * 1073741824;
    function pct(b) { return Math.max(0, Math.min(100, b / total * 100)); }

    var bar = el('div', 'position:relative;height:10px;border-radius:5px;background:rgba(255,255,255,.12);overflow:hidden;margin-top:.45em;display:flex;');
    bar.appendChild(el('div', 'height:100%;width:' + pct(used) + '%;background:#64748b;'));
    var mine = el('div', 'height:100%;min-width:' + (add > 0 ? '3px' : '0') + ';width:' + pct(add) + '%;background:'
      + (!fits ? '#dc2626' : belowFloor ? '#f59e0b' : '#00a4dc') + ';');
    mine.title = 'This recording: ' + gb(bytes);
    bar.appendChild(mine);
    if (t.KeepFreeGb > 0) {
      var tick = el('div', 'position:absolute;top:0;bottom:0;width:2px;background:#fff;opacity:.7;left:'
        + pct(total - t.KeepFreeGb * 1073741824) + '%;');
      tick.title = 'Keeps ' + t.KeepFreeGb + ' GB free';
      bar.appendChild(tick);
    }
    bar.title = gb(used) + ' used \u00b7 ' + gb(t.FreeBytes) + ' free of ' + gb(total);
    return bar;
  }

  function showPicker(itemId, choices) {
    var overlay = el('div', 'position:fixed;inset:0;z-index:100000;background:rgba(0,0,0,.6);display:flex;align-items:center;justify-content:center;padding:16px;');
    var box = el('div', 'background:#202020;color:#fff;border-radius:10px;max-width:560px;width:100%;max-height:85vh;overflow:auto;padding:1.2em 1.4em;box-shadow:0 8px 40px rgba(0,0,0,.6);font-size:95%;');
    overlay.appendChild(box);

    function close() {
      document.removeEventListener('keydown', onKey, true);
      if (overlay.parentNode) {
        overlay.parentNode.removeChild(overlay);
      }
    }
    function onKey(e) {
      if (e.key === 'Escape') {
        e.stopPropagation();
        close();
      }
    }
    overlay.addEventListener('click', function (e) {
      if (e.target === overlay) {
        close();
      }
    });
    document.addEventListener('keydown', onKey, true);

    box.appendChild(el('h2', 'margin:0 0 .3em;font-size:1.3em;font-weight:500;', 'Move to archive'));
    box.appendChild(el('div', 'opacity:.8;margin-bottom:1em;',
      choices.Name + ' \u00b7 ' + (KIND_LABELS[choices.Kind] || choices.Kind) + ' \u00b7 ' + gb(choices.Bytes)));

    if (choices.Targets.some(function (t) { return t.TotalBytes; })) {
      var legend = el('div', 'font-size:85%;opacity:.75;margin:-.4em 0 1em;display:flex;gap:1em;flex-wrap:wrap;align-items:center;');
      [['#64748b', 'used'], ['#00a4dc', 'this recording'], ['rgba(255,255,255,.12)', 'free']].forEach(function (k) {
        var item = el('span', 'display:inline-flex;align-items:center;gap:.35em;');
        item.appendChild(el('span', 'display:inline-block;width:.8em;height:.8em;border-radius:2px;background:' + k[0] + ';'));
        item.appendChild(document.createTextNode(k[1]));
        legend.appendChild(item);
      });
      var floor = el('span', 'display:inline-flex;align-items:center;gap:.35em;');
      floor.appendChild(el('span', 'display:inline-block;width:2px;height:.9em;background:#fff;opacity:.7;'));
      floor.appendChild(document.createTextNode('keep-free floor'));
      legend.appendChild(floor);
      box.appendChild(legend);
    }

    if (choices.Blocked) {
      box.appendChild(el('div', 'color:#f59e0b;margin-bottom:1em;', 'It can\'t be moved while it\'s ' + choices.Blocked + '.'));
    }

    if (!choices.Targets.length) {
      box.appendChild(el('div', 'margin-bottom:1em;',
        'No archive location takes ' + (KIND_LABELS[choices.Kind] || choices.Kind).toLowerCase()
        + ' recordings yet. Add one in Dashboard \u2192 Plugins \u2192 JellyDVR Space \u2192 Archive storage.'));
    }

    choices.Targets.forEach(function (t) {
      var usable = !t.Problem && !choices.Blocked;
      var row = el('button', 'display:block;width:100%;text-align:left;margin:0 0 .6em;padding:.7em .9em;border-radius:8px;border:1px solid rgba(255,255,255,.15);'
        + 'background:' + (usable ? 'rgba(255,255,255,.06)' : 'transparent') + ';color:inherit;font:inherit;cursor:' + (usable ? 'pointer' : 'default') + ';opacity:' + (usable ? '1' : '.55') + ';');
      row.type = 'button';
      row.disabled = !usable;
      row.appendChild(el('div', 'font-weight:500;', t.Path));
      row.appendChild(el('div', 'opacity:.75;font-size:90%;margin-top:.15em;',
        (KIND_LABELS[t.Kind] || t.Kind) + (t.FreeBytes != null
          ? ' \u00b7 ' + gb(t.FreeBytes) + ' free' + (t.TotalBytes ? ' of ' + gb(t.TotalBytes) : '')
            + (t.FreeBytes >= choices.Bytes ? ' \u00b7 ' + gb(t.FreeBytes - choices.Bytes) + ' after' : '')
          : '')));
      if (t.FreeBytes != null && t.TotalBytes) {
        row.appendChild(spaceBar(t, choices.Bytes));
      }
      if (t.Problem) {
        row.appendChild(el('div', 'color:#f87171;font-size:90%;margin-top:.15em;', t.Problem));
      } else {
        row.appendChild(el('div', 'opacity:.6;font-size:85%;margin-top:.15em;word-break:break-all;', '\u2192 ' + t.Destination));
        if (t.Note) {
          row.appendChild(el('div', 'color:#f59e0b;font-size:90%;margin-top:.15em;', t.Note));
        }
      }
      if (usable) {
        row.addEventListener('mouseenter', function () { row.style.background = 'rgba(255,255,255,.14)'; });
        row.addEventListener('mouseleave', function () { row.style.background = 'rgba(255,255,255,.06)'; });
        row.addEventListener('click', function () {
          close();
          var apiClient = window.ApiClient;
          apiClient.ajax({
            type: 'POST',
            url: apiClient.getUrl('JellyDvrSpace/Archive/' + itemId, { target: t.Path }),
            dataType: 'json'
          }).then(function (r) {
            notify((r && r.Message) || 'Moving.');
          }).catch(function (err) {
            failureMessage(err, 'Moving the recording failed.').then(notify);
          });
        });
      }
      box.appendChild(row);
    });

    var cancel = el('button', 'margin-top:.4em;padding:.5em 1.2em;border-radius:6px;border:0;background:rgba(255,255,255,.12);color:inherit;font:inherit;cursor:pointer;', 'Cancel');
    cancel.type = 'button';
    cancel.addEventListener('click', close);
    box.appendChild(cancel);

    document.body.appendChild(overlay);
  }

  // ---- the menu entry -----------------------------------------------------------------------

  function buildMenuItem(dataId, iconName, label, onClick) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    button.setAttribute('data-id', dataId);

    var icon = document.createElement('span');
    icon.className = 'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ' + iconName;
    icon.setAttribute('aria-hidden', 'true');

    var bodyText = document.createElement('div');
    bodyText.className = 'listItemBodyText actionSheetItemText';
    bodyText.textContent = label;

    var body = document.createElement('div');
    body.className = 'listItemBody actionsheetListItemBody';
    body.appendChild(bodyText);

    button.appendChild(icon);
    button.appendChild(body);
    button.addEventListener('click', onClick);
    return button;
  }

  function tryInject(scroller) {
    var itemId = getMenuItemId();
    if (!itemId) {
      return;
    }

    checkIsAdmin().then(function (isAdmin) {
      return isAdmin ? getChoices(itemId) : null;
    }).then(function (choices) {
      if (!choices || !choices.IsRecording || scroller.querySelector('[data-id^="jdvrspace-"]')) {
        return;
      }

      var button;
      if (choices.InFlight) {
        var p = choices.Progress;
        var pct = p && p.Bytes > 0 ? Math.min(99, Math.floor(p.CopiedBytes * 100 / p.Bytes)) : 0;
        button = buildMenuItem('jdvrspace-moving', 'hourglass_top', p && !p.Started ? 'Waiting to move to the archive\u2026' : 'Moving to archive\u2026 ' + pct + '%', function () {});
        button.disabled = true;
      } else {
        button = buildMenuItem('jdvrspace-archive', 'drive_file_move', 'Move to archive\u2026', function () {
          // Let the sheet close first, then ask again: the free space may have changed.
          setTimeout(function () {
            getChoices(itemId).then(function (fresh) {
              if (fresh && fresh.IsRecording) {
                showPicker(itemId, fresh);
              } else {
                notify('Could not load the archive locations.');
              }
            });
          }, 150);
        });
      }
      scroller.appendChild(button);
    });
  }

  // ---- cards of recordings being moved -------------------------------------------------
  /*
   * A recording on its way to the archive (from this menu, JDVR or the space manager) gets
   * blue stripes drifting over its card with "Moving N%" and a progress strip, and a pulsing
   * tag in list rows: the same look as the Comskip plugin's "Cutting". Admins only (GET
   * /JellyDvrSpace/Moving): every 2 s while something moves, else every 10 s.
   */
  var WORK_CSS = [
    '.jdvr-work{position:absolute;top:0;left:0;right:0;bottom:0;z-index:3;pointer-events:none;overflow:hidden;display:flex;align-items:center;justify-content:center;background-color:rgba(0,0,0,.45);background-image:repeating-linear-gradient(-45deg,rgba(var(--jw),.28) 0 10px,transparent 10px 28px);background-size:39.6px 39.6px;animation:jdvr-drift 1.2s linear infinite}',
    '@keyframes jdvr-drift{from{background-position:0 0}to{background-position:39.6px 0}}',
    '@keyframes jdvr-pulse{0%,100%{opacity:1}50%{opacity:.5}}',
    '.jdvr-work-label{background:rgba(0,0,0,.6);color:#fff;font-weight:700;font-size:13px;padding:2px 8px;border-radius:6px;white-space:nowrap}',
    '.jdvr-work-bar{position:absolute;left:0;right:0;bottom:0;height:5px;background:rgba(0,0,0,.6)}',
    '.jdvr-work-bar i{display:block;height:100%;background:rgb(var(--jw));transition:width 1.5s}',
    '.jdvr-work-pill{display:inline-block;margin-left:10px;padding:2px 8px;border-radius:999px;font-size:12px;font-weight:700;color:#fff;background:rgb(var(--jw));animation:jdvr-pulse 1.4s ease-in-out infinite;vertical-align:middle}',
    '.jdvr-work-move{--jw:0,164,220}',
    '@media (prefers-reduced-motion:reduce){.jdvr-work,.jdvr-work-pill{animation:none}}'
  ].join('\n');
  var moving = {};
  var movingAt = 0;

  function plainId(id) {
    return (id || '').replace(/-/g, '').toLowerCase();
  }

  function refreshMoving() {
    var apiClient = window.ApiClient;
    var wait = Object.keys(moving).length ? 2000 : 10000;
    if (document.hidden || !apiClient || !apiClient.getCurrentUserId || !apiClient.getCurrentUserId() || Date.now() - movingAt < wait - 100) {
      return;
    }
    movingAt = Date.now();
    checkIsAdmin().then(function (admin) {
      if (!admin) {
        return;
      }
      return apiClient.ajax({ type: 'GET', url: apiClient.getUrl('JellyDvrSpace/Moving'), dataType: 'json' }).then(function (list) {
        var next = {};
        (list || []).forEach(function (m) {
          next[plainId(m.ItemId)] = m;
          if (m.SeriesId && !next[plainId(m.SeriesId)]) {
            next[plainId(m.SeriesId)] = m;
          }
        });
        moving = next;
        renderMoving();
      });
    }).catch(function () { log('moving list request failed'); });
  }

  function renderMoving() {
    if (!document.getElementById('jdvrspace-work-style')) {
      var style = document.createElement('style');
      style.id = 'jdvrspace-work-style';
      style.textContent = WORK_CSS;
      document.head.appendChild(style);
    }
    var nodes = document.querySelectorAll('.card[data-id], .listItem[data-id]');
    for (var i = 0; i < nodes.length; i++) {
      var n = nodes[i];
      var card = n.classList.contains('card');
      var host = card ? n.querySelector('.cardScalable') : n.querySelector('.listItemBody > .listItemBodyText');
      if (!host) {
        continue;
      }
      var el = host.querySelector(card ? ':scope > .jdvr-work-move' : ':scope > .jdvr-work-pill.jdvr-work-move');
      var m = moving[plainId(n.getAttribute('data-id'))];
      if (!m) {
        if (el) {
          el.remove();
        }
        continue;
      }
      var pct = m.Bytes > 0 ? Math.min(100, m.CopiedBytes * 100 / m.Bytes) : 0;
      var text = m.Started ? '⇢ Moving ' + Math.floor(Math.min(99, pct)) + '%' : '⇢ Waiting to move';
      if (!el) {
        el = document.createElement(card ? 'div' : 'span');
        el.className = card ? 'jdvr-work jdvr-work-move' : 'jdvr-work-pill jdvr-work-move';
        if (card) {
          el.innerHTML = '<span class="jdvr-work-label"></span><div class="jdvr-work-bar"><i></i></div>';
        }
        host.appendChild(el);
      }
      if (card) {
        el.querySelector('.jdvr-work-label').textContent = text;
        el.querySelector('.jdvr-work-bar i').style.width = pct + '%';
      } else {
        el.textContent = text;
      }
    }
  }

  setInterval(function () {
    refreshMoving();
    renderMoving();
  }, 2000);

  var observer = new MutationObserver(function (mutations) {
    for (var i = 0; i < mutations.length; i++) {
      var added = mutations[i].addedNodes;
      for (var j = 0; j < added.length; j++) {
        var node = added[j];
        if (!(node instanceof HTMLElement)) {
          continue;
        }
        var scroller = node.classList && node.classList.contains('actionSheetScroller')
          ? node
          : (node.querySelector && node.querySelector('.actionSheetScroller'));
        if (scroller) {
          tryInject(scroller);
        }
      }
    }
  });

  observer.observe(document.body, { childList: true, subtree: true });
  log('archive menu script loaded');
})();
