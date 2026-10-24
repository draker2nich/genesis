using System;
using System.Collections.Generic;
using System.IO;

namespace Genesis.Core
{
    /// <summary>
    /// Надёжная запись сохранений:
    /// <list type="bullet">
    /// <item>файл пишется во временный <c>.tmp</c>, сбрасывается на диск и подменяет старый атомарно
    /// (<see cref="File.Replace(string,string,string)"/>); прежняя версия остаётся как <c>.bak</c>;</item>
    /// <item>нет момента, когда файла нет вовсе (раньше было Delete → Move: падение между ними = потеря);</item>
    /// <item>чтение с откатом на <c>.bak</c>, если основной файл повреждён.</item>
    /// </list>
    /// </summary>
    public static class SafeFile
    {
        public const string TmpExt = ".tmp";
        public const string BakExt = ".bak";

        public static byte[] Serialize(Action<BinaryWriter> write)
        {
            using (var ms = new MemoryStream())
            {
                using (var w = new BinaryWriter(ms))
                {
                    write(w);
                    w.Flush();
                    return ms.ToArray();
                }
            }
        }

        /// <summary>Записать данные во временный файл рядом с path и сбросить их на диск.</summary>
        public static string WriteTemp(string path, byte[] data)
        {
            string tmp = path + TmpExt;
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            return tmp;
        }

        /// <summary>Атомарно записать файл; предыдущая версия сохраняется как .bak.</summary>
        public static void WriteAllBytes(string path, byte[] data)
        {
            Commit(WriteTemp(path, data), path);
        }

        public static void Write(string path, Action<BinaryWriter> write) => WriteAllBytes(path, Serialize(write));

        /// <summary>Подменить path готовым tmp. Старое содержимое → .bak.</summary>
        public static void Commit(string tmp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }
            string bak = path + BakExt;
            try
            {
                File.Replace(tmp, path, bak, true);
            }
            catch (Exception e) when (e is PlatformNotSupportedException || e is IOException || e is UnauthorizedAccessException)
            {
                // Файловая система без атомарной подмены: сначала копия старого в .bak, потом перезапись
                // копированием — основной файл существует в любой момент.
                File.Copy(path, bak, true);
                File.Copy(tmp, path, true);
                File.Delete(tmp);
            }
        }

        public static bool Exists(string path) => File.Exists(path) || File.Exists(path + BakExt);

        // ============================================================ Сжатие (с запасным вариантом для браузера)

        /// <summary>Поток записи: GZip, если платформа умеет, иначе без сжатия (WebGL).</summary>
        public static Stream CompressWriter(Stream target, bool leaveOpen)
        {
            if (Platform.Compression)
                return new System.IO.Compression.GZipStream(target, System.IO.Compression.CompressionLevel.Fastest, leaveOpen);
            return leaveOpen ? new NonClosingStream(target) : target;
        }

        /// <summary>
        /// Поток чтения: сам определяет, сжат ли файл (сигнатура GZip 1F 8B). Файлы без сжатия читаются везде,
        /// сжатые — там, где есть zlib.
        /// </summary>
        public static Stream DecompressReader(Stream source)
        {
            int b0 = source.ReadByte(), b1 = source.ReadByte();
            source.Seek(0, SeekOrigin.Begin);
            bool gzip = b0 == 0x1F && b1 == 0x8B;
            if (!gzip) return source;
            if (!Platform.Compression) throw new NotSupportedException("compressed save can't be read on this platform");
            return new System.IO.Compression.GZipStream(source, System.IO.Compression.CompressionMode.Decompress);
        }

        /// <summary>Обёртка, которая не закрывает внутренний поток (аналог leaveOpen для записи без сжатия).</summary>
        private sealed class NonClosingStream : Stream
        {
            private readonly Stream _s;
            public NonClosingStream(Stream s) { _s = s; }
            public override bool CanRead => _s.CanRead;
            public override bool CanSeek => _s.CanSeek;
            public override bool CanWrite => _s.CanWrite;
            public override long Length => _s.Length;
            public override long Position { get => _s.Position; set => _s.Position = value; }
            public override void Flush() => _s.Flush();
            public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => _s.Seek(offset, origin);
            public override void SetLength(long value) => _s.SetLength(value);
            public override void Write(byte[] buffer, int offset, int count) => _s.Write(buffer, offset, count);
            protected override void Dispose(bool disposing) => _s.Flush();
        }

