using System.IO;

namespace Genesis.Gameplay
{
    /// <summary>
    /// Настройки мира: задаются при создании, хранятся в world.dat, хост может поменять их в меню паузы.
    /// </summary>
    public sealed class WorldSettings
    {
        /// <summary>Креатив (выдача предметов, полёт, перескок эпох). По умолчанию выключен.</summary>
        public bool Creative;
        /// <summary>Креатив и для гостей коопа; иначе — только у хоста.</summary>
        public bool CreativeForGuests;
        /// <summary>Враждебные существа на поверхности ночью. Выключено — только пещеры и руины (как в ТЗ).</summary>
        public bool SurfaceEnemies = true;
        /// <summary>Смерть без потери вещей (останки не появляются).</summary>
        public bool KeepInventory;
        /// <summary>Множитель урона существ: 0.5 — лёгкая, 1 — обычная, 1.5 — сложная.</summary>
        public float Difficulty = 1f;

        private const int Version = 1;

        public WorldSettings Clone() => (WorldSettings)MemberwiseClone();

        public void Write(BinaryWriter w)
        {
            w.Write(Version);
            w.Write(Creative);
            w.Write(CreativeForGuests);
            w.Write(SurfaceEnemies);
            w.Write(KeepInventory);
            w.Write(Difficulty);
        }

        public static WorldSettings Read(BinaryReader r)
        {
            r.ReadInt32();
            return new WorldSettings
            {
                Creative = r.ReadBoolean(),
                CreativeForGuests = r.ReadBoolean(),
                SurfaceEnemies = r.ReadBoolean(),
                KeepInventory = r.ReadBoolean(),
                Difficulty = r.ReadSingle()
            };
        }
    }
}
