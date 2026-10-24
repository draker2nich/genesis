// Мост Unity ↔ браузер для WebGL-сборки (см. Genesis.Client.WebPlatform).
mergeInto(LibraryManager.library, {

  // Язык браузера (navigator.language), например "ru-RU"
  GN_BrowserLang: function () {
    var lang = '';
    try { lang = (navigator.languages && navigator.languages[0]) || navigator.language || ''; } catch (e) { }
    var size = lengthBytesUTF8(lang) + 1;
    var buf = _malloc(size);
    stringToUTF8(lang, buf, size);
    return buf;
  },

  // Сбросить файловую систему (persistentDataPath) в IndexedDB: без этого сохранение пропадёт после F5
  GN_SyncFS: function () {
    try {
      FS.syncfs(false, function (err) { if (err) console.warn('[Genesis] syncfs: ' + err); });
    } catch (e) { console.warn('[Genesis] syncfs failed: ' + e); }
  }
});
