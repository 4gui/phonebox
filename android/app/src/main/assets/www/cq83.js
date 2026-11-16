// WebView below Chromium 105 has no container query units, so cq83.css reads --cqw / --cqh instead.
// This file measures every widget and keeps those two variables in sync with its real content box.
(function () {
  if (window.CSS && CSS.supports && CSS.supports('container-type: size')) return;

  var SEL = '.w, .wc, .radio-carousel';

  function set(el) {
    var cs = getComputedStyle(el);
    var w = el.clientWidth - (parseFloat(cs.paddingLeft) || 0) - (parseFloat(cs.paddingRight) || 0);
    var h = el.clientHeight - (parseFloat(cs.paddingTop) || 0) - (parseFloat(cs.paddingBottom) || 0);
    if (w < 1) w = 1;
    if (h < 1) h = 1;
    if (el.__cw === w && el.__ch === h) return;
    el.__cw = w;
    el.__ch = h;
    el.style.setProperty('--cqw', w / 100 + 'px');
    el.style.setProperty('--cqh', h / 100 + 'px');
  }

  var ro = window.ResizeObserver ? new ResizeObserver(function (entries) {
    for (var i = 0; i < entries.length; i++) set(entries[i].target);
  }) : null;

  function scan() {
    var nodes = document.querySelectorAll(SEL);
    for (var i = 0; i < nodes.length; i++) {
      var el = nodes[i];
      if (el.__cqseen) continue;
      el.__cqseen = 1;
      set(el);
      if (ro) ro.observe(el);
    }
  }

  scan();
  // Pages and widgets are built after this script runs, so pick them up as they appear.
  new MutationObserver(scan).observe(document.documentElement, { childList: true, subtree: true });
  window.addEventListener('resize', scan);
  window.addEventListener('orientationchange', function () { setTimeout(scan, 150); });
})();
