// Мост Unity ↔ Yandex Games SDK. SDK инициализируется в index.html (шаблон WebGLTemplates/Yandex),
// объект window.GenesisYandex хранит ysdk и очередь вызовов, пришедших раньше инициализации.
mergeInto(LibraryManager.library, {

  // Игра загрузилась и готова к игре (главное меню показано) — LoadingAPI.ready()
  YG_Ready: function () {
    window.GenesisYandex && window.GenesisYandex.call(function (ysdk) {
      if (ysdk.features && ysdk.features.LoadingAPI) ysdk.features.LoadingAPI.ready();
    });
  },

  // Разметка геймплея — GameplayAPI.start()/stop()
  YG_GameplayStart: function () {
    window.GenesisYandex && window.GenesisYandex.call(function (ysdk) {
      if (ysdk.features && ysdk.features.GameplayAPI) ysdk.features.GameplayAPI.start();
    });
  },

  YG_GameplayStop: function () {
    window.GenesisYandex && window.GenesisYandex.call(function (ysdk) {
      if (ysdk.features && ysdk.features.GameplayAPI) ysdk.features.GameplayAPI.stop();
    });
  },

  // Полноэкранная реклама. По закрытию (или ошибке) Unity получает OnYandexAdClosed("1"/"0").
  YG_ShowFullscreenAd: function () {
    var send = function (shown) {
      if (window.unityInstance) window.unityInstance.SendMessage('Genesis', 'OnYandexAdClosed', shown ? '1' : '0');
    };
    if (!window.GenesisYandex || !window.GenesisYandex.ysdk) { send(false); return; }
    window.GenesisYandex.ysdk.adv.showFullscreenAdv({
      callbacks: {
        onOpen: function () {
          if (window.unityInstance) window.unityInstance.SendMessage('Genesis', 'OnYandexAdOpened');
        },
        onClose: function (wasShown) { send(!!wasShown); },
        onError: function () { send(false); }
      }
    });
  },

  // Язык интерфейса Яндекс Игр (ru, en, tr…) или пустая строка, если SDK ещё не готов
  YG_GetLang: function () {
    var lang = '';
    try {
      var y = window.GenesisYandex && window.GenesisYandex.ysdk;
      if (y && y.environment && y.environment.i18n) lang = y.environment.i18n.lang || '';
    } catch (e) { }
    var size = lengthBytesUTF8(lang) + 1;
    var buf = _malloc(size);
    stringToUTF8(lang, buf, size);
    return buf;
  },

  YG_IsReady: function () {
    return (window.GenesisYandex && window.GenesisYandex.ysdk) ? 1 : 0;
  },

  // Сбросить файловую систему (persistentDataPath) в IndexedDB: без этого сохранение пропадёт после F5
  YG_SyncFS: function () {
    try {
      FS.syncfs(false, function (err) { if (err) console.warn('[Genesis] syncfs: ' + err); });
    } catch (e) { console.warn('[Genesis] syncfs failed: ' + e); }
  }
});
