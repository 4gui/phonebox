// WebView below Chromium 105 has no container query units, so cq83.css reads --cqw / --cqh instead.
// This file measures every widget and keeps those two variables in sync with its real box.
(function () {
  if (window.CSS && CSS.supports && CSS.supports('container-type: size')) return;

  var SEL = '.w, .wc, .radio-carousel';
  var pending = [];
  var queued = false;

  // Measured on the border box, never the content box. cq83.css derives padding and font size from
  // --cqw / --cqh, so a content-box measurement feeds its own output back in: bigger padding means a
  // smaller content box, which means smaller padding, and the pair never settles - that is what the
  // "ResizeObserver loop limit exceeded" banner was. A widget's border box is fixed by the grid, so it
  // stays put while these variables are written. It also matches how the desktop layout was checked.
  function measure(el) {
    if (!el.isConnected) return;
    var w = el.offsetWidth;
    var h = el.offsetHeight;
    if (w < 1 || h < 1) return;
    if (el.__cw === w && el.__ch === h) return;
    el.__cw = w;
    el.__ch = h;
    el.style.setProperty('--cqw', w / 100 + 'px');
    el.style.setProperty('--cqh', h / 100 + 'px');
  }

  function flush() {
    queued = false;
    var list = pending;
    pending = [];
    for (var i = 0; i < list.length; i++) {
      list[i].__cqqueued = 0;
      measure(list[i]);
    }
  }

  // Writes are deferred to the next frame: doing them inside the observer callback would re-trigger
  // the observer within the same delivery cycle.
  function queue(el) {
    if (el.__cqqueued) return;
    el.__cqqueued = 1;
    pending.push(el);
    if (!queued) {
      queued = true;
      requestAnimationFrame(flush);
    }
  }

  var ro = window.ResizeObserver ? new ResizeObserver(function (entries) {
    for (var i = 0; i < entries.length; i++) queue(entries[i].target);
  }) : null;

  function scan() {
    var nodes = document.querySelectorAll(SEL);
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      if (el.__cqseen) continue;
      el.__cqseen = 1;
      measure(el);
      if (ro) ro.observe(el);
    }
  }

  scan();
  // Pages and widgets are built after this script runs, so pick them up as they appear.
  new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('resize', scan);
  window.addEventListener('orientationchange', function () { setTimeout(scan, 150); });
})();
