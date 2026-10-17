using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Steamworks;
using UnityEngine;

namespace Genesis.Modding.Steam
{
    /// <summary>
    /// Steam Workshop через Steamworks.NET. Сборка компилируется, только если установлен пакет
    /// com.rlabrecque.steamworks.net (или задан символ GENESIS_STEAMWORKS).
    /// Для запуска нужен steam_appid.txt с AppID игры рядом с исполняемым файлом (в редакторе — в корне проекта).
    /// </summary>
    public sealed class SteamWorkshop : IWorkshopProvider
    {
        private readonly bool _initialized;
        private CallResult<CreateItemResult_t> _createResult;
        private CallResult<SubmitItemUpdateResult_t> _submitResult;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            var ws = new SteamWorkshop();
            if (ws.Available) ModLoader.Workshop = ws;
            Application.quitting += () =>
            {
                if (ws._initialized) SteamAPI.Shutdown();
            };
        }

        private SteamWorkshop()
        {
            try
            {
                _initialized = SteamAPI.Init();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Workshop] Steam API unavailable: " + e.Message);
                _initialized = false;
            }
            if (!_initialized) Debug.Log("[Workshop] Steam is not running — Workshop disabled");
        }

        public bool Available => _initialized;

        public void Update()
        {
            if (_initialized) SteamAPI.RunCallbacks();
        }

        public IEnumerable<string> InstalledFolders()
        {
            var result = new List<string>();
            if (!_initialized) return result;
            uint count = SteamUGC.GetNumSubscribedItems();
            if (count == 0) return result;
            var ids = new PublishedFileId_t[count];
            count = SteamUGC.GetSubscribedItems(ids, count);
            for (int i = 0; i < count; i++)
            {
                if (SteamUGC.GetItemInstallInfo(ids[i], out ulong _, out string folder, 1024, out uint _) && Directory.Exists(folder))
                    result.Add(folder);
            }
            return result;
        }

        public void Publish(ModInfo mod, string changeNote, Action<string> done)
        {
            if (!_initialized)
            {
                done?.Invoke("Steam is not running");
                return;
            }
            if (mod.Manifest.WorkshopId != 0)
            {
                Submit(mod, new PublishedFileId_t(mod.Manifest.WorkshopId), changeNote, done);
                return;
            }
            var call = SteamUGC.CreateItem(SteamUtils.GetAppID(), EWorkshopFileType.k_EWorkshopFileTypeCommunity);
            _createResult = CallResult<CreateItemResult_t>.Create((res, ioFailure) =>
            {
                if (ioFailure || res.m_eResult != EResult.k_EResultOK)
                {
                    done?.Invoke("CreateItem failed: " + res.m_eResult);
                    return;
                }
                mod.Manifest.WorkshopId = res.m_nPublishedFileId.m_PublishedFileId;
                SaveManifest(mod);
                Submit(mod, res.m_nPublishedFileId, changeNote, done);
            });
            _createResult.Set(call);
        }

        private void Submit(ModInfo mod, PublishedFileId_t id, string changeNote, Action<string> done)
        {
            var handle = SteamUGC.StartItemUpdate(SteamUtils.GetAppID(), id);
            SteamUGC.SetItemTitle(handle, mod.Manifest.Name ?? mod.Id);
            SteamUGC.SetItemDescription(handle, mod.Manifest.Description ?? "");
            SteamUGC.SetItemContent(handle, Path.GetFullPath(mod.Folder));
            string preview = Path.Combine(mod.Folder, "preview.png");
            if (File.Exists(preview)) SteamUGC.SetItemPreview(handle, Path.GetFullPath(preview));
            var call = SteamUGC.SubmitItemUpdate(handle, changeNote ?? "");
            _submitResult = CallResult<SubmitItemUpdateResult_t>.Create((res, ioFailure) =>
            {
                done?.Invoke(ioFailure || res.m_eResult != EResult.k_EResultOK ? "Upload failed: " + res.m_eResult : null);
            });
            _submitResult.Set(call);
        }

        private static void SaveManifest(ModInfo mod)
        {
            try
            {
                File.WriteAllText(Path.Combine(mod.Folder, "mod.json"), JsonConvert.SerializeObject(mod.Manifest, Formatting.Indented));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Workshop] could not save mod.json: " + e.Message);
            }
        }
    }
}
