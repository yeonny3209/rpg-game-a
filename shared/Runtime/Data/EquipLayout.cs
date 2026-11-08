namespace Game.Shared.Data
{
    /// <summary>
    /// Equipment slots (spec vol.4 2.1 확정: 무기 1, 투구·갑옷·장갑·신발 각 1, 목걸이 1, 반지 2, 아티팩트 1~3).
    /// Index = slot number stored in the DB and sent in InventorySync (container Equipment).
    /// </summary>
    public static class EquipLayout
    {
        public static readonly string[] SlotKeys =
        {
            "weapon", "helmet", "armor", "gloves", "boots", "necklace", "ring", "ring", "artifact", "artifact", "artifact",
        };

        public static readonly string[] SlotLabels =
        {
            "무기", "투구", "갑옷", "장갑", "신발", "목걸이", "반지 1", "반지 2", "아티팩트 1", "아티팩트 2", "아티팩트 3",
        };

        public const int Weapon = 0;
        public static int Count => SlotKeys.Length;

        /// <summary>First slot index that accepts this item's equipSlot, or -1.</summary>
        public static int FirstSlotFor(string equipSlot)
        {
            if (string.IsNullOrEmpty(equipSlot)) return -1;
            for (int i = 0; i < SlotKeys.Length; i++) if (SlotKeys[i] == equipSlot) return i;
            return -1;
        }
    }
}
