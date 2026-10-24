// Мост Unity ↔ Poki SDK (https://sdk.poki.com/html5). SDK подключает шаблон WebGLTemplates/Poki
// (или официальный шаблон Poki). Все вызовы защищены: без SDK (локальный запуск, блокировщик рекламы)
// игра работает, а колбэки вызываются сразу.
mergeInto(LibraryManager.library, {

  PK_Init: function (onReady) {
    var done = false;
    var ready = function () { if (done) return; done = true; {{{ makeDynCall('v', 'onReady') }}}(); };
    try {
      if (typeof PokiSDK === 'undefined') { ready(); return; }
      PokiSDK.init().then(ready).catch(function (e) { console.warn('[Genesis] PokiSDK.init:', e); ready(); });
      // Отладочные события видны в консоли только на localhost / в Poki Inspector
      try { if (/localhost|127\.0\.0\.1/.test(location.hostname)) PokiSDK.setDebug(true); } catch (e) { }
    } catch (e) { console.warn('[Genesis] PokiSDK:', e); ready(); }
  },

  PK_LoadingFinished: function () {
    try { if (typeof PokiSDK !== 'undefined') PokiSDK.gameLoadingFinished(); } catch (e) { console.warn(e); }
  },

  PK_GameplayStart: function () {
    try { if (typeof PokiSDK !== 'undefined') PokiSDK.gameplayStart(); } catch (e) { console.warn(e); }
  },

  PK_GameplayStop: function () {
    try { if (typeof PokiSDK !== 'undefined') PokiSDK.gameplayStop(); } catch (e) { console.warn(e); }
  },

  // Рекламная пауза. onDone вызывается один раз: после рекламы, если Poki решил её не показывать, или при ошибке.
  PK_CommercialBreak: function (onDone) {
    var called = false;
    var finish = function () { if (called) return; called = true; {{{ makeDynCall('v', 'onDone') }}}(); };
    try {
      if (typeof PokiSDK === 'undefined') { finish(); return; }
      PokiSDK.commercialBreak().then(finish).catch(function (e) { console.warn('[Genesis] commercialBreak:', e); finish(); });
    } catch (e) { console.warn(e); finish(); }
  },

  // Язык браузера (у Poki нет своего API языка)
  PK_BrowserLang: function () {
    var lang = '';
    try { lang = (navigator.languages && navigator.languages[0]) || navigator.language || ''; } catch (e) { }
    var size = lengthBytesUTF8(lang) + 1;
    var buf = _malloc(size);
    stringToUTF8(lang, buf, size);
    return buf;
  },

  // На какой площадке запущена сборка: 'poki', 'yandex' или ''
  GP_Portal: function () {
    var p = '';
    try {
      if (window.GenesisPortal) p = String(window.GenesisPortal);
      else if (typeof PokiSDK !== 'undefined') p = 'poki';
      else if (window.GenesisYandex) p = 'yandex';
    } catch (e) { }
    var size = lengthBytesUTF8(p) + 1;
    var buf = _malloc(size);
    stringToUTF8(p, buf, size);
    return buf;
  }
});
