using Genesis.Modding;
using Genesis.Simulation;

namespace TrashCan
{
    /// <summary>Точка входа мода: ищется загрузчиком во всех DLL папки мода.</summary>
    public sealed class TrashCanMod : IGenesisMod
    {
        public void OnLoad(IModContext ctx)
        {
            // Логика для машин с "behavior": "trash_can:void"
            ctx.RegisterBehavior("trash_can:void", new VoidBehavior());

            // Событие игры: считать сломанные блоки
            int broken = 0;
            ctx.Events.BlockBroken += (server, player, pos, block) =>
            {
                if (++broken % 100 == 0) ctx.Log($"{player.Name} broke {broken} blocks");
            };
            ctx.Log("loaded");
        }
    }

    /// <summary>Уничтожает всё, что попало во входные слоты.</summary>
    public sealed class VoidBehavior : IMachineBehavior
    {
        public void Tick(MachineInstance m, Factory f)
        {
            m.Activity = m.Input.IsEmpty ? 0f : 1f;
            m.Status = MachineStatus.Idle;
            for (int i = 0; i < m.Input.Size; i++)
            {
                var st = m.Input[i];
                if (st.IsEmpty) continue;
                f.Stats.Consumed(st.Item, st.Count);
                m.Input[i] = ItemStack.Empty;
            }
        }
    }
}