        /// <summary>
        /// Прочитать файл; если он повреждён (исключение при чтении) — прочитать .bak. reset вызывается перед
        /// каждой попыткой, чтобы недочитанные данные первой попытки не смешались со второй.
        /// Возвращает false, если не удалось ни то, ни другое (или файлов нет).
        /// </summary>
        public static bool TryRead(string path, Action<BinaryReader> read, Action reset = null)
        {
            bool any = false;
            foreach (var candidate in new[] { path, path + BakExt })
            {
                if (!File.Exists(candidate)) continue;
                any = true;
                try
                {
                    reset?.Invoke();
                    using (var r = new BinaryReader(File.OpenRead(candidate)))
                        read(r);
                    if (candidate != path) Log.Warn($"{Path.GetFileName(path)} was damaged, restored from backup");
                    return true;
                }
                catch (Exception e)
                {
                    Log.Error($"Failed to read {candidate}: {e.Message}");
                }
            }
            if (any) reset?.Invoke();
            return false;
        }

        /// <summary>Отложить повреждённый файл в сторону (никогда не перезаписывать то, что не прочиталось).</summary>
        public static string Quarantine(string path)
        {
            if (!File.Exists(path)) return null;
            string dst = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try
            {
                File.Move(path, dst);
                return dst;
            }
            catch (Exception e)
            {
                Log.Error($"Could not move damaged file {path}: {e.Message}");
                return null;
            }
        }

        // ============================================================ Резервные копии

        /// <summary>
        /// Ротация копий файлов сохранения: backups/1 — самая свежая, keep штук. Не чаще minInterval.
        /// Регион-файлы не копируются (их защищают .bak и откат при чтении).
        /// </summary>
        public static void RotateBackups(string dir, IList<string> names, int keep, TimeSpan minInterval)
        {
            string root = Path.Combine(dir, "backups");
            string newest = Path.Combine(root, "1");
            string stamp = Path.Combine(newest, "time.txt");
            if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < minInterval) return;

            Directory.CreateDirectory(root);
            string oldest = Path.Combine(root, keep.ToString());
            if (Directory.Exists(oldest)) Directory.Delete(oldest, true);
            for (int i = keep - 1; i >= 1; i--)
            {
                string from = Path.Combine(root, i.ToString());
                if (Directory.Exists(from)) Directory.Move(from, Path.Combine(root, (i + 1).ToString()));
            }
            Directory.CreateDirectory(newest);
            foreach (var name in names)
            {
                string src = Path.Combine(dir, name);
                if (File.Exists(src)) File.Copy(src, Path.Combine(newest, name), true);
            }
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
        }
    }

    /// <summary>
    /// Согласованная запись нескольких файлов сохранения (мир, фабрика, игроки, знания, предметы):
    /// все пишутся во временные файлы, затем журнал фиксирует «коммит», затем файлы подменяются.
    /// Упали посередине подмены — при следующей загрузке <see cref="Recover"/> доводит её до конца;
    /// упали до журнала — остаются прежние файлы целиком. Так не бывает «сундук уже новый, игрок ещё старый» (дюп).
    /// </summary>
    public sealed class SaveTransaction
    {
        public const string JournalName = "save.journal";

        private readonly string _dir;
        private readonly List<string> _names = new List<string>();
        private readonly List<byte[]> _data = new List<byte[]>();

        public SaveTransaction(string dir)
        {
            _dir = dir;
        }

        public IReadOnlyList<string> Names => _names;

        /// <summary>Добавить файл (сериализуется сразу — можно вызывать в главном потоке, а Commit — в фоне).</summary>
        public void Add(string name, Action<BinaryWriter> write)
        {
            _names.Add(name);
            _data.Add(SafeFile.Serialize(write));
        }

        public void Commit()
        {
            Directory.CreateDirectory(_dir);
            for (int i = 0; i < _names.Count; i++)
                SafeFile.WriteTemp(Path.Combine(_dir, _names[i]), _data[i]);

            string journal = Path.Combine(_dir, JournalName);
            string journalTmp = journal + SafeFile.TmpExt;
            File.WriteAllLines(journalTmp, _names);
            if (File.Exists(journal)) File.Delete(journal);
            File.Move(journalTmp, journal);

            foreach (var name in _names)
            {
                string path = Path.Combine(_dir, name);
                SafeFile.Commit(path + SafeFile.TmpExt, path);
            }
            File.Delete(journal);
        }

        /// <summary>Довести прерванное сохранение до конца (или убрать недописанные временные файлы).</summary>
        public static void Recover(string dir)
        {
            if (dir == null || !Directory.Exists(dir)) return;
            string journal = Path.Combine(dir, JournalName);
            if (File.Exists(journal))
            {
                foreach (var name in File.ReadAllLines(journal))
                {
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string path = Path.Combine(dir, name);
                    string tmp = path + SafeFile.TmpExt;
                    if (File.Exists(tmp)) SafeFile.Commit(tmp, path);
                }
                File.Delete(journal);
                Log.Warn("Previous save was interrupted, finished it");
            }
            foreach (var tmp in Directory.GetFiles(dir, "*" + SafeFile.TmpExt))
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
    }
}
