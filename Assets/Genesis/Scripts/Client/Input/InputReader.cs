using UnityEngine;

namespace Genesis.Client
{
    /// <summary>
    /// Снимок ввода за кадр. Сейчас — клавиатура/мышь через старый Input Manager;
    /// геймпад добавится здесь же (Input System), остальной код от источника ввода не зависит.
    /// </summary>
    public struct InputSnapshot
    {
        public float Forward, Strafe;
        public float LookX, LookY;
        public bool Jump, Sprint, Descend, Sneak;
        public bool Attack, AttackDown, Use, UseDown;
        public bool Rotate, ToggleFly, Inventory, Creative, Save, Escape, Debug, Knowledge, Stats, Recipes;
        public bool Drop, DropAll, Map, MapZoomIn, MapZoomOut;
        public int HotbarKey;   // -1 нет, иначе 0..8
        public int Scroll;      // -1, 0, +1
    }

    public static class InputReader
    {
        public static float MouseSensitivity = 2.2f;

        public static InputSnapshot Read()
        {
            var s = new InputSnapshot { HotbarKey = -1 };
            s.Forward = (Input.GetKey(KeyCode.W) ? 1 : 0) - (Input.GetKey(KeyCode.S) ? 1 : 0);
            s.Strafe = (Input.GetKey(KeyCode.D) ? 1 : 0) - (Input.GetKey(KeyCode.A) ? 1 : 0);
            s.LookX = Input.GetAxisRaw("Mouse X") * MouseSensitivity;
            s.LookY = Input.GetAxisRaw("Mouse Y") * MouseSensitivity;
            s.Jump = Input.GetKey(KeyCode.Space);
            s.Sprint = Input.GetKey(KeyCode.LeftControl);
            s.Descend = Input.GetKey(KeyCode.LeftShift);
            s.Sneak = Input.GetKey(KeyCode.LeftShift);
            s.Attack = Input.GetMouseButton(0);
            s.AttackDown = Input.GetMouseButtonDown(0);
            s.Use = Input.GetMouseButton(1);
            s.UseDown = Input.GetMouseButtonDown(1);
            s.Rotate = Input.GetKeyDown(KeyCode.R);
            s.ToggleFly = Input.GetKeyDown(KeyCode.F);
            s.Inventory = Input.GetKeyDown(KeyCode.E);
            s.Creative = Input.GetKeyDown(KeyCode.C);
            s.Save = Input.GetKeyDown(KeyCode.F5);
            s.Escape = Input.GetKeyDown(KeyCode.Escape);
            s.Debug = Input.GetKeyDown(KeyCode.F3);
            s.Knowledge = Input.GetKeyDown(KeyCode.K);
            s.Stats = Input.GetKeyDown(KeyCode.P);
            s.Recipes = Input.GetKeyDown(KeyCode.J);
            s.Drop = Input.GetKeyDown(KeyCode.Q);
            s.DropAll = s.Drop && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
            s.Map = Input.GetKeyDown(KeyCode.M);
            s.MapZoomIn = Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus);
            s.MapZoomOut = Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus);
            for (int i = 0; i < 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) s.HotbarKey = i;

            // Печатают в поле ввода (поиск в справочнике, ME) — горячие клавиши не срабатывают
            if (GUIUtility.keyboardControl != 0)
            {
                s.Rotate = s.ToggleFly = s.Inventory = s.Creative = s.Knowledge = s.Stats = s.Recipes = false;
                s.Drop = s.DropAll = s.Map = s.MapZoomIn = s.MapZoomOut = false;
                s.HotbarKey = -1;
            }
            float wheel = Input.mouseScrollDelta.y;
            s.Scroll = wheel > 0.01f ? -1 : (wheel < -0.01f ? 1 : 0);
            return s;
        }
    }
}
